using System.IO;
using Microsoft.Win32;
using OpenClean.Models;
using OpenClean.Services.Integrity;

namespace OpenClean.Services;

/// <summary>
/// Liest die Einträge des Windows-Rechtsklick-Menüs (statische Verben + COM-Shell-Extensions)
/// und schaltet sie an/aus. Es wird NICHTS gelöscht – beide Mechanismen sind ein einziger
/// Registry-Wert und jederzeit zurücknehmbar:
///
///   Shell-Extension: CLSID in der Blocked-Liste
///                    (HKLM\…\Shell Extensions\Blocked) => Explorer lädt den Handler nicht.
///   Statisches Verb: Wert "LegacyDisable" im Verb-Schlüssel => Verb erscheint nicht im Menü.
///
/// Gelesen wird die 64-Bit-View: Der Explorer ist ein 64-Bit-Prozess und lädt nur Handler aus
/// dieser View – Wow6432Node-Einträge tauchen im Menü gar nicht auf.
///
/// Robustheit wie im <see cref="StartupService"/>: jede Quelle und jeder Eintrag gekapselt,
/// ein defekter Schlüssel darf die restliche Liste nicht verschlucken.
/// </summary>
public sealed class ContextMenuService
{
    private const string BlockedKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string LegacyDisableValue = "LegacyDisable";
    private const string RecycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>
    /// Windows-Kern-Verben, ohne die der Explorer unbrauchbar wird. Sie werden angezeigt,
    /// lassen sich aber nicht abschalten.
    /// </summary>
    private static readonly HashSet<string> ProtectedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewprocess", "explore", "edit", "runas",
        "cut", "copy", "paste", "delete", "rename", "properties", "link",
        "print", "printto", "find", "pintohome", "empty", "restore"
    };

    /// <summary>HKCR-relativer Basispfad je Ort.</summary>
    private static string BasePath(ContextMenuLocation location) => location switch
    {
        ContextMenuLocation.AllFiles => "*",
        ContextMenuLocation.Directory => "Directory",
        ContextMenuLocation.DirectoryBackground => @"Directory\Background",
        ContextMenuLocation.Folder => "Folder",
        ContextMenuLocation.Drive => "Drive",
        ContextMenuLocation.Desktop => "DesktopBackground",
        ContextMenuLocation.RecycleBin => $@"CLSID\{RecycleBinClsid}",
        _ => throw new ArgumentOutOfRangeException(nameof(location))
    };

    // ---- Lesen --------------------------------------------------------------

    /// <summary>Rohtreffer einer Quelle – vor dem Zusammenführen gleicher Einträge über mehrere Orte.</summary>
    private sealed record Raw(
        string Name, ContextMenuLocation Location, ContextMenuEntryKind Kind, string RegistryPath,
        string? Clsid, string? HandlerPath, string Command, bool IsEnabled, bool CanToggle);

    public IReadOnlyList<ContextMenuEntry> GetEntries()
    {
        var raw = new List<Raw>();
        var blocked = ReadBlockedClsids();

        foreach (ContextMenuLocation location in Enum.GetValues<ContextMenuLocation>())
        {
            Safe(() => ReadStaticVerbs(raw, location));
            Safe(() => ReadShellExtensions(raw, location, blocked));
        }

        return Merge(raw);
    }

    /// <summary>
    /// Führt Registrierungen desselben Eintrags an mehreren Orten zu EINEM Eintrag zusammen.
    /// Shell-Erweiterungen werden über die CLSID gruppiert – die Blocked-Liste wirkt ohnehin
    /// global pro CLSID, drei einzeln schaltbare Zeilen wären eine Lüge. Menü-Befehle werden
    /// über Name + Ziel-Programm gruppiert: „Open Git Bash here“ steht an Ordner und
    /// Ordner-Hintergrund mit identischer EXE, aber unterschiedlichem Platzhalter (%1 / %V) –
    /// für den Nutzer ist das ein Eintrag. Beim Schalten werden alle zugehörigen Schlüssel angefasst.
    /// </summary>
    private static List<ContextMenuEntry> Merge(List<Raw> raw)
    {
        return raw
            .GroupBy(r => r.Kind == ContextMenuEntryKind.ShellExtension
                ? $"ext|{r.Clsid}"
                : $"verb|{r.Name}|{r.HandlerPath ?? r.Command}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new ContextMenuEntry
                {
                    Name = first.Name,
                    Kind = first.Kind,
                    Locations = group.Select(r => r.Location).Distinct().OrderBy(l => l).ToList(),
                    RegistryPaths = group.Select(r => r.RegistryPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Clsid = first.Clsid,
                    HandlerPath = first.HandlerPath,
                    Publisher = AuthenticodeService.PublisherFor(first.HandlerPath),
                    Command = first.Command,
                    // Ein Eintrag gilt nur als aktiviert, wenn er an KEINEM Ort abgeschaltet ist;
                    // schaltbar nur, wenn das an allen Orten erlaubt ist.
                    IsEnabled = group.All(r => r.IsEnabled),
                    CanToggle = group.All(r => r.CanToggle)
                };
            })
            .ToList();
    }

    /// <summary>Führt eine Quelle aus; ein Fehler darf die übrigen Quellen nicht abbrechen.</summary>
    private static void Safe(Action source)
    {
        try { source(); }
        catch { /* Quelle nicht lesbar -> restliche Quellen laufen weiter */ }
    }

    private static RegistryKey OpenClassesRoot()
        => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);

    /// <summary>Alle CLSIDs, die aktuell in der Blocked-Liste stehen (= abgeschaltete Handler).</summary>
    private static HashSet<string> ReadBlockedClsids()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(BlockedKeyPath);
            if (key is null) return blocked;

            foreach (var valueName in key.GetValueNames())
            {
                if (!string.IsNullOrEmpty(valueName))
                    blocked.Add(valueName);
            }
        }
        catch { /* Liste nicht lesbar -> alles gilt als aktiviert */ }

        return blocked;
    }

    private static void ReadStaticVerbs(List<Raw> entries, ContextMenuLocation location)
    {
        string shellPath = $@"{BasePath(location)}\shell";

        using var root = OpenClassesRoot();
        using var shellKey = root.OpenSubKey(shellPath);
        if (shellKey is null) return;

        foreach (var verb in shellKey.GetSubKeyNames())
        {
            try
            {
                using var verbKey = shellKey.OpenSubKey(verb);
                if (verbKey is null) continue;

                string command = ReadCommand(verbKey);
                string? exe = string.IsNullOrWhiteSpace(command)
                    ? null
                    : ResolveExisting(StartupService.SplitCommand(command).exe);

                entries.Add(new Raw(
                    Name: VerbDisplayName(verbKey, verb),
                    Location: location,
                    Kind: ContextMenuEntryKind.StaticVerb,
                    RegistryPath: $@"{shellPath}\{verb}",
                    Clsid: null,
                    HandlerPath: exe,
                    Command: command,
                    IsEnabled: verbKey.GetValue(LegacyDisableValue) is null,
                    CanToggle: !ProtectedVerbs.Contains(verb)));
            }
            catch { /* einzelnes Verb defekt -> überspringen */ }
        }
    }

    private static void ReadShellExtensions(List<Raw> entries, ContextMenuLocation location,
        HashSet<string> blocked)
    {
        string handlersPath = $@"{BasePath(location)}\shellex\ContextMenuHandlers";

        using var root = OpenClassesRoot();
        using var handlersKey = root.OpenSubKey(handlersPath);
        if (handlersKey is null) return;

        foreach (var handler in handlersKey.GetSubKeyNames())
        {
            try
            {
                using var handlerKey = handlersKey.OpenSubKey(handler);
                if (handlerKey is null) continue;

                // Der Standardwert ist die CLSID; manche Handler tragen sie stattdessen im
                // Schlüsselnamen ({…}) selbst.
                string clsid = handlerKey.GetValue(null)?.ToString()?.Trim() ?? "";
                if (!IsClsid(clsid))
                    clsid = IsClsid(handler) ? handler : "";
                if (clsid.Length == 0) continue;

                string? dll = ResolveHandlerDll(root, clsid);

                entries.Add(new Raw(
                    Name: HandlerDisplayName(root, clsid, handler),
                    Location: location,
                    Kind: ContextMenuEntryKind.ShellExtension,
                    RegistryPath: $@"{handlersPath}\{handler}",
                    Clsid: clsid,
                    HandlerPath: dll,
                    Command: "",
                    IsEnabled: !blocked.Contains(clsid),
                    CanToggle: true));
            }
            catch { /* einzelner Handler defekt -> überspringen */ }
        }
    }

    // ---- Schalten -----------------------------------------------------------

    /// <summary>
    /// Aktiviert/deaktiviert einen Eintrag. Deaktivieren heißt: CLSID in die Blocked-Liste
    /// eintragen bzw. den Wert LegacyDisable setzen – kein Schlüssel wird gelöscht.
    /// </summary>
    public void SetEnabled(ContextMenuEntry entry, bool enabled)
    {
        // Sperre bei erkannter Manipulation (OPCL-20): keine Schreibvorgänge in HKLM/HKCR.
        if (IntegrityState.IsBlocked) return;

        if (!entry.CanToggle) return;

        if (entry.Kind == ContextMenuEntryKind.ShellExtension)
        {
            if (string.IsNullOrEmpty(entry.Clsid))
                throw new InvalidOperationException(Loc.T("contextmenu.error.noClsid"));

            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(BlockedKeyPath, writable: true)
                ?? throw new InvalidOperationException(Loc.T("contextmenu.error.blockedKeyMissing"));

            if (enabled) key.DeleteValue(entry.Clsid, throwOnMissingValue: false);
            else key.SetValue(entry.Clsid, entry.Name, RegistryValueKind.String);
        }
        else
        {
            // Derselbe Menü-Befehl kann an mehreren Orten registriert sein (z. B. „cmd“ an
            // Ordner, Ordner-Hintergrund und Laufwerk) – alle zugehörigen Schlüssel schalten.
            using var root = OpenClassesRoot();
            foreach (var path in entry.RegistryPaths)
            {
                using var verbKey = root.OpenSubKey(path, writable: true)
                    ?? throw new InvalidOperationException(Loc.T("contextmenu.error.keyMissing"));

                if (enabled) verbKey.DeleteValue(LegacyDisableValue, throwOnMissingValue: false);
                else verbKey.SetValue(LegacyDisableValue, "", RegistryValueKind.String);
            }
        }

        entry.IsEnabled = enabled;
    }

    // ---- Hilfsfunktionen ----------------------------------------------------

    private static bool IsClsid(string value)
        => value.StartsWith('{') && value.EndsWith('}') && value.Length >= 38;

    /// <summary>Befehl des Verbs (…\command, Standardwert); leer, wenn keiner hinterlegt ist.</summary>
    private static string ReadCommand(RegistryKey verbKey)
    {
        try
        {
            using var commandKey = verbKey.OpenSubKey("command");
            return commandKey?.GetValue(null)?.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Anzeigename eines Verbs: Standardwert, sonst MUIVerb, sonst der Schlüsselname.
    /// Ressourcen-Verweise (<c>@shell32.dll,-1234</c>) werden nicht aufgelöst – dann greift
    /// der Schlüsselname, der bei solchen Windows-Verben ohnehin sprechend ist.
    /// </summary>
    private static string VerbDisplayName(RegistryKey verbKey, string verb)
    {
        foreach (var candidate in new[] { verbKey.GetValue(null)?.ToString(), verbKey.GetValue("MUIVerb")?.ToString() })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !candidate.StartsWith('@'))
                return candidate;
        }
        return verb;
    }

    /// <summary>Anzeigename eines Handlers: Standardwert des CLSID-Schlüssels, sonst Schlüsselname.</summary>
    private static string HandlerDisplayName(RegistryKey root, string clsid, string handlerKeyName)
    {
        try
        {
            using var clsidKey = root.OpenSubKey($@"CLSID\{clsid}");
            string? name = clsidKey?.GetValue(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
                return name;
        }
        catch { /* nicht lesbar -> Schlüsselname */ }

        return IsClsid(handlerKeyName) ? clsid : handlerKeyName;
    }

    /// <summary>Handler-Datei aus InprocServer32 (bzw. LocalServer32) des CLSID-Schlüssels.</summary>
    private static string? ResolveHandlerDll(RegistryKey root, string clsid)
    {
        foreach (var server in new[] { "InprocServer32", "LocalServer32" })
        {
            try
            {
                using var serverKey = root.OpenSubKey($@"CLSID\{clsid}\{server}");
                string? path = serverKey?.GetValue(null)?.ToString();
                if (string.IsNullOrWhiteSpace(path)) continue;

                // LocalServer32 kann Argumente enthalten, InprocServer32 ist ein reiner Pfad.
                string? candidate = ResolveExisting(StartupService.SplitCommand(path).exe)
                                    ?? ResolveExisting(Environment.ExpandEnvironmentVariables(path.Trim('"')));
                if (candidate is not null) return candidate;
            }
            catch { /* nächster Server-Typ */ }
        }

        return null;
    }

    private static string? ResolveExisting(string path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
}
