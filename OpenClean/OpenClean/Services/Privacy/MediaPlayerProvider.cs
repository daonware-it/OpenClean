using System.IO;
using System.Text;
using Microsoft.Win32;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 6 (Agent 3): Media-Player-Verlauf löschen.
///
/// Kombiniert ZWEI Quellen in einem Provider:
///   • VLC media player: Konfigurationsdatei <c>%AppData%\vlc\vlcrc</c> (INI-Format).
///     Zuletzt gespielte Medien liegen in Zeilen wie <c>recentsmrl-list=…</c> bzw.
///     <c>qt-recentplay-list=…</c> als komma-getrennte MRLs (z. B. <c>file:///C:/…</c>).
///   • Windows Media Player (WMP): Registry-Schlüssel
///     <c>HKCU\Software\Microsoft\MediaPlayer\Player\RecentFileList</c> (und
///     <c>RecentURLList</c>) mit Werten <c>File0</c>, <c>File1</c>, …
///
/// Es werden persistente Spuren gelöscht -> Bestätigung anfordern.
///
/// Robustheit: Registry-/Datei-Operationen sind einzeln gekapselt; Scan/Clean lassen
/// NIE eine Ausnahme durch (im Zweifel leere Liste bzw. 0).
/// </summary>
public sealed class MediaPlayerProvider : IPrivacyProvider
{
    public string Name => "Media-Player-Verlauf";

    public string Description =>
        "Zuletzt abgespielte Medien in VLC media player und Windows Media Player.";

    public bool RequiresConfirmation => true;

    /// <summary>Quelle eines Eintrags, damit <c>CleanAsync</c> richtig löscht.</summary>
    private enum MediaSource
    {
        VlcIni,
        WmpRegistry
    }

    /// <summary>
    /// Opaker Lösch-Handle. Für VLC: <see cref="ConfigPath"/> + <see cref="Key"/> +
    /// die exakte <see cref="Value"/> (MRL). Für WMP: der Registry-Unterschlüssel
    /// (<see cref="Key"/>) + der Value-Name (<see cref="Value"/>).
    /// </summary>
    private sealed record MediaHandle(MediaSource Source, string ConfigPath, string Key, string Value);

    // ---- VLC ---------------------------------------------------------------

