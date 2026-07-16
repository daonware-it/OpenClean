using System.IO;
using OpenClean.Models;

namespace OpenClean.Services.Safety;

/// <summary>
/// Ein laufender Backup-Durchlauf: sichert einzelne Objekte VOR dem Löschen und löscht sie
/// anschließend. Nicht threadsicher – pro Durchlauf von genau einem Worker benutzen. Am Ende
/// <see cref="Commit"/> aufrufen (oder <c>using</c> nutzen), damit das Manifest geschrieben und
/// die Aufbewahrung angestoßen wird.
/// </summary>
public sealed class BackupSession : IDisposable
{
    private readonly BackupService _service;
    private readonly BackupManifest _manifest;
    private readonly string _filesDir;
    private readonly IntPtr _ownerWindow;
    private int _backupIndex;
    private bool _committed;

    internal BackupSession(BackupService service, string dir, BackupManifest manifest, IntPtr ownerWindow)
    {
        _service = service;
        DirectoryPath = dir;
        _manifest = manifest;
        _filesDir = Path.Combine(dir, "files");
        _ownerWindow = ownerWindow;
    }

    /// <summary>Eindeutige Id des Durchlaufs (= Ordnername).</summary>
    public string Id => _manifest.Id;

    /// <summary>Vollständiger Pfad des Sitzungsordners.</summary>
    public string DirectoryPath { get; }

    /// <summary>Anzahl bisher gesicherter Objekte.</summary>
    public int Count => _manifest.Entries.Count;

    /// <summary>
    /// Sichert das Objekt gemäß Strategie und löscht es anschließend. Gibt <see cref="SafeDeleteOutcome.Skipped"/>
    /// zurück, wenn das Objekt NICHT gelöscht wurde (Sicherung fehlgeschlagen, gesperrt, kein Zugriff) –
    /// in diesem Fall bleibt das Original unangetastet.
    /// </summary>
    public SafeDeleteOutcome TryDelete(string path, bool isDirectory, long sizeBytes, SafeDeleteStrategy strategy)
    {
        try
        {
            // Existiert gar nicht (mehr) -> als erledigt behandeln, kein Backup nötig.
            if (isDirectory ? !Directory.Exists(path) : !File.Exists(path))
                return SafeDeleteOutcome.Deleted;

            if (strategy is SafeDeleteStrategy.PreferRecycle or SafeDeleteStrategy.RecycleOnly)
            {
                if (TryRecycle(path, isDirectory, sizeBytes))
                    return SafeDeleteOutcome.Deleted;

                // RecycleOnly: keine Kopie als Ausweg -> Original bleibt stehen.
                if (strategy == SafeDeleteStrategy.RecycleOnly)
                    return SafeDeleteOutcome.Skipped;

                // Papierkorb nicht möglich (z. B. zu groß/gesperrt) -> auf Backup-Kopie ausweichen.
            }

            return BackupAndDelete(path, isDirectory, sizeBytes)
                ? SafeDeleteOutcome.Deleted
                : SafeDeleteOutcome.Skipped;
        }
        catch
        {
            return SafeDeleteOutcome.Skipped;
        }
    }

    /// <summary>
    /// Wie <see cref="TryDelete"/>, aber für einen ganzen Stapel – die Ergebnisse kommen in der
    /// Reihenfolge der übergebenen Objekte zurück.
    ///
    /// <para>Bei <see cref="SafeDeleteStrategy.PreferRecycle"/> geht der ganze Stapel in EINEM
    /// Shell-Aufruf in den Papierkorb, statt je Objekt einen eigenen STA-Thread plus
    /// SHFileOperation zu kosten. Genau die Objekte, die der Papierkorb nicht genommen hat, weichen
    /// einzeln auf die Backup-Kopie aus – identisch zum Einzelpfad.</para>
    ///
    /// <para><see cref="SafeDeleteStrategy.PreferBackup"/> kopiert ohnehin Datei für Datei; dort
    /// gibt es nichts zu bündeln, der Stapel wird schlicht durchlaufen.</para>
    /// </summary>
    public IReadOnlyList<SafeDeleteOutcome> TryDeleteMany(
        IReadOnlyList<SafeDeleteRequest> items, SafeDeleteStrategy strategy)
    {
        var outcomes = new SafeDeleteOutcome[items.Count];

        if (strategy is not (SafeDeleteStrategy.PreferRecycle or SafeDeleteStrategy.RecycleOnly))
        {
            for (int i = 0; i < items.Count; i++)
                outcomes[i] = TryDelete(items[i].Path, items[i].IsDirectory, items[i].SizeBytes, strategy);
            return outcomes;
        }

        // Nicht (mehr) vorhandene Objekte gelten als erledigt und gehören nicht in den Shell-Stapel.
        var pending = new List<int>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            try
            {
                if (it.IsDirectory ? !Directory.Exists(it.Path) : !File.Exists(it.Path))
                    outcomes[i] = SafeDeleteOutcome.Deleted;
                else
                    pending.Add(i);
            }
            catch
            {
                outcomes[i] = SafeDeleteOutcome.Skipped;
            }
        }

        if (pending.Count == 0) return outcomes;

        var paths = new List<string>(pending.Count);
        foreach (int i in pending) paths.Add(items[i].Path);

        IReadOnlyList<string> failedPaths;
        try { failedPaths = RecycleBin.MoveToRecycleBin(paths, _ownerWindow); }
        catch { failedPaths = paths; }

