using System.IO;
using Microsoft.Win32;
using OpenClean.Models;
using OpenClean.Services.Integrity;

namespace OpenClean.Services;

/// <summary>
/// Agent 3: Liest Autostart-Einträge (Registry-Run-Keys + Autostart-Ordner),
/// ermittelt Status/Impact und aktiviert/deaktiviert Einträge über die
/// StartupApproved-Keys – exakt der Weg, den auch der Windows-Task-Manager nutzt.
/// Es werden KEINE Einträge gelöscht.
///
/// Robustheit: Jede Quelle und jeder einzelne Eintrag ist gekapselt, sodass ein
/// fehlerhafter Wert oder eine unlesbare Quelle die übrigen nicht verschluckt.
/// Registry-Run-Keys werden für 64- UND 32-Bit-Views explizit gelesen, unabhängig
/// von der Prozess-Bitness.
/// </summary>
public sealed class StartupService
{
    // Pfade sind view-relativ: Registry32 leitet automatisch nach Wow6432Node um,
    // daher wird KEIN expliziter Wow6432Node-Pfad benötigt.
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string PoliciesRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    private const string PackagedAppsPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private static readonly byte[] EnabledValue = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    // Lazy gecachte Maps aus dem PackageManager: PFN -> DisplayName bzw. -> PublisherDisplayName.
    private Dictionary<string, string>? _packageDisplayNames;
    private Dictionary<string, string>? _packagePublishers;

    private Dictionary<string, string> PackageDisplayNames
    {
        get { EnsurePackageMaps(); return _packageDisplayNames!; }
    }

    private Dictionary<string, string> PackagePublishers
    {
        get { EnsurePackageMaps(); return _packagePublishers!; }
    }

