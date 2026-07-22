using System.IO;
using OpenClean.Models;
using OpenClean.Services.Integrity;
using OpenClean.Services.Safety;

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

    /// <summary>
    /// True, wenn gar nicht gelöscht wurde, weil die Integritätsprüfung angeschlagen hat
    /// (OPCL-20). Unterscheidet „nichts zu tun" von „aus Sicherheitsgründen verweigert".
    /// </summary>
    public bool Blocked { get; init; }

    public string Summary => Blocked
        ? Loc.T("integrity.blocked.summary")
        : Loc.T("cleanup.report.summary", DeletedCount, ByteFormatter.Format(FreedBytes)) +
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
        => Clean(categories, null, null);

    /// <summary>
    /// Wie <see cref="Clean(IEnumerable{CleanupCategory})"/>, meldet aber pro gelöschtem/
    /// übersprungenem Item den Fortschritt (aktueller Pfad + Prozent).
    /// </summary>
    public CleanupReport Clean(IEnumerable<CleanupCategory> categories, IProgress<CleanupProgress>? progress)
        => Clean(categories, progress, null);

    /// <summary>
    /// Kernmethode mit optionalem Sicherheitskontext. Die gemeinsame Klammer aller Bereinigungs-
    /// pfade: Ist ein <paramref name="safety"/>-Kontext gesetzt, laufen alle Datei-Löschungen über
    /// dessen <see cref="BackupSession"/> (Undo möglich); optional erstellt der Dienst dann auch den
    /// Wiederherstellungspunkt selbst (für einen unbeaufsichtigten Aufrufer ohne UI).
    ///
    /// <para>Interaktiver Aufruf: das ViewModel erstellt den Wiederherstellungspunkt vorab (mit
    /// UI-Rückfrage bei Fehlern) und reicht eine fertige <see cref="BackupSession"/> herein, die es
    /// danach selbst committet.</para>
    ///
    /// <para>Ohne Kontext (<paramref name="safety"/> = null): unverändertes, direktes Löschen wie
    /// bisher – z. B. der über den Choke Point laufende, von einem eigenen Sicherheitsmodell
    /// begleitete Aufruf oder Tests. Der Aufrufer bleibt Eigentümer einer evtl. übergebenen Sitzung
    /// und committet sie selbst.</para>
    /// </summary>
    public CleanupReport Clean(IEnumerable<CleanupCategory> categories, IProgress<CleanupProgress>? progress, CleanupSafetyContext? safety)
    {
        // Sperre bei erkannter Manipulation (OPCL-20). Bewusst HIER und nicht nur im ViewModel:
        // AutoCleanService ruft diese Methode für den unbeaufsichtigten --auto-Lauf direkt auf.
        if (IntegrityState.IsBlocked)
            return new CleanupReport { Blocked = true };

        // Optionaler unbeaufsichtigter Aufrufer, der den Wiederherstellungspunkt hier erzeugen lässt.
        if (safety?.CreateRestorePointHere == true)
            CleanupSafety.EnsureRestorePoint("cleanup"); // best effort, kein Abbruch (kein UI)

        BackupSession? session = safety?.Session;

        var report = new CleanupReport();

        // Papierkorb-Kategorien ZUERST verarbeiten: Wird der Papierkorb im selben Lauf geleert,
        // muss das geschehen, BEVOR mit PreferRecycle gesicherte Temp-Dateien dorthin verschoben
        // werden – sonst würde das Leeren die eben erstellten Backups wieder vernichten.
        // OrderBy ist stabil, die übrige Reihenfolge bleibt also erhalten.
        var list = categories.OrderBy(c => c.Kind == CleanupKind.RecycleBin ? 0 : 1).ToList();

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

            var selectedItems = category.Items.Where(i => i.IsSelected).ToList();

            // Mit Sicherheitsnetz gestapelt löschen (ein Shell-Aufruf je Stapel statt je Datei).
            if (session is not null)
            {
                done = DeleteItemsBatched(report, selectedItems, session, progress, done, total);
                continue;
            }

            foreach (var item in selectedItems)
            {
                progress?.Report(new CleanupProgress { CurrentPath = item.FullPath, Done = done, Total = total });
                DeleteItem(report, item, session);
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

    // Objekte je Stapel an die Backup-Sitzung geben. Die Größe bestimmt, wie fein der Fortschritt
    // gemeldet wird (je Stapel, nicht je Datei) – 128 hält die Anzeige flüssig und amortisiert die
    // Kosten des Shell-Aufrufs praktisch vollständig.
    private const int SafeDeleteBatchSize = 128;

    /// <summary>
    /// Löscht die ausgewählten Objekte einer Kategorie gesichert in Stapeln. Gibt den fortgeschriebenen
    /// <paramref name="done"/>-Zähler zurück.
    /// </summary>
    private static int DeleteItemsBatched(CleanupReport report, List<ScanItem> items, BackupSession session,
        IProgress<CleanupProgress>? progress, int done, int total)
    {
        var batch = new List<ScanItem>(SafeDeleteBatchSize);

        foreach (var item in items)
        {
            // Der Schutzfilter bleibt eine Einzelfallprüfung und läuft VOR dem Stapel.
            if (!IsSafeToDelete(item.FullPath))
            {
                report.Skipped.Add(item.FullPath);
                done++;
                progress?.Report(new CleanupProgress { CurrentPath = item.FullPath, Done = done, Total = total });
                continue;
            }

            batch.Add(item);
            if (batch.Count < SafeDeleteBatchSize) continue;

            done = FlushDeleteBatch(report, batch, session, progress, done, total);
            batch.Clear();
        }

        if (batch.Count > 0)
            done = FlushDeleteBatch(report, batch, session, progress, done, total);

        return done;
    }

    private static int FlushDeleteBatch(CleanupReport report, List<ScanItem> batch, BackupSession session,
        IProgress<CleanupProgress>? progress, int done, int total)
    {
        progress?.Report(new CleanupProgress { CurrentPath = batch[0].FullPath, Done = done, Total = total });

        var requests = new List<SafeDeleteRequest>(batch.Count);
        foreach (var item in batch)
            requests.Add(new SafeDeleteRequest(item.FullPath, item.IsDirectory, item.SizeBytes));

        var outcomes = session.TryDeleteMany(requests, SafeDeleteStrategy.PreferRecycle);

        for (int i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            if (outcomes[i] == SafeDeleteOutcome.Deleted)
            {
                report.DeletedCount++;
                report.FreedBytes += item.SizeBytes;
                report.Deleted.Add(item.FullPath);
            }
            else
            {
                report.Skipped.Add(item.FullPath);
            }
            done++;
        }

        progress?.Report(new CleanupProgress { CurrentPath = batch[^1].FullPath, Done = done, Total = total });
        return done;
    }

    private static void DeleteItem(CleanupReport report, ScanItem item, BackupSession? session)
    {
        if (!IsSafeToDelete(item.FullPath))
        {
            report.Skipped.Add(item.FullPath);
            return;
        }

        // Mit aktivem Sicherheitsnetz: gesichert löschen (Temp/Cache -> Papierkorb, sonst Backup-Kopie).
        if (session is not null)
        {
            var outcome = session.TryDelete(item.FullPath, item.IsDirectory, item.SizeBytes, SafeDeleteStrategy.PreferRecycle);
            if (outcome == SafeDeleteOutcome.Deleted)
            {
                report.DeletedCount++;
                report.FreedBytes += item.SizeBytes;
                report.Deleted.Add(item.FullPath);
            }
            else
            {
                report.Skipped.Add(item.FullPath);
            }
            return;
        }

        // Ohne Sicherheitsnetz (Backup abgeschaltet): direktes, endgültiges Löschen wie bisher.
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
            // Gleich oder echter Nachfahre (Grenze am Trennzeichen, kein reiner StartsWith:
            // sonst würde "...\.network" fälschlich auf "...\.net" matchen) – zentral in PathScope.
            if (PathScope.IsSameOrUnderNormalized(full, extractBase)) return true;
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
