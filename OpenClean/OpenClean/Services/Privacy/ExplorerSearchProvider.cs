using System.Text;
using Microsoft.Win32;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 4 (Agent 2): Explorer-Suchverlauf löschen.
///
/// Quelle: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery</c>.
/// Die nummerierten Werte (0, 1, 2 …) sind REG_BINARY und enthalten den zuletzt im
/// Datei-Explorer gesuchten Begriff als null-terminierten UTF-16LE-String. Der Wert
/// <c>MRUListEx</c> ist die Reihenfolge-Liste (int32-Indizes) und wird nicht angezeigt.
///
/// Robustheit: Jeder Wert einzeln gekapselt; Scan/Clean lassen NIE eine Ausnahme durch.
/// </summary>
public sealed class ExplorerSearchProvider : IPrivacyProvider
{
    public string Name => "Explorer-Suchverlauf";
    public string Description => "Zuletzt im Datei-Explorer gesuchte Begriffe.";

    // Es werden persistente Spuren gelöscht -> Bestätigung anfordern.
    public bool RequiresConfirmation => true;

    private const string WordWheelPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery";

    /// <summary>Opaker Lösch-Handle: Name des binären Registry-Werts unter WordWheelQuery.</summary>
    private sealed record WordWheelHandle(string ValueName);

    private static bool IsOrderingValue(string valueName)
        => string.IsNullOrEmpty(valueName)
           || valueName.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(WordWheelPath);
                return key is not null && key.ValueCount > 0;
            }
            catch { return false; }
        }
    }

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync()
    {
        var items = new List<PrivacyItem>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(WordWheelPath);
            if (key is null)
                return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);

            foreach (var valueName in key.GetValueNames())
            {
                try
                {
                    if (IsOrderingValue(valueName)) continue;
                    if (key.GetValue(valueName) is not byte[] data || data.Length == 0) continue;

                    string term = DecodeUtf16(data);
                    items.Add(new PrivacyItem
                    {
                        Name = term.Length > 0 ? term : $"(Eintrag {valueName})",
                        Detail = "Datei-Explorer-Suche",
                        Tag = new WordWheelHandle(valueName)
                    });
                }
                catch { /* einzelner Wert defekt -> überspringen */ }
            }
        }
        catch { /* im Zweifel: bis hierhin Gesammeltes zurückgeben */ }

        return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);
    }

    /// <summary>Dekodiert einen null-terminierten UTF-16LE-Suchbegriff aus den Binärdaten.</summary>
    private static string DecodeUtf16(byte[] data)
    {
        try
        {
            string s = Encoding.Unicode.GetString(data);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s[..nul];
            return s.Trim();
        }
        catch { return ""; }
    }

    public Task<int> CleanAsync(IEnumerable<PrivacyItem> selected)
    {
        int deleted = 0;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(WordWheelPath, writable: true);
            if (key is null) return Task.FromResult(0);

            foreach (var item in selected)
            {
                try
                {
                    if (item.Tag is not WordWheelHandle handle) continue;
                    if (key.GetValue(handle.ValueName) is null) continue;
                    key.DeleteValue(handle.ValueName, throwOnMissingValue: false);
                    deleted++;
                }
                catch { /* einzelner Wert nicht löschbar -> überspringen */ }
            }

            // Nach dem Entfernen von Einträgen wird die Reihenfolge-Liste inkonsistent.
            // Sicherste Variante: MRUListEx entfernen -> Windows baut sie neu auf.
            if (deleted > 0)
            {
                try { key.DeleteValue("MRUListEx", throwOnMissingValue: false); }
                catch { /* nicht kritisch */ }
            }
        }
        catch { /* Registry nicht erreichbar -> gelöschte Anzahl zurückgeben */ }

        return Task.FromResult(deleted);
    }
}
