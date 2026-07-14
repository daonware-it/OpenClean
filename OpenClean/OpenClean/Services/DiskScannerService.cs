using System.Diagnostics;
using System.IO;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>Parameter eines Belegungs-Scans.</summary>
public sealed class DiskScanOptions
{
    /// <summary>Startpunkt, z. B. „C:\" oder ein Unterordner beim Drill-down.</summary>
    public string RootPath { get; init; } = "";

    /// <summary>Wie viele Ebenen unter der Wurzel einzeln im Baum behalten werden.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Kleinste Dateigröße, die in die Top-Liste aufgenommen wird (0 = keine Grenze).</summary>
    public long MinFileBytes { get; init; }

    /// <summary>Wie viele der größten Dateien behalten werden.</summary>
    public int MaxFiles { get; init; } = 500;

    /// <summary>True: Systemordner werden übersprungen (nur für den Datei-Finder).</summary>
    public bool ExcludeSystemFolders { get; init; }
}

/// <summary>Zwischenstand des Scans für die Fortschrittsanzeige.</summary>
public sealed record DiskScanProgress(string CurrentPath, long ScannedBytes, int SkippedFolders);

/// <summary>Ergebnis eines Scans: Ordnerbaum plus die größten Dateien.</summary>
public sealed record DiskScanResult(FolderNode Root, IReadOnlyList<LargeFile> LargestFiles, int SkippedFolders);

/// <summary>
/// Durchläuft einen Datenträger (oder Unterordner) genau einmal und liefert daraus zwei
/// Dinge: den tiefenbegrenzten Ordnerbaum für den Sunburst und die größten Dateien für den
/// Große-Dateien-Finder.
///
/// <para>Der Lauf ist iterativ (eigener Stack) statt rekursiv mit
/// <c>SearchOption.AllDirectories</c>: Nur so überlebt der Scan einen einzelnen unlesbaren
/// Ordner, statt komplett abzubrechen. Reparse-Points (Junctions, Symlinks) werden
/// übersprungen – sonst läuft man z. B. über <c>C:\Users\All Users</c> im Kreis.</para>
///
/// <para>Die größten Dateien liegen in einer <see cref="PriorityQueue{TElement,TPriority}"/>
/// fester Kapazität: Speicherbedarf bleibt konstant, egal wie viele Dateien die Platte hat.</para>
/// </summary>
public sealed class DiskScannerService
{
    /// <summary>Abstand zwischen zwei Fortschrittsmeldungen – häufiger erstickt den UI-Thread.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(50);

    public Task<DiskScanResult> ScanAsync(
        DiskScanOptions options,
        IProgress<DiskScanProgress>? progress,
        CancellationToken ct)
        => Task.Run(() => Scan(options, progress, ct), ct);

    private static DiskScanResult Scan(
        DiskScanOptions options,
        IProgress<DiskScanProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string rootPath = options.RootPath;
        var root = new FolderNode(FolderName(rootPath), rootPath);

        // Kleinste-zuerst-Halde: Der kleinste Eintrag liegt vorn und fliegt raus,
        // sobald eine größere Datei auftaucht und die Kapazität erreicht ist.
        var largest = new PriorityQueue<LargeFile, long>();

        int skippedFolders = 0;
        long scannedBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        if (!Directory.Exists(rootPath))
            return new DiskScanResult(root, Array.Empty<LargeFile>(), 0);

        // Nachbearbeitungs-Stack: Wir müssen Kinder-Bytes zum Elternteil hochreichen,
        // deshalb zwei Durchgänge über denselben Knoten (Betreten / Verlassen).
        var stack = new Stack<(FolderNode Node, int Depth, bool Visited)>();
        stack.Push((root, 0, false));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (node, depth, visited) = stack.Pop();

            if (visited)
            {
                // Verlassen: Kinder sind fertig, ihre Bytes aufaddieren.
                foreach (var child in node.Children)
                    node.TotalBytes += child.TotalBytes;

                // Kinder unterhalb der Tiefengrenze wieder abschneiden: Ihre Bytes
                // stecken jetzt im Elternteil, der Teilbaum wird beim Drill-down nachgeladen.
                if (depth >= options.MaxDepth && node.Children.Count > 0)
                {
                    node.Children.Clear();
                    node.IsPartial = true;
                }
                continue;
            }

            // Betreten: erst die Dateien dieses Ordners, dann die Unterordner einreihen.
            stack.Push((node, depth, true));

            string[] files;
            try { files = Directory.GetFiles(node.FullPath); }
            catch { skippedFolders++; continue; }

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();

                long size;
                DateTime written;
                try
                {
                    var info = new FileInfo(file);
                    size = info.Length;
                    written = info.LastWriteTime;
                }
                catch { continue; }   // gesperrt oder verschwunden – nicht der Rede wert

                node.TotalBytes += size;
                scannedBytes += size;

                if (options.MaxFiles > 0 && size >= options.MinFileBytes)
                {
                    largest.Enqueue(new LargeFile(file, size, written), size);
                    if (largest.Count > options.MaxFiles) largest.Dequeue();
                }
            }

            if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress.Report(new DiskScanProgress(node.FullPath, scannedBytes, skippedFolders));
            }

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(node.FullPath); }
            catch { skippedFolders++; continue; }

            foreach (string dir in subDirs)
            {
                if (IsReparsePoint(dir)) continue;
                if (options.ExcludeSystemFolders && PathSafety.IsExcludedFolder(dir)) continue;

                var child = new FolderNode(FolderName(dir), dir) { Parent = node };
                node.Children.Add(child);
                stack.Push((child, depth + 1, false));
            }
        }

        // Halde leeren und absteigend sortieren (die Halde selbst liefert aufsteigend).
        var files2 = new List<LargeFile>(largest.Count);
        while (largest.TryDequeue(out var file2, out _)) files2.Add(file2);
        files2.Reverse();

        progress?.Report(new DiskScanProgress(rootPath, scannedBytes, skippedFolders));
        return new DiskScanResult(root, files2, skippedFolders);
    }

    /// <summary>Junctions/Symlinks überspringen – sonst drohen Endlosschleifen.</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }   // nicht lesbar → lieber überspringen
    }

    /// <summary>Letztes Pfadsegment; bei „C:\" das Laufwerk selbst.</summary>
    private static string FolderName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