        var failed = new HashSet<string>(failedPaths, StringComparer.OrdinalIgnoreCase);

        foreach (int i in pending)
        {
            var it = items[i];

            if (!failed.Contains(it.Path))
            {
                _manifest.Entries.Add(new BackupEntry
                {
                    OriginalPath = it.Path,
                    IsDirectory = it.IsDirectory,
                    SizeBytes = it.SizeBytes,
                    Method = BackupMethod.RecycleBin
                });
                outcomes[i] = SafeDeleteOutcome.Deleted;
                continue;
            }

            // RecycleOnly: keine Kopie als Ausweg -> Original bleibt stehen.
            if (strategy == SafeDeleteStrategy.RecycleOnly)
            {
                outcomes[i] = SafeDeleteOutcome.Skipped;
                continue;
            }

            // Papierkorb nicht möglich (zu groß/gesperrt) -> auf Backup-Kopie ausweichen.
            try
            {
                outcomes[i] = BackupAndDelete(it.Path, it.IsDirectory, it.SizeBytes)
                    ? SafeDeleteOutcome.Deleted
                    : SafeDeleteOutcome.Skipped;
            }
            catch
            {
                outcomes[i] = SafeDeleteOutcome.Skipped;
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Verschiebt das Objekt in den Papierkorb. Die konkreten $R-/$I-Pfade werden hier bewusst
    /// NICHT aufgelöst: Das würde je Datei den kompletten Papierkorb durchsuchen (O(n²) über einen
    /// ganzen Reinigungslauf). Stattdessen merkt sich das Manifest nur den Originalpfad; beim (seltenen)
    /// Undo werden die Papierkorb-Einträge einmalig darüber zugeordnet.
    /// </summary>
    private bool TryRecycle(string path, bool isDirectory, long sizeBytes)
    {
        try
        {
            // Owner-Fenster durchreichen: Zeigt Windows bei einem für den Papierkorb zu großen
            // Objekt trotz FOF_NOCONFIRMATION eine Nuke-Warnung, erscheint sie so modal/sichtbar
            // zum Hauptfenster statt dahinter (dieser Pfad hat bewusst kein Timeout).
            var failed = RecycleBin.MoveToRecycleBin(new[] { path }, _ownerWindow);
            if (failed.Count > 0) return false;

            _manifest.Entries.Add(new BackupEntry
            {
                OriginalPath = path,
                IsDirectory = isDirectory,
                SizeBytes = sizeBytes,
                Method = BackupMethod.RecycleBin
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kopiert das Objekt ins Backup-Verzeichnis und löscht dann das Original.</summary>
    private bool BackupAndDelete(string path, bool isDirectory, long sizeBytes)
    {
        string relative = Path.Combine("files", $"{_backupIndex:D6}_{SanitizeName(Path.GetFileName(path))}");
        string target = Path.Combine(DirectoryPath, relative);

        try
        {
            Directory.CreateDirectory(_filesDir);

            if (isDirectory)
            {
                // Schlägt auch nur EINE Datei im Baum fehl, gilt die Sicherung als unvollständig:
                // dann darf das Original NICHT gelöscht werden (sonst Datenverlust ohne Backup).
                if (!CopyDirectory(path, target))
                {
                    TryRemove(target, isDirectory);
                    return false;
                }
            }
            else
            {
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(path, target, overwrite: true);
            }
        }
        catch
        {
            // Kopie fehlgeschlagen -> NICHT löschen, Kopierrest entfernen.
            TryRemove(target, isDirectory);
            return false;
        }

        // Kopie steht -> Original löschen.
        try
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                var fi = new FileInfo(path) { Attributes = FileAttributes.Normal };
                fi.Delete();
            }
        }
        catch
        {
            // Löschen fehlgeschlagen -> Kopie verwerfen (kein verwaistes Backup) und überspringen.
            TryRemove(target, isDirectory);
            return false;
        }

        _backupIndex++;
        _manifest.Entries.Add(new BackupEntry
        {
            OriginalPath = path,
            IsDirectory = isDirectory,
            SizeBytes = sizeBytes,
            Method = BackupMethod.Backup,
            BackupRelativePath = relative.Replace('\\', '/')
        });
        return true;
    }

    /// <summary>Schreibt das Manifest und stößt die Aufbewahrungs-Bereinigung an. Leere Durchläufe werden verworfen.</summary>
    public void Commit()
    {
        if (_committed) return;
        _committed = true;

        if (_manifest.Entries.Count == 0)
        {
            BackupService.TryDeleteDirectory(DirectoryPath);
            return;
        }

        _service.SaveManifest(DirectoryPath, _manifest);
        _service.CleanupOld();
    }

    public void Dispose() => Commit();

    // ---- Hilfen -------------------------------------------------------------

    /// <summary>
    /// Kopiert einen Verzeichnisbaum vollständig. Gibt <c>false</c> zurück, wenn auch nur eine Datei
    /// nicht kopiert werden konnte – der Aufrufer darf das Original dann nicht löschen.
    /// </summary>
    private static bool CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));

        bool complete = true;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            try { File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true); }
            catch { complete = false; } // gesperrte/nicht lesbare Datei -> Sicherung unvollständig
        }
        return complete;
    }

    private static void TryRemove(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            else { if (File.Exists(path)) File.Delete(path); }
        }
        catch { /* egal */ }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "item";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }
}