    public IReadOnlyList<StartupEntry> GetEntries()
    {
        var entries = new List<StartupEntry>();

        // ---- Registry-Run-Keys (explizite Views: 64-Bit UND 32-Bit) ----
        // HKCU Run – 64-Bit und 32-Bit (HKCU ist meist nicht umgeleitet -> Dedupe entfernt Dubletten)
        Safe(() => ReadRunKey(entries, RegistryHive.CurrentUser, RegistryView.Registry64, RunKeyPath,
            StartupLocation.HkcuRun, RegistryHive.CurrentUser, ApprovedRun));
        Safe(() => ReadRunKey(entries, RegistryHive.CurrentUser, RegistryView.Registry32, RunKeyPath,
            StartupLocation.HkcuRun, RegistryHive.CurrentUser, ApprovedRun));

        // HKLM Run – 64-Bit
        Safe(() => ReadRunKey(entries, RegistryHive.LocalMachine, RegistryView.Registry64, RunKeyPath,
            StartupLocation.HklmRun, RegistryHive.LocalMachine, ApprovedRun));
        // HKLM Run – 32-Bit (= Wow6432Node)
        Safe(() => ReadRunKey(entries, RegistryHive.LocalMachine, RegistryView.Registry32, RunKeyPath,
            StartupLocation.HklmRunWow6432, RegistryHive.LocalMachine, ApprovedRun32));

        // ---- RunOnce (kein StartupApproved -> immer als aktiviert gewertet) ----
        Safe(() => ReadRunKey(entries, RegistryHive.CurrentUser, RegistryView.Registry64, RunOnceKeyPath,
            StartupLocation.HkcuRunOnce, RegistryHive.CurrentUser, ApprovedRun));
        Safe(() => ReadRunKey(entries, RegistryHive.LocalMachine, RegistryView.Registry64, RunOnceKeyPath,
            StartupLocation.HklmRunOnce, RegistryHive.LocalMachine, ApprovedRun));

        // ---- Policy-Run-Keys (per Gruppenrichtlinie, nicht schaltbar) ----
        // Policies werden nicht per WOW64 umgeleitet -> Registry64 genügt.
        Safe(() => ReadPolicyRunKey(entries, RegistryHive.LocalMachine, PoliciesRunKeyPath,
            StartupLocation.HklmPoliciesRun));
        Safe(() => ReadPolicyRunKey(entries, RegistryHive.CurrentUser, PoliciesRunKeyPath,
            StartupLocation.HkcuPoliciesRun));

        // ---- UWP-/Store-App-Startup-Tasks ----
        Safe(() => ReadPackagedApps(entries));

        // ---- Autostart-Ordner ----
        Safe(() => ReadStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupLocation.StartupFolderUser));
        Safe(() => ReadStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            StartupLocation.StartupFolderCommon));

        return Deduplicate(entries);
    }

    /// <summary>Führt eine Quelle aus; ein Fehler darf die übrigen Quellen nicht abbrechen.</summary>
    private static void Safe(Action source)
    {
        try { source(); }
        catch { /* Quelle nicht lesbar -> ignorieren, restliche Quellen laufen weiter */ }
    }

    /// <summary>Entfernt Dubletten (z. B. gleiche HKCU-Werte aus 64-/32-Bit-View) über (Location, Name).</summary>
    private static List<StartupEntry> Deduplicate(List<StartupEntry> entries)
    {
        var seen = new HashSet<(StartupLocation, string, string)>();
        var result = new List<StartupEntry>(entries.Count);
        foreach (var e in entries)
        {
            var key = (e.Location, e.Name.ToLowerInvariant(), e.Command.ToLowerInvariant());
            if (seen.Add(key))
                result.Add(e);
        }
        return result;
    }

    private void ReadRunKey(List<StartupEntry> entries, RegistryHive hive, RegistryView view, string subPath,
        StartupLocation location, RegistryHive approvedHive, string approvedPath)
    {
        // RunOnce wird von Windows NICHT über StartupApproved geschaltet: Einträge laufen
        // beim nächsten Start genau einmal und lassen sich hier nicht deaktivieren.
        bool isRunOnce = location is StartupLocation.HkcuRunOnce or StartupLocation.HklmRunOnce;

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subPath);
        if (key is null) return;

        foreach (var name in key.GetValueNames())
        {
            // Jeder Eintrag einzeln gekapselt: ein defekter Wert darf den Rest nicht verschlucken.
            try
            {
                if (string.IsNullOrEmpty(name)) continue; // (Standard)-Wert überspringen
                string command = key.GetValue(name)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(command)) continue;

                string? exe = ResolveExecutable(command);
                var (enabled, approvedData) = isRunOnce
                    ? (true, (byte[]?)null)
                    : ReadApproved(approvedHive, approvedPath, name);

                var entry = new StartupEntry
                {
                    Name = name,
                    Command = command,
                    ExecutablePath = exe,
                    Location = location,
                    IsEnabled = enabled,
                    CanToggle = !isRunOnce,
                    Publisher = PublisherForExe(exe)
                };
                AssignImpact(entry, approvedData);
                entries.Add(entry);
            }
            catch { /* einzelner Eintrag defekt -> überspringen */ }
        }
    }

    /// <summary>
    /// Liest per Gruppenrichtlinie gesetzte Run-Keys. Diese lassen sich nicht über
    /// StartupApproved schalten: immer aktiviert, CanToggle=false. Value-Namen sind
    /// meist numerisch ("1","2",…); dann wird der Name aus dem EXE-Dateinamen abgeleitet.
    /// </summary>
    private void ReadPolicyRunKey(List<StartupEntry> entries, RegistryHive hive, string subPath,
        StartupLocation location)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subPath);
        if (key is null) return;

        foreach (var valueName in key.GetValueNames())
        {
            try
            {
                if (string.IsNullOrEmpty(valueName)) continue; // (Standard)-Wert überspringen
                string command = key.GetValue(valueName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(command)) continue;

                string? exe = ResolveExecutable(command);

                // Bei rein numerischem Value-Namen einen lesbaren Namen aus der EXE ableiten.
                string name = valueName;
                if (long.TryParse(valueName, out _))
                {
                    name = exe is not null
                        ? Path.GetFileNameWithoutExtension(exe)
                        : $"Richtlinie {valueName}";
                }

                var entry = new StartupEntry
                {
                    Name = name,
                    Command = command,
                    ExecutablePath = exe,
                    Location = location,
                    IsEnabled = true,
                    CanToggle = false,
                    Publisher = PublisherForExe(exe)
                };
                AssignImpact(entry, null);
                entries.Add(entry);
            }
            catch { /* einzelner Eintrag defekt -> überspringen */ }
        }
    }

    /// <summary>
    /// Liest UWP-/Store-App-Startup-Tasks aus SystemAppData. Für jedes Paket (PackageFamilyName)
    /// werden die Kind-Schlüssel geprüft; ein Kind mit einem REG_DWORD-Wert "State" ist ein
    /// Startup-Task (TaskId = Kind-Schlüsselname). Status: gerade = aktiviert, ungerade = deaktiviert.
    /// </summary>
    private void ReadPackagedApps(List<StartupEntry> entries)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var root = baseKey.OpenSubKey(PackagedAppsPath);
        if (root is null) return;

        foreach (var pfn in root.GetSubKeyNames())
        {
            try
            {
                using var pkgKey = root.OpenSubKey(pfn);
                if (pkgKey is null) continue;

                foreach (var taskId in pkgKey.GetSubKeyNames())
                {
                    try
                    {
                        using var taskKey = pkgKey.OpenSubKey(taskId);
                        // Nur Kinder mit einem State-DWORD sind Startup-Tasks.
                        if (taskKey?.GetValue("State") is not int state) continue;

                        bool enabled = (state & 1) == 0;
                        string displayName = ResolvePackageDisplayName(pfn, taskId);

                        var entry = new StartupEntry
                        {
                            Name = displayName,
                            Command = $"Store-App: {pfn}\\{taskId}",
                            ExecutablePath = null,
                            Location = StartupLocation.PackagedApp,
                            IsEnabled = enabled,
                            CanToggle = true,
                            PackagedStateKeyPath = $@"{PackagedAppsPath}\{pfn}\{taskId}",
                            Impact = StartupImpact.Low,
                            ImpactIsEstimated = true,
                            Publisher = PackagePublishers.TryGetValue(pfn, out var pub) ? pub : ""
                        };
                        entries.Add(entry);
                    }
                    catch { /* einzelner Task defekt -> überspringen */ }
                }
            }
            catch { /* einzelnes Paket defekt -> überspringen */ }
        }
    }

    /// <summary>
    /// Ermittelt den Anzeigenamen: zuerst der echte DisplayName aus dem PackageManager,
    /// sonst eine verbesserte Heuristik aus PackageFamilyName/TaskId.
    /// </summary>
    private string ResolvePackageDisplayName(string pfn, string taskId)
    {
        if (PackageDisplayNames.TryGetValue(pfn, out var real) && !string.IsNullOrWhiteSpace(real))
            return real;

        // Heuristik: Segment zwischen erstem '.' und '_'. Sieht es wie ein Hash aus,
        // stattdessen die TaskId als Basis nehmen.
        string segment = StartupDisplayName.PackageNameSegment(pfn);
        string basis = StartupDisplayName.LooksLikeHash(segment) ? taskId : segment;
        return StartupDisplayName.Prettify(basis);
    }

    /// <summary>
    /// Baut einmalig die Maps PackageFamilyName -> DisplayName und -> PublisherDisplayName über
    /// den Windows-PackageManager (wie der Task-Manager). Bei fehlender/nicht verfügbarer
    /// WinRT-API bleiben die Maps leer und die Heuristik/Fallback greift.
    /// </summary>
    private void EnsurePackageMaps()
    {
        if (_packageDisplayNames is not null) return;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var publishers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();

            IEnumerable<Windows.ApplicationModel.Package> packages;
            try
            {
                packages = manager.FindPackagesForUser(string.Empty);
            }
            catch
            {
                // Fallback (z. B. als Admin): alle Pakete.
                packages = manager.FindPackages();
            }

            foreach (var pkg in packages)
            {
                try
                {
                    string pfn = pkg.Id.FamilyName;
                    if (string.IsNullOrWhiteSpace(pfn)) continue;

                    string name = pkg.DisplayName;
                    if (!string.IsNullOrWhiteSpace(name)
                        && !name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        names[pfn] = name;

                    string publisher = pkg.PublisherDisplayName;
                    if (!string.IsNullOrWhiteSpace(publisher)
                        && !publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        publishers[pfn] = publisher;
                }
                catch { /* einzelnes Package wirft -> überspringen */ }
            }
        }
        catch { /* PackageManager/WinRT nicht verfügbar -> leere Maps */ }

        _packageDisplayNames = names;
        _packagePublishers = publishers;
    }

    /// <summary>Verifizierter Authenticode-Herausgeber einer EXE (gecacht, siehe <see cref="AuthenticodeService"/>).</summary>
    private static string PublisherForExe(string? exePath) => AuthenticodeService.PublisherFor(exePath);

    private void ReadStartupFolder(List<StartupEntry> entries, string folder, StartupLocation location)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        foreach (var file in Directory.GetFiles(folder))
        {
            // Jede Verknüpfung einzeln gekapselt.
            try
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                string? target = ResolveShortcut(file) ?? file;
                // Der Status des gemeinsamen Autostart-Ordners liegt unter HKLM, der des
                // Benutzer-Ordners unter HKCU.
                var approvedHive = location == StartupLocation.StartupFolderCommon
                    ? RegistryHive.LocalMachine
                    : RegistryHive.CurrentUser;
                var (enabled, approvedData) = ReadApproved(approvedHive, ApprovedFolder, fileName);

                string? folderExe = ResolveExecutable(target);
                var entry = new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    Command = target,
                    ExecutablePath = folderExe,
                    Location = location,
                    IsEnabled = enabled,
                    Publisher = PublisherForExe(folderExe)
                };
                AssignImpact(entry, approvedData);
                entries.Add(entry);
            }
            catch { /* einzelner Shortcut defekt -> überspringen */ }
        }
    }

    // ---- Enable / Disable ---------------------------------------------------

    /// <summary>Setzt den Aktiviert-Status über die StartupApproved-Keys.</summary>
    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        // Sperre bei erkannter Manipulation (OPCL-20): keine Registry-Schreibvorgänge.
        // Deckt auch DelayedStartupService mit ab, der hierüber deaktiviert.
        if (IntegrityState.IsBlocked) return;

        // Per Gruppenrichtlinie gesetzte Einträge lassen sich nicht schalten -> nichts tun.
        if (!entry.CanToggle) return;

        // Store-App: State-Wert im Task-Schlüssel setzen (2 = aktiviert, 1 = deaktiviert).
        if (entry.Location == StartupLocation.PackagedApp)
        {
            if (string.IsNullOrEmpty(entry.PackagedStateKeyPath))
                throw new InvalidOperationException(Loc.T("startup.error.noTaskKey"));

            using var pkgBaseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var taskKey = pkgBaseKey.OpenSubKey(entry.PackagedStateKeyPath, writable: true)
                ?? throw new InvalidOperationException(Loc.T("startup.error.taskKeyMissing"));
            taskKey.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
            entry.IsEnabled = enabled;
            return;
        }

        var (hive, approvedPath, valueName) = GetApprovedTarget(entry);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(approvedPath, writable: true);

        byte[] data;
        if (enabled)
        {
            data = (byte[])EnabledValue.Clone();
        }
        else
        {
            // Byte 0 = 0x03 (deaktiviert), Bytes 4..11 = FILETIME des Deaktivierungszeitpunkts.
            data = new byte[12];
            data[0] = 0x03;
            long filetime = DateTime.UtcNow.ToFileTimeUtc();
            BitConverter.GetBytes(filetime).CopyTo(data, 4);
        }

        key.SetValue(valueName, data, RegistryValueKind.Binary);
        entry.IsEnabled = enabled;
    }

    private static (RegistryHive hive, string path, string valueName) GetApprovedTarget(StartupEntry entry)
        => entry.Location switch
        {
            StartupLocation.HkcuRun => (RegistryHive.CurrentUser, ApprovedRun, entry.Name),
            StartupLocation.HklmRun => (RegistryHive.LocalMachine, ApprovedRun, entry.Name),
            StartupLocation.HklmRunWow6432 => (RegistryHive.LocalMachine, ApprovedRun32, entry.Name),
            StartupLocation.StartupFolderUser => (RegistryHive.CurrentUser, ApprovedFolder, entry.Name + ".lnk"),
            StartupLocation.StartupFolderCommon => (RegistryHive.LocalMachine, ApprovedFolder, entry.Name + ".lnk"),
            _ => (RegistryHive.CurrentUser, ApprovedRun, entry.Name)
        };

    // ---- StartupApproved lesen ---------------------------------------------

    private static (bool enabled, byte[]? data) ReadApproved(RegistryHive hive, string path, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key?.GetValue(valueName) is byte[] data && data.Length > 0)
            {
                // Bit 0 des ersten Bytes gesetzt => deaktiviert (0x03), sonst aktiviert (0x02/0x06).
                bool enabled = (data[0] & 0x01) == 0;
                return (enabled, data);
            }
        }
        catch { /* kein Zugriff -> als aktiviert behandeln */ }

        return (true, null);
    }

    // ---- Impact -------------------------------------------------------------

    /// <summary>
    /// Ermittelt den Impact. Enthält der StartupApproved-Blob erweiterte Messdaten
    /// (mehr als die Standard-12 Byte), werden diese interpretiert; andernfalls greift
    /// eine ehrliche Heuristik anhand der EXE-Größe/Herkunft (als geschätzt markiert).
    /// </summary>
    private static void AssignImpact(StartupEntry entry, byte[]? approvedData)
    {
        // Nur ein erweiterter Blob (>= 20 Bytes) enthält ab Offset 12 einen Messwert.
        // Bytes 4..11 sind die FILETIME des Deaktivierungszeitpunkts und dürfen
        // niemals als Dauer interpretiert werden.
        if (approvedData is { Length: >= 20 })
        {
            // Erweiterter Blob: gemessene Zeit (100ns-Einheiten) grob interpretieren.
            long measured = BitConverter.ToInt64(approvedData, 12);
            entry.ImpactIsEstimated = false;
            entry.Impact = measured switch
            {
                > 10_000_000 => StartupImpact.High,   // > 1 s
                > 3_000_000 => StartupImpact.Medium,  // > 0,3 s
                > 0 => StartupImpact.Low,
                _ => EstimateImpact(entry)
            };
            if (entry.Impact == StartupImpact.Unknown) entry.Impact = EstimateImpact(entry);
            return;
        }

        entry.Impact = EstimateImpact(entry);
        entry.ImpactIsEstimated = true;
    }

    private static StartupImpact EstimateImpact(StartupEntry entry)
    {
        string? exe = entry.ExecutablePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return StartupImpact.Low;

        long size;
        try { size = new FileInfo(exe).Length; }
        catch { return StartupImpact.Low; }

        return size switch
        {
            > 50L * 1024 * 1024 => StartupImpact.High,
            > 5L * 1024 * 1024 => StartupImpact.Medium,
            _ => StartupImpact.Low
        };
    }

    // ---- Hilfsfunktionen ----------------------------------------------------

    /// <summary>Extrahiert den EXE-Pfad aus einem (evtl. quotierten, mit Argumenten versehenen) Befehl.</summary>
    private static string? ResolveExecutable(string command)
    {
        var (exe, _) = CommandLine.Split(command);
        return exe.Length > 0 && File.Exists(exe) ? exe : null;
    }

    /// <summary>Löst eine .lnk-Verknüpfung auf ihr Ziel auf (best effort, via WScript.Shell).</summary>
    private static string? ResolveShortcut(string linkPath)
    {
        if (!linkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return linkPath;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            dynamic shortcut = shell.CreateShortcut(linkPath);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }
}
