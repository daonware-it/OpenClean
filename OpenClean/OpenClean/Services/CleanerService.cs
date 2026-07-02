using System.IO;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>Ergebnis eines Löschvorgangs, für die Anzeige im UI.</summary>
public sealed class CleanupReport
{
    public int DeletedCount { get; set; }
    public long FreedBytes { get; set; }
    public List<string> Skipped { get; } = new();

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
        long size = category.Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        try
        {
            RecycleBin.Empty();
            report.DeletedCount++;
            report.FreedBytes += size;
        }
        catch
        {
            report.Skipped.Add(Loc.T("cleanup.recycleBin.name"));
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

        return true;
    }
}
