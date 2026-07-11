using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Agent (Inventar): liest die installierten Programme aus den Windows-Uninstall-
/// Registry-Schlüsseln (HKLM 64-/32-Bit und HKCU) – genau die Quelle, die auch
/// „Apps &amp; Features" verwendet. Bewusst KEIN <c>Win32_Product</c> (WMI): das ist
/// langsam und kann ungewollt MSI-Reparaturen auslösen.
///
/// Reine Leseoperation. Jede Quelle/jeder Eintrag ist gekapselt, damit ein defekter
/// Wert die übrigen nicht verschluckt.
/// </summary>
public sealed class InstalledAppsService
{
    private const string UninstallPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();

        Safe(() => ReadUninstallKey(apps, RegistryHive.LocalMachine, RegistryView.Registry64));
        Safe(() => ReadUninstallKey(apps, RegistryHive.LocalMachine, RegistryView.Registry32));
        Safe(() => ReadUninstallKey(apps, RegistryHive.CurrentUser, RegistryView.Registry64));

        return Deduplicate(apps);
    }

    private static void Safe(Action source)
    {
        try { source(); }
        catch { /* Quelle nicht lesbar -> übrige Quellen laufen weiter */ }
    }

    private static void ReadUninstallKey(List<InstalledApp> apps, RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(UninstallPath);
        if (uninstall is null) return;

        foreach (var subKeyName in uninstall.GetSubKeyNames())
        {
            try
            {
                using var key = uninstall.OpenSubKey(subKeyName);
                if (key is null) continue;

                var app = TryReadApp(key, subKeyName);
                if (app is not null)
                    apps.Add(app);
            }
            catch { /* einzelner Eintrag defekt -> überspringen */ }
        }
    }

    /// <summary>
    /// Liest einen Uninstall-Eintrag. Gibt null zurück, wenn er kein „echtes" Programm
    /// ist (kein Anzeigename, System-Komponente, Update/Patch, oder reiner Elternknoten).
    /// </summary>
    private static InstalledApp? TryReadApp(RegistryKey key, string subKeyName)
    {
        string name = GetString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;

        // System-Komponenten und Windows-Updates/Patches ausblenden (wie „Apps & Features").
        if (GetDword(key, "SystemComponent") == 1) return null;

        string releaseType = GetString(key, "ReleaseType");
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
            return null;

        // Einträge mit ParentKeyName sind i. d. R. Bestandteile/Updates eines Hauptprogramms.
        if (!string.IsNullOrWhiteSpace(GetString(key, "ParentKeyName"))) return null;

        // Windows-Updates heißen oft „... (KB1234567)" ohne Uninstall-Möglichkeit.
        string uninstallString = GetString(key, "UninstallString");
        string quiet = GetString(key, "QuietUninstallString");

        long sizeBytes = GetEstimatedSizeBytes(key);

        // MSI-Produktcode: bei WindowsInstaller-Paketen ist der Schlüsselname der {GUID}.
        string? msiCode = null;
        if (GetDword(key, "WindowsInstaller") == 1 && LooksLikeGuid(subKeyName))
            msiCode = subKeyName;

        string installLocation = GetString(key, "InstallLocation").Trim().Trim('"');

        // Version: Registry-DisplayVersion; fehlt sie (typisch bei Steam-Spielen),
        // als Ersatz die Steam-Build-ID aus dem App-Manifest verwenden.
        string version = GetString(key, "DisplayVersion");
        if (string.IsNullOrWhiteSpace(version))
        {
            string? steamAppId = SteamAppId(subKeyName, uninstallString);
            if (steamAppId is not null)
                version = SteamCatalog.GetBuildId(steamAppId) is { } build ? $"Build {build}" : version;
        }

        return new InstalledApp
        {
            Name = name.Trim(),
            Version = version,
            Publisher = GetString(key, "Publisher"),
            InstallLocation = installLocation,
            IconPath = ResolveIconPath(key),
            SizeFolder = ResolveSizeFolder(installLocation, key),
            EstimatedBytes = sizeBytes,
            InstallDate = ParseInstallDate(GetString(key, "InstallDate")),
            UninstallString = uninstallString,
            QuietUninstallString = quiet,
            MsiProductCode = msiCode
        };
    }

    /// <summary>Entfernt Dubletten (gleicher Name + Version, z. B. aus 64-/32-Bit-View).</summary>
    private static List<InstalledApp> Deduplicate(List<InstalledApp> apps)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<InstalledApp>(apps.Count);
        foreach (var app in apps)
        {
            var id = (app.Name.ToLowerInvariant(), app.Version.ToLowerInvariant());
            if (seen.Add(id))
                result.Add(app);
        }
        return result;
    }

    // Prozessweiter Cache berechneter Ordnergrößen (Key = normalisierter Pfad). Verhindert,
    // dass beim erneuten Laden der Liste dieselben (teuren) Größen neu berechnet werden –
    // die Werte bleiben dadurch erhalten und erscheinen sofort.
    private static readonly ConcurrentDictionary<string, long> FolderSizeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string CacheKey(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Bereits berechnete Größe für einen Pfad, sonst 0 (ohne Neuberechnung).</summary>
    public static long GetCachedFolderSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        return FolderSizeCache.TryGetValue(CacheKey(path), out var size) ? size : 0;
    }

    /// <summary>
    /// Best-effort-Größe eines Installationsordners (Summe aller Dateien, rekursiv), mit Cache.
    /// Fallback, wenn die Registry keine <c>EstimatedSize</c> liefert (z. B. Steam-Spiele).
    /// Läuft potenziell länger → vom Aufrufer im Hintergrund ausführen. 0 bei Fehler.
    /// </summary>
    public static long TryGetFolderSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        path = path.Trim().Trim('"');
        if (!Directory.Exists(path) || !IsSafeSizeFolder(path)) return 0;

        string key = CacheKey(path);
        if (FolderSizeCache.TryGetValue(key, out var cached)) return cached;

        long total = 0;
        try
        {
            // IgnoreInaccessible: gesperrte Unterordner überspringen statt die gesamte
            // Aufzählung abzubrechen (sonst liefern z. B. Spiele-Ordner 0 oder Teilsummen).
            // ReparsePoint: Symlinks/Junctions nicht verfolgen (keine Doppelzählung/Schleifen).
            // AttributesToSkip explizit setzen: der Default würde Hidden/System-Dateien auslassen.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try { total += file.Length; }
                catch { /* gesperrt/nicht lesbar -> überspringen */ }
            }
        }
        catch { /* Zugriff verweigert -> Teilsumme */ }

        if (total > 0) FolderSizeCache[key] = total;
        return total;
    }

    // ---- Größen-Quellen -------------------------------------------------------

    /// <summary>EstimatedSize (KB) → Bytes. DWORD ist vorzeichenlos – große Werte nicht
    /// als negativ interpretieren; manche Installer schreiben die Größe als Text.</summary>
    private static long GetEstimatedSizeBytes(RegistryKey key)
    {
        long kb = key.GetValue("EstimatedSize") switch
        {
            int i => (uint)i,
            long l when l > 0 => l,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 => p,
            _ => 0
        };
        return kb * 1024;
    }

    /// <summary>
    /// Ermittelt den Ordner für die Größenberechnung: die InstallLocation, wenn sie sicher
    /// ist; sonst abgeleitet aus DisplayIcon bzw. dem Uninstaller-Pfad (z. B. Chrome-Apps,
    /// Launcher-Spiele ohne InstallLocation). Leer, wenn nichts Brauchbares gefunden wird.
    /// </summary>
    private static string ResolveSizeFolder(string installLocation, RegistryKey key)
    {
        if (!string.IsNullOrWhiteSpace(installLocation) &&
            Directory.Exists(installLocation) && IsSafeSizeFolder(installLocation))
            return installLocation;

        // DisplayIcon zuerst: zeigt meist auf die Haupt-EXE bzw. eine Datei im App-Ordner
        // (bei Chrome-Web-Apps auf den app-eigenen Icon-Ordner – nicht auf chrome.exe).
        var candidates = new[]
        {
            DisplayIconPath(key),
            ExtractExecutablePath(GetString(key, "QuietUninstallString")),
            ExtractExecutablePath(GetString(key, "UninstallString"))
        };

        foreach (var file in candidates)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(dir) && IsSafeSizeFolder(dir))
                return dir;
        }

        return "";
    }

    /// <summary>
    /// Beste Quelle zum Extrahieren des Programm-Icons: der DisplayIcon-Pfad (ohne Index),
    /// wenn die Datei existiert. Leer, wenn nichts Brauchbares vorliegt (dann Buchstaben-Avatar).
    /// Bewusst KEIN Raten im Installationsordner – das wäre langsam und unzuverlässig.
    /// </summary>
    private static string ResolveIconPath(RegistryKey key)
    {
        string? icon = DisplayIconPath(key);
        return !string.IsNullOrWhiteSpace(icon) && File.Exists(icon) ? icon! : "";
    }

    /// <summary>
    /// Ermittelt die Steam-appid eines Eintrags: aus dem Schlüsselnamen „Steam App &lt;id&gt;"
    /// oder aus einem <c>steam://uninstall/&lt;id&gt;</c>-Kommando. <c>null</c>, wenn kein Steam-Eintrag.
    /// </summary>
    private static string? SteamAppId(string subKeyName, string uninstallString)
    {
        const string prefix = "Steam App ";
        if (subKeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string id = subKeyName[prefix.Length..].Trim();
            if (id.Length > 0 && id.All(char.IsDigit)) return id;
        }

        var m = Regex.Match(uninstallString ?? "", @"steam://uninstall/(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>DisplayIcon-Pfad ohne Anführungszeichen und ohne Icon-Index („…,0").</summary>
    private static string? DisplayIconPath(RegistryKey key)
    {
        string raw = GetString(key, "DisplayIcon").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw)) return null;

        int comma = raw.LastIndexOf(',');
        if (comma > 1 && int.TryParse(raw[(comma + 1)..].Trim(), out _))
            raw = raw[..comma];

        return raw.Trim().Trim('"');
    }

    /// <summary>Extrahiert den EXE-Pfad aus einem Kommando („"C:\…\unins.exe" /SILENT").</summary>
    private static string? ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        if (File.Exists(command)) return command;

        // Unquotiert mit Argumenten: schrittweise am jeweils nächsten Leerzeichen prüfen
        // (Pfade mit Leerzeichen wie "C:\Program Files\…\unins.exe /SILENT").
        int space = command.IndexOf(' ');
        while (space > 0)
        {
            string head = command[..space];
            if (File.Exists(head)) return head;
            space = command.IndexOf(' ', space + 1);
        }
        return null;
    }

    /// <summary>
    /// Schutz vor absurden Größen: keine Laufwerkswurzeln, nichts unterhalb von Windows
    /// und keine Sammel-Wurzeln (Program Files, AppData, ProgramData, Benutzerprofil) –
    /// manche Installer tragen genau solche Pfade als InstallLocation ein.
    /// </summary>
    private static bool IsSafeSizeFolder(string path)
    {
        try
        {
            string full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? root = Path.GetPathRoot(full)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(full) ||
                string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return false;

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(windows) &&
                (full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return false;

            foreach (var folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData
            })
            {
                string p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p) &&
                    full.Equals(p.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch { return false; }
    }

    // ---- Registry-Hilfsfunktionen -------------------------------------------

    private static string GetString(RegistryKey key, string name)
        => key.GetValue(name)?.ToString() ?? "";

    private static int GetDword(RegistryKey key, string name)
        => key.GetValue(name) is int i ? i : 0;

    private static bool LooksLikeGuid(string text)
        => text.StartsWith('{') && text.EndsWith('}') && text.Length >= 34;

    private static DateTime? ParseInstallDate(string raw)
    {
        if (DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return date;
        return null;
    }
}
