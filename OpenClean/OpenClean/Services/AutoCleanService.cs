using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Agent (Automatik-Ausführung): führt eine geplante Reinigung UNBEAUFSICHTIGT aus.
/// Es wird NICHT nachgefragt – die zuvor per Profil festgelegten Kategorien werden
/// gescannt und alle gefundenen Objekte gelöscht. Wiederverwendet bewusst den
/// bestehenden <see cref="TempScannerService"/> und <see cref="CleanerService"/>,
/// damit die automatische Reinigung exakt dieselbe (geprüfte) Lösch-Logik nutzt wie
/// die manuelle Bereinigung.
/// </summary>
public sealed class AutoCleanService
{
    private readonly TempScannerService _scanner = new();
    private readonly CleanerService _cleaner = new();

    /// <summary>
    /// Kategorie-Schlüssel des Profils "Nur Temp" – die risikoärmsten, reinen
    /// temporären Bereiche (keine Browser-Caches, kein Papierkorb).
    /// </summary>
    private static readonly string[] TempOnlyKeys =
    {
        "cat.windowsTemp", "cat.updateCache", "cat.thumbnails", "cat.inetCache"
    };

    /// <summary>
    /// Liefert die Kategorie-Schlüssel, die für ein Profil gereinigt werden.
    /// "Full" = alle aktuell verfügbaren Kategorien; "Custom" = die gewählten;
    /// sonst (Standard) das Temp-Profil.
    /// </summary>
    public IReadOnlyList<string> ResolveCategoryKeys(ScheduleSettings schedule)
    {
        var available = _scanner.CreateCategories().Select(c => c.Key).ToList();
        var availableSet = new HashSet<string>(available, StringComparer.Ordinal);

        return schedule.Profile switch
        {
            "Full" => available,
            "Custom" => schedule.CustomCategoryKeys.Where(availableSet.Contains).ToList(),
            _ => TempOnlyKeys.Where(availableSet.Contains).ToList()
        };
    }

    /// <summary>
    /// Führt die Reinigung für das gegebene Profil synchron aus und gibt den Bericht
    /// zurück. Rein server-seitig (keine UI) – für den <c>--auto</c>-Start gedacht.
    /// </summary>
    public CleanupReport Run(ScheduleSettings schedule)
    {
        var wantedKeys = new HashSet<string>(ResolveCategoryKeys(schedule), StringComparer.Ordinal);

        // Alle verfügbaren Kategorien erzeugen; nur die gewünschten aktivieren.
        var categories = _scanner.CreateCategories().ToList();
        foreach (var category in categories)
            category.IsEnabled = wantedKeys.Contains(category.Key);

        var enabled = categories.Where(c => c.IsEnabled).ToList();
        if (enabled.Count == 0)
            return new CleanupReport();

        // Scannen (rein lesend) und Ergebnis in die Kategorien übernehmen.
        var scanned = _scanner.ScanAll(enabled);
        foreach (var (category, items) in scanned)
        {
            foreach (var item in items)
            {
                item.IsSelected = true; // Automatik: ausnahmslos alles Gefundene löschen.
                category.Items.Add(item);
            }
        }

        // Löschen – ohne Bestätigung, das ist der Sinn der automatischen Reinigung.
        return _cleaner.Clean(enabled);
    }
}
