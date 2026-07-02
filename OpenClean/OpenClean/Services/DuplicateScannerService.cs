using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Agent (Duplikat-Finder): findet exakte Duplikate über mehrere Ordner hinweg.
/// Rein lesend – gelöscht wird ausschließlich vom Aufrufer nach Nutzer-Bestätigung.
///
/// Dreistufig, damit große Datenmengen praktikabel bleiben:
///  1. Dateien einsammeln und nach Größe gruppieren (nur gleich große Dateien
///     können Duplikate sein),
///  2. Teil-Hash der ersten 64 KB (sortiert die meisten Kandidaten billig aus),
///  3. vollständiger SHA-256-Hash – nur Dateien mit identischem Voll-Hash gelten
///     als Duplikate.
/// </summary>
public sealed class DuplicateScannerService
{
    private const int PartialHashBytes = 64 * 1024;

    /// <summary>
    /// Scannt die übergebenen Ordner (rekursiv) und liefert alle Duplikat-Gruppen,
    /// sortiert nach verschwendetem Speicher (größte zuerst). Überlappende Ordner
    /// (einer liegt im anderen) verfälschen nichts – jede Datei zählt nur einmal.
    /// </summary>
    public IReadOnlyList<DuplicateGroup> Scan(IReadOnlyList<string> folders,
        IProgress<DuplicateScanProgress>? progress, CancellationToken ct)
    {
        // ---- Phase 1: Dateien einsammeln (dedupliziert über den vollen Pfad) ----
        var files = CollectFiles(folders, progress, ct);

        // Nur gleich große Dateien können identisch sein; 0-Byte-Dateien sind
        // trivial „gleich" und werden bewusst ignoriert.
        var sizeGroups = files
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Key > 0 && g.Count() > 1)
            .ToList();

        int totalCandidates = sizeGroups.Sum(g => g.Count());
        int done = 0;

        // ---- Phase 2 + 3: Teil-Hash, dann Voll-Hash je Größenklasse ----
        var result = new ConcurrentBag<DuplicateGroup>();

        try
        {
            Parallel.ForEach(sizeGroups,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                sizeGroup =>
                {
                    var byPartial = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
                    foreach (var file in sizeGroup)
                    {
                        ct.ThrowIfCancellationRequested();
                        string? partial = TryHash(file.Path, PartialHashBytes);
                        int current = Interlocked.Increment(ref done);
                        progress?.Report(new DuplicateScanProgress
                        {
                            Phase = "hash", Done = current, Total = totalCandidates
                        });
                        if (partial is null) continue; // gesperrt/nicht lesbar -> überspringen

                        if (!byPartial.TryGetValue(partial, out var list))
                            byPartial[partial] = list = new List<DuplicateFile>();
                        list.Add(file);
                    }

                    foreach (var candidates in byPartial.Values.Where(l => l.Count > 1))
                    {
                        var byFull = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
                        foreach (var file in candidates)
                        {
                            ct.ThrowIfCancellationRequested();
                            string? full = TryHash(file.Path, maxBytes: null);
                            if (full is null) continue;

                            if (!byFull.TryGetValue(full, out var list))
                                byFull[full] = list = new List<DuplicateFile>();
                            list.Add(file);
                        }

                        foreach (var (hash, dupes) in byFull.Where(kv => kv.Value.Count > 1))
                        {
                            result.Add(new DuplicateGroup
                            {
                                Hash = hash,
                                SizeBytes = dupes[0].SizeBytes,
                                Files = dupes.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()
                            });
                        }
                    }
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return result
            .OrderByDescending(g => g.WastedBytes)
            .ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sammelt alle Dateien der Ordner rekursiv ein. Gesperrte Unterordner werden
    /// übersprungen; Symlinks/Junctions nicht verfolgt (keine Schleifen/Doppelzählung).
    /// </summary>
    private static List<DuplicateFile> CollectFiles(IReadOnlyList<string> folders,
        IProgress<DuplicateScanProgress>? progress, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<DuplicateFile>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

            IEnumerable<FileInfo> enumeration;
            try { enumeration = new DirectoryInfo(folder).EnumerateFiles("*", options); }
            catch { continue; }

            foreach (var info in enumeration)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!seen.Add(info.FullName)) continue; // überlappende Ordner

                    files.Add(new DuplicateFile
                    {
                        Path = info.FullName,
                        SizeBytes = info.Length,
                        Created = info.CreationTime,
                        Modified = info.LastWriteTime
                    });

                    if (files.Count % 200 == 0)
                        progress?.Report(new DuplicateScanProgress
                        {
                            Phase = "collect", Done = files.Count, Total = 0
                        });
                }
                catch { /* einzelne Datei nicht lesbar -> überspringen */ }
            }
        }

        return files;
    }

    /// <summary>
    /// SHA-256 einer Datei als Hex-String; bei <paramref name="maxBytes"/> nur über
    /// die ersten Bytes (Teil-Hash). Null, wenn die Datei nicht lesbar ist.
    /// </summary>
    private static string? TryHash(string path, int? maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1024 * 128);
            using var sha = SHA256.Create();

            if (maxBytes is int limit)
            {
                var buffer = new byte[Math.Min(limit, 1024 * 128)];
                long remaining = limit;
                int read;
                while (remaining > 0 &&
                       (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    remaining -= read;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return null; // gesperrt/gelöscht/kein Zugriff
        }
    }
}
