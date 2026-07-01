using Microsoft.Win32;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 3 (Agent 2): Startmenü-/Such-Verlauf löschen.
///
/// Sammelt die vom Nutzer eingegebenen Such-/Pfad-Begriffe aus mehreren
/// bekannten Registry-Quellen unter HKCU:
///   • Explorer\TypedPaths      – in Adresszeile/„Ausführen“ getippte Pfade (url1, url2 …)
///   • Explorer\RunMRU          – Verlauf des „Ausführen“-Dialogs (a, b, c … + MRUList)
///   • Explorer\SearchHistory\* – ältere Startmenü-/Such-Verlaufswerte (falls vorhanden)
///
/// Robustheit: Jede Quelle und jeder einzelne Wert ist gekapselt. ScanAsync/CleanAsync
/// lassen NIE eine Ausnahme durch (im Zweifel leere Liste bzw. 0).
/// </summary>
public sealed class StartMenuSearchProvider : IPrivacyProvider
{
    public string Name => "Startmenü-Suchverlauf";
    public string Description => "Zuletzt in die Windows-Suche eingegebene Begriffe.";

    // Es werden persistente Spuren gelöscht -> Bestätigung anfordern.
    public bool RequiresConfirmation => true;

    // Kandidaten-Schlüssel unter HKCU (view-relativ, Registry64).
    private const string TypedPathsPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths";
    private const string RunMruPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU";
    private const string SearchHistoryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\SearchHistory";

    /// <summary>Opaker Lösch-Handle: Registry-Wert unter HKCU\<c>KeyPath</c> mit <c>ValueName</c>.</summary>
    private sealed record RegValueHandle(string KeyPath, string ValueName);

    // Ordnungs-/Metawerte, die keinen eigentlichen Suchbegriff darstellen.
    private static bool IsOrderingValue(string valueName)
        => string.IsNullOrEmpty(valueName)
           || valueName.Equals("MRUList", StringComparison.OrdinalIgnoreCase)
           || valueName.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                foreach (var path in new[] { TypedPathsPath, RunMruPath, SearchHistoryPath })
                {
                    using var key = baseKey.OpenSubKey(path);
                    if (key is not null && key.ValueCount + key.SubKeyCount > 0)
                        return true;
                }
            }
            catch { /* Zugriff nicht möglich -> nicht verfügbar */ }
            return false;
        }
    }

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync()
    {
        var items = new List<PrivacyItem>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

            // Adresszeile / „Ausführen“: getippte Pfade.
            ReadStringValues(baseKey, TypedPathsPath, "Adresszeile", items);
            // „Ausführen“-Dialog: Verlauf (Werte enthalten Befehl + Trenner "\1").
            ReadStringValues(baseKey, RunMruPath, "Ausführen", items);
            // Älterer Suchverlauf-Schlüssel inkl. Unterschlüssel.
            ReadSearchHistory(baseKey, items);
        }
        catch { /* im Zweifel liefern wir, was bis hier gesammelt wurde */ }

        return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);
    }

    private static void ReadStringValues(RegistryKey baseKey, string path, string quelle, List<PrivacyItem> items)
    {
        try
        {
            using var key = baseKey.OpenSubKey(path);
            if (key is null) return;

            foreach (var valueName in key.GetValueNames())
            {
                try
                {
                    if (IsOrderingValue(valueName)) continue;
                    string raw = key.GetValue(valueName)?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    // RunMRU speichert den Befehl mit angehängtem "\1" -> abschneiden.
                    string text = raw;
                    int sep = text.IndexOf('\x01');
                    if (sep >= 0) text = text[..sep];
                    text = text.Trim();
                    if (text.Length == 0) continue;

                    items.Add(new PrivacyItem
                    {
                        Name = text,
                        Detail = quelle,
                        Tag = new RegValueHandle(path, valueName)
                    });
                }
                catch { /* einzelner Wert defekt -> überspringen */ }
            }
        }
        catch { /* Quelle nicht lesbar -> überspringen */ }
    }

    private static void ReadSearchHistory(RegistryKey baseKey, List<PrivacyItem> items)
    {
        try
        {
            using var root = baseKey.OpenSubKey(SearchHistoryPath);
            if (root is null) return;

            // Direkte Werte im Wurzel-Schlüssel.
            ReadStringValues(baseKey, SearchHistoryPath, "Suchverlauf", items);

            // Werte in Unterschlüsseln (z. B. je Anwendung).
            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    string subPath = SearchHistoryPath + "\\" + subName;
                    ReadStringValues(baseKey, subPath, "Suchverlauf", items);
                }
                catch { /* einzelner Unterschlüssel defekt -> überspringen */ }
            }
        }
        catch { /* Quelle nicht lesbar -> überspringen */ }
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
                    if (item.Tag is not RegValueHandle handle) continue;
                    using var key = baseKey.OpenSubKey(handle.KeyPath, writable: true);
                    if (key is null) continue;

                    // Nur löschen, wenn der Wert (noch) existiert.
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