    private static string VlcConfigPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "vlc", "vlcrc");

    // Schlüssel, die (je nach VLC-Version) die Recent-Liste tragen.
    private static readonly string[] VlcRecentKeys =
    {
        "recentsmrl-list",
        "qt-recentplay-list"
    };

    // ---- WMP ---------------------------------------------------------------

    private const string WmpPlayerPath = @"Software\Microsoft\MediaPlayer\Player";
    private static readonly string[] WmpRecentSubKeys =
    {
        "RecentFileList",
        "RecentURLList"
    };

    public bool IsAvailable
    {
        get
        {
            try { return HasVlcEntries() || HasWmpKey(); }
            catch { return false; }
        }
    }

    private static bool HasVlcEntries()
    {
        try
        {
            string path = VlcConfigPath;
            if (!File.Exists(path)) return false;

            foreach (var line in File.ReadLines(path))
            {
                if (TryParseVlcLine(line, out _, out var values) && values.Count > 0)
                    return true;
            }
        }
        catch { /* nicht lesbar -> nicht verfügbar */ }
        return false;
    }

    private static bool HasWmpKey()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            foreach (var sub in WmpRecentSubKeys)
            {
                using var key = baseKey.OpenSubKey($@"{WmpPlayerPath}\{sub}");
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (!string.IsNullOrEmpty(name) &&
                        name.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { /* kein Zugriff -> nicht verfügbar */ }
        return false;
    }

    // ---- Scan --------------------------------------------------------------

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync()
    {
        var items = new List<PrivacyItem>();
        try
        {
            ScanVlc(items);
            ScanWmp(items);
        }
        catch { /* im Zweifel: bis hierhin Gesammeltes zurückgeben */ }

        return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);
    }

    private static void ScanVlc(List<PrivacyItem> items)
    {
        try
        {
            string path = VlcConfigPath;
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (!TryParseVlcLine(line, out var key, out var values)) continue;

                    foreach (var mrl in values)
                    {
                        if (string.IsNullOrWhiteSpace(mrl)) continue;

                        items.Add(new PrivacyItem
                        {
                            Name = MrlToDisplayName(mrl),
                            Detail = "VLC: " + MrlToPath(mrl),
                            Tag = new MediaHandle(MediaSource.VlcIni, path, key, mrl)
                        });
                    }
                }
                catch { /* einzelne Zeile defekt -> überspringen */ }
            }
        }
        catch { /* vlcrc nicht lesbar -> überspringen */ }
    }

    private static void ScanWmp(List<PrivacyItem> items)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            foreach (var sub in WmpRecentSubKeys)
            {
                try
                {
                    string subPath = $@"{WmpPlayerPath}\{sub}";
                    using var key = baseKey.OpenSubKey(subPath);
                    if (key is null) continue;

                    foreach (var name in key.GetValueNames())
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(name)) continue;
                            if (!name.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;

                            string value = key.GetValue(name)?.ToString() ?? "";
                            if (string.IsNullOrWhiteSpace(value)) continue;

                            items.Add(new PrivacyItem
                            {
                                Name = SafeFileName(value),
                                Detail = "Windows Media Player: " + value,
                                Tag = new MediaHandle(MediaSource.WmpRegistry, subPath, name, value)
                            });
                        }
                        catch { /* einzelner Wert defekt -> überspringen */ }
                    }
                }
                catch { /* einzelner Unterschlüssel defekt -> überspringen */ }
            }
        }
        catch { /* WMP-Registry nicht lesbar -> überspringen */ }
    }

    // ---- Clean -------------------------------------------------------------

    public Task<int> CleanAsync(IEnumerable<PrivacyItem> selected)
    {
        int deleted = 0;
        try
        {
            var handles = selected
                .Select(i => i.Tag)
                .OfType<MediaHandle>()
                .ToList();

            deleted += CleanVlc(handles.Where(h => h.Source == MediaSource.VlcIni));
            deleted += CleanWmp(handles.Where(h => h.Source == MediaSource.WmpRegistry));
        }
        catch { /* unerwarteter Fehler -> bisher gezählte Anzahl zurückgeben */ }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Entfernt die ausgewählten MRLs zeilenweise aus vlcrc. Gruppiert nach
    /// Konfigurationsdatei; jede betroffene Zeile wird neu geschrieben (Wert leer,
    /// falls keine MRLs übrig bleiben). Encoding (UTF-8) wird beibehalten.
    /// </summary>
    private static int CleanVlc(IEnumerable<MediaHandle> handles)
    {
        int removed = 0;
        try
        {
            foreach (var byFile in handles.GroupBy(h => h.ConfigPath, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string path = byFile.Key;
                    if (!File.Exists(path)) continue;

                    // Zu entfernende MRLs pro Schlüssel.
                    var removeByKey = byFile
                        .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => new HashSet<string>(g.Select(h => h.Value), StringComparer.Ordinal),
                            StringComparer.OrdinalIgnoreCase);

                    string[] lines = File.ReadAllLines(path);
                    bool changed = false;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (!TryParseVlcLine(lines[i], out var key, out var values)) continue;
                        if (!removeByKey.TryGetValue(key, out var toRemove)) continue;

                        var kept = values.Where(v => !toRemove.Contains(v)).ToList();
                        int diff = values.Count - kept.Count;
                        if (diff <= 0) continue;

                        lines[i] = key + "=" + string.Join(",", kept);
                        removed += diff;
                        changed = true;
                    }

                    if (changed)
                        File.WriteAllLines(path, lines, new UTF8Encoding(false));
                }
                catch { /* eine Datei nicht schreibbar -> überspringen */ }
            }
        }
        catch { /* unerwarteter Fehler -> bisher entfernte Anzahl */ }
        return removed;
    }

    /// <summary>Löscht die ausgewählten WMP-Registry-Werte (File0, File1, …).</summary>
    private static int CleanWmp(IEnumerable<MediaHandle> handles)
    {
        int removed = 0;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

            foreach (var byKey in handles.GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var key = baseKey.OpenSubKey(byKey.Key, writable: true);
                    if (key is null) continue;

                    foreach (var handle in byKey)
                    {
                        try
                        {
                            // Nur löschen, wenn der Wert noch existiert.
                            if (key.GetValue(handle.Value) is null &&
                                Array.IndexOf(key.GetValueNames(), handle.Value) < 0)
                                continue;

                            key.DeleteValue(handle.Value, throwOnMissingValue: false);
                            removed++;
                        }
                        catch { /* einzelner Wert nicht löschbar -> überspringen */ }
                    }
                }
                catch { /* Unterschlüssel nicht schreibbar -> überspringen */ }
            }
        }
        catch { /* Registry nicht erreichbar -> bisher entfernte Anzahl */ }
        return removed;
    }

    // ---- Hilfsfunktionen ---------------------------------------------------

    /// <summary>
    /// Prüft, ob eine vlcrc-Zeile eine bekannte Recent-Liste ist. Liefert den Schlüssel
    /// und die (komma-getrennten, nicht-leeren) Werte. Kommentar-/Leerzeilen liefern false.
    /// </summary>
    private static bool TryParseVlcLine(string line, out string key, out List<string> values)
    {
        key = "";
        values = new List<string>();

        if (string.IsNullOrWhiteSpace(line)) return false;

        string trimmed = line.TrimStart();
        // Kommentare in vlcrc beginnen mit '#'.
        if (trimmed.StartsWith('#')) return false;

        int eq = trimmed.IndexOf('=');
        if (eq <= 0) return false;

        string candidate = trimmed[..eq].Trim();
        if (!VlcRecentKeys.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return false;

        key = candidate;
        string rhs = trimmed[(eq + 1)..];

        foreach (var part in rhs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            values.Add(part);

        return true;
    }

    /// <summary>Wandelt eine MRL (z. B. <c>file:///C:/Filme/x.mp4</c>) in den Dateinamen.</summary>
    private static string MrlToDisplayName(string mrl)
    {
        try
        {
            string path = MrlToPath(mrl);
            string name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? mrl : name;
        }
        catch { return mrl; }
    }

    /// <summary>Entschlüsselt eine MRL in einen lesbaren Pfad (file://-URIs -> lokaler Pfad).</summary>
    private static string MrlToPath(string mrl)
    {
        try
        {
            if (Uri.TryCreate(mrl, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;

            return Uri.UnescapeDataString(mrl);
        }
        catch { return mrl; }
    }

    /// <summary>Dateiname aus einem WMP-Pfad, robust gegen ungültige Pfade.</summary>
    private static string SafeFileName(string value)
    {
        try
        {
            string name = Path.GetFileName(value);
            return string.IsNullOrWhiteSpace(name) ? value : name;
        }
        catch { return value; }
    }
}
