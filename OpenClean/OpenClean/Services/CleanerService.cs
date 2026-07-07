using System.IO;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>Ergebnis eines Löschvorgangs, für die Anzeige im UI.</summary>
public sealed class CleanupReport
{
    public int DeletedCount { get; set; }
    public long FreedBytes { get; set; }
    public List<string> Skipped { get; } = new();

    /// <summary>
    /// Pfade der tatsächlich gelöschten Objekte – für den Bericht der automatischen
    /// Reinigung („was wurde gelöscht"). Bei interaktiver Nutzung ungenutzt, aber billig.
    /// </summary>
    public List<string> Deleted { get; } = new();

    public string Summary =>
        Loc.T("cleanup.report.summary", DeletedCount, ByteFormatter.Format(FreedBytes)) +
        (Skipped.Count > 0 ? Loc.T("cleanup.report.skipped", Skipped.Count) : "");
}

/// <summary>
/// Agent 1: Löscht ausschließlich die vom Nutzer bestätigten Items.
/// Gesperrte/geschützte Objekte werden übersprungen, nie mit Absturz quittiert.
/// </summary>
public sealed class CleanerService
{
    /// <summary>
    /// Löscht alle ausgewählten Items der aktivierten Kategorien.
    /// Synchron; vom ViewModel per Task.Run aufgerufen.
    /// </summary>
    public CleanupReport Clean(IEnumerable<CleanupCategory> categories)
        => Clean(categories, null);

    /// <summary>
    /// Wie <see cref="Clean(IEnumerable{CleanupCategory})"/>, meldet aber pro gelöschtem/
    /// übersprungenem Item den Fortschritt (aktueller Pfad + Prozent).
    /// </summary>
    public CleanupReport Clean(IEnumerable<CleanupCategory> categories, IProgress<CleanupProgress>? progress)
    {
        var report = new CleanupReport();
        var list = categories.ToList();

        // Gesamtzahl der ausgewählten Items der aktivierten Kategorien (für Prozentanzeige).
        int total = list.Where(c => c.IsEnabled).Sum(c => c.Items.Count(i => i.IsSelected));
        int done = 0;

        foreach (var category in list)
        {
            if (!category.IsEnabled) continue;

            if (category.Kind == CleanupKind.RecycleBin)
            {
                if (category.Items.Any(i => i.IsSelected))
                {
                    progress?.Report(new CleanupProgress { CurrentPath = Loc.T("cleanup.recycleBin.progress"), Done = done, Total = total });
                    CleanRecycleBin(report, category);
                    // Alle ausgewählten Papierkorb-Items zählen als verarbeitet.
                    done += category.Items.Count(i => i.IsSelected);
                    progress?.Report(new CleanupProgress { CurrentPath = Loc.T("cleanup.recycleBin.progress"), Done = done, Total = total });
                }
                continue;
            }

            foreach (var item in category.Items.Where(i => i.IsSelected).ToList())
            {
                progress?.Report(new CleanupProgress { CurrentPath = item.FullPath, Done = done, Total = total });
                DeleteItem(report, item);
                done++;
                progress?.Report(new CleanupProgress { CurrentPath = item.FullPath, Done = done, Total = total });
            }
        }

        return report;
    }

    private static void CleanRecycleBin(CleanupReport report, CleanupCategory category)
    {
        var selected = category.Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        // Sind ALLE aufgelisteten Objekte ausgewählt, den offiziellen, schnellen Weg nehmen
        // (leert den Papierkorb komplett über die Shell).
        bool allSelected = selected.Count == category.Items.Count;
        if (allSelected)
        {
            try
            {
                RecycleBin.Empty();
                foreach (var item in selected)
                {
                    report.DeletedCount++;
                    report.FreedBytes += item.SizeBytes;
                    report.Deleted.Add(item.FullPath);
                }
                return;
            }
            catch
            {
                // Zeitüberschreitung/Fehler -> unten einzeln versuchen.
            }
        }

        // Teilauswahl (oder komplettes Leeren fehlgeschlagen): jedes ausgewählte Objekt
        // einzeln über seine $R-/$I-Dateien entfernen.
        foreach (var item in selected)
        {
            if (RecycleBin.DeleteEntry(item.RecycleDataPath, item.RecycleMetaPath))
            {
                report.DeletedCount++;
                report.FreedBytes += item.SizeBytes;
                report.Deleted.Add(item.FullPath);
            }
            else
            {
                report.Skipped.Add(item.FullPath);
            }
        }
    }

