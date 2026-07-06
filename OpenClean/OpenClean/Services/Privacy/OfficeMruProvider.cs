using Microsoft.Win32;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 7 (Agent 2): Office-Verlauf (zuletzt verwendete Dokumente) löschen.
///
/// Quelle: <c>HKCU\Software\Microsoft\Office\&lt;Version&gt;\&lt;App&gt;\{File MRU|Place MRU}</c>.
/// Iteriert alle vorhandenen Versions-Unterschlüssel (z. B. 16.0, 15.0) und die Apps
/// Word/Excel/PowerPoint. Die MRU-Werte („Item 1“, „Item 2“ …) sind REG_SZ im Format
/// <c>[F00000000][T01D…][O00000000]*C:\Pfad\Datei.docx</c>; der Dateipfad steht nach dem
/// letzten <c>*</c>.
///
/// Robustheit: Jede Version/App/Wert einzeln gekapselt; Scan/Clean lassen NIE eine
/// Ausnahme durch (im Zweifel leere Liste bzw. 0).
/// </summary>
public sealed class OfficeMruProvider : IPrivacyProvider
{
    public string Name => Loc.T("privacy.officeMru.name");
    public string Description => Loc.T("privacy.officeMru.description");

    // Es werden persistente Spuren gelöscht -> Bestätigung anfordern.
    public bool RequiresConfirmation => true;

    private const string OfficeRoot = @"Software\Microsoft\Office";

    // Anzeigename je App-Schlüssel.
    private static readonly (string Key, string Display)[] Apps =
    {
        ("Word", "Word"),
        ("Excel", "Excel"),
        ("PowerPoint", "PowerPoint")
    };

    // MRU-Listen je App.
    private static readonly (string SubKey, string Label)[] MruLists =
    {
        ("File MRU", "Dokument"),
        ("Place MRU", "Speicherort")
    };

    /// <summary>Opaker Lösch-Handle: Registry-Wert unter HKCU\<c>KeyPath</c> mit <c>ValueName</c>.</summary>
    private sealed record OfficeMruHandle(string KeyPath, string ValueName);

    public bool IsAvailable
    {
        get
        {
            try
            {
                foreach (var _ in EnumerateMruKeys())
                    return true; // mindestens ein MRU-Schlüssel existiert
            }
            catch { /* nicht verfügbar */ }
            return false;
        }
    }

    /// <summary>Liefert alle existierenden MRU-Schlüsselpfade samt App-/Listenbeschriftung.</summary>
    private static IEnumerable<(string KeyPath, string AppDisplay, string Label)> EnumerateMruKeys()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var root = baseKey.OpenSubKey(OfficeRoot);
        if (root is null) yield break;

        foreach (var version in root.GetSubKeyNames())
        {
            // Nur numerische Versionsschlüssel (z. B. 16.0) berücksichtigen.
            if (!IsVersionKey(version)) continue;

            foreach (var (appKey, appDisplay) in Apps)
            {
                foreach (var (mruSub, label) in MruLists)
                {
                    string keyPath = $@"{OfficeRoot}\{version}\{appKey}\{mruSub}";
                    bool exists;
                    try
                    {
                        using var k = baseKey.OpenSubKey(keyPath);
                        exists = k is not null && k.ValueCount > 0;
                    }
                    catch { exists = false; }

                    if (exists)
                        yield return (keyPath, appDisplay, label);
                }
            }
        }
    }

    private static bool IsVersionKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (char c in name)
            if (!char.IsDigit(c) && c != '.') return false;
        return char.IsDigit(name[0]);
    }

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync()
    {
        var items = new List<PrivacyItem>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

            foreach (var (keyPath, appDisplay, label) in EnumerateMruKeys())
            {
                try
                {
                    using var key = baseKey.OpenSubKey(keyPath);
                    if (key is null) continue;

                    foreach (var valueName in key.GetValueNames())
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(valueName)) continue;
                            // Ordnungswert der MRU-Liste überspringen.
                            if (valueName.Equals("Max Display", StringComparison.OrdinalIgnoreCase)) continue;

                            string raw = key.GetValue(valueName)?.ToString() ?? "";
                            if (string.IsNullOrWhiteSpace(raw)) continue;

                            string path = ExtractPath(raw);
                            if (path.Length == 0) continue;

                            items.Add(new PrivacyItem
                            {
                                Name = path,
                                Detail = $"{appDisplay} · {label}",
                                Tag = new OfficeMruHandle(keyPath, valueName)
                            });
                        }
                        catch { /* einzelner Wert defekt -> überspringen */ }
                    }
                }
                catch { /* einzelner Schlüssel defekt -> überspringen */ }
            }
        }
        catch { /* im Zweifel: bis hierhin Gesammeltes zurückgeben */ }

        return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);
    }

    /// <summary>Extrahiert den Dateipfad hinter dem letzten <c>*</c> aus einem MRU-Wert.</summary>
    private static string ExtractPath(string raw)
    {
        int star = raw.LastIndexOf('*');
        string path = star >= 0 && star + 1 < raw.Length ? raw[(star + 1)..] : raw;
        return path.Trim();
    }

    public Task<int> CleanAsync(IEnumerable<PrivacyItem> selected)
    {
        int deleted = 0;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

            foreach (var item in selected)
            {
                try
                {
                    if (item.Tag is not OfficeMruHandle handle) continue;
                    using var key = baseKey.OpenSubKey(handle.KeyPath, writable: true);
                    if (key is null) continue;
                    if (key.GetValue(handle.ValueName) is null) continue;

                    key.DeleteValue(handle.ValueName, throwOnMissingValue: false);
                    deleted++;
                }
                catch { /* einzelner Wert nicht löschbar -> überspringen */ }
            }
        }
        catch { /* Registry nicht erreichbar -> gelöschte Anzahl zurückgeben */ }

        return Task.FromResult(deleted);
    }
}