    private static void DeleteItem(CleanupReport report, ScanItem item)
    {
        if (!IsSafeToDelete(item.FullPath))
        {
            report.Skipped.Add(item.FullPath);
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                if (Directory.Exists(item.FullPath))
                    Directory.Delete(item.FullPath, recursive: true);
            }
            else if (File.Exists(item.FullPath))
            {
                var fi = new FileInfo(item.FullPath) { Attributes = FileAttributes.Normal };
                fi.Delete();
            }

            report.DeletedCount++;
            report.FreedBytes += item.SizeBytes;
            report.Deleted.Add(item.FullPath);
        }
        catch
        {
            // In Benutzung oder Zugriff verweigert -> überspringen, nicht abbrechen.
            report.Skipped.Add(item.FullPath);
        }
    }

    /// <summary>
    /// Schutz-Filter: verhindert das Löschen offensichtlich gefährlicher Pfade.
    /// Nur voll qualifizierte Pfade unterhalb einer Wurzel, keine Laufwerkswurzeln.
    /// </summary>
    private static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Path.IsPathFullyQualified(path)) return false;

        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return false; }

        // Laufwerkswurzel (z. B. "C:\") niemals löschen.
        string? parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent)) return false;

        // Kritische Systemwurzeln ausschließen.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar);
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System)
            .TrimEnd(Path.DirectorySeparatorChar);

        foreach (var forbidden in new[] { windows, system32 })
        {
            if (full.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Entpackordner von Single-File-.NET-Apps niemals löschen.
        if (IsProtectedFromCleaning(full)) return false;

        return true;
    }

    /// <summary>
    /// True, wenn der Pfad zum Selbst-Entpackordner von Single-File-.NET-Apps gehört
    /// (<c>%TEMP%\.net</c> bzw. <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c>) – gleich diesem
    /// Ordner oder darunter. Dorthin entpackt OpenClean beim Start seine native
    /// <c>e_sqlite3.dll</c>; würde der unbeaufsichtigte Lauf diesen Ordner (er liegt in
    /// <c>%TEMP%</c>, also im Reinigungsziel) löschen, brächte er sich mitten im Lauf
    /// selbst zum Absturz. Der Ausschluss schützt zugleich die laufenden Entpackordner
    /// anderer Single-File-Apps – ein Cleaner darf die nie anrühren.
    /// </summary>
    internal static bool IsProtectedFromCleaning(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return false; }

        foreach (var extractBase in NetBundleExtractBases())
        {
            if (full.Equals(extractBase, StringComparison.OrdinalIgnoreCase)) return true;
            // Echtes Nachfahren-Verhältnis (Grenze am Trennzeichen, kein reiner StartsWith:
            // sonst würde "...\.network" fälschlich auf "...\.net" matchen).
            if (full.StartsWith(extractBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Kandidaten für die .NET-Bundle-Entpackwurzel (normalisiert, ohne End-Trennzeichen).</summary>
    private static IEnumerable<string> NetBundleExtractBases()
    {
        var bases = new List<string>();

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try { bases.Add(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)); }
            catch { /* ungültiger Pfad -> ignorieren */ }
        }

        // Nutzerdefinierte Wurzel (falls gesetzt) hat Vorrang; darunter erzeugt der Host
        // je App/Version einen Unterordner – geschützt wird die gesamte Wurzel.
        Add(Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
        // Standard unter Windows: %TEMP%\.net
        try { Add(Path.Combine(Path.GetTempPath(), ".net")); }
        catch { /* GetTempPath sollte nie werfen -> defensiv */ }

        return bases;
    }
}
