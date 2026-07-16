using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClean.Models;

namespace OpenClean.Services.Safety;

/// <summary>Strategie, wie ein Objekt vor dem Löschen gesichert wird.</summary>
public enum SafeDeleteStrategy
{
    /// <summary>
    /// Regenerierbare Cache-/Temp-Objekte: in den Papierkorb verschieben (spart doppelten
    /// Speicher). Scheitert das, wird ersatzweise ins Backup-Verzeichnis kopiert.
    /// </summary>
    PreferRecycle,

    /// <summary>Echte Nutzerdaten: immer in das Backup-Verzeichnis kopieren (zuverlässiges App-Undo).</summary>
    PreferBackup,

    /// <summary>
    /// Nur Papierkorb, NIEMALS eine Backup-Kopie. Für große Dateien: eine mehrere GB große Datei erst
    /// ins Backup-Verzeichnis zu kopieren würde den Platzbedarf kurzzeitig verdoppeln und dem Zweck
    /// (Platz schaffen) genau zuwiderlaufen. Nimmt der Papierkorb das Objekt nicht, gilt es als
    /// übersprungen – das Original bleibt unangetastet.
    /// </summary>
    RecycleOnly
}

/// <summary>Ergebnis eines gesicherten Löschversuchs.</summary>
public enum SafeDeleteOutcome
{
    /// <summary>Objekt wurde gesichert und gelöscht.</summary>
    Deleted,

    /// <summary>Objekt wurde übersprungen (gesperrt/kein Zugriff/Sicherung fehlgeschlagen) – NICHT gelöscht.</summary>
    Skipped
}

/// <summary>Ein zu löschendes Objekt für die Stapelverarbeitung (siehe <see cref="BackupSession.TryDeleteMany"/>).</summary>
public readonly record struct SafeDeleteRequest(string Path, bool IsDirectory, long SizeBytes);

/// <summary>Ergebnis eines Wiederherstellungs-Durchlaufs (Undo).</summary>
public sealed record UndoResult(int Restored, int Failed, long RestoredBytes);

/// <summary>
/// Sichert vor dem Löschen stehende Dateien/Ordner und ermöglicht ein gezieltes Rückgängigmachen
/// pro Bereinigungs-Durchlauf. Jeder Durchlauf ist ein <see cref="BackupSession"/> mit eigenem
/// Ordner (<c>&lt;Datenverzeichnis&gt;\Backups\&lt;Id&gt;\</c>) und <c>manifest.json</c>.
///
/// <para>Das Backup-Verzeichnis liegt bewusst im OpenClean-Datenverzeichnis (portabel neben der
/// EXE bzw. <c>%AppData%\OpenClean</c>) – also AUSSERHALB der Reinigungsziele (%TEMP%), damit die
/// Reinigung ihre eigenen Sicherungen nicht wieder mitlöscht.</para>
/// </summary>
public sealed class BackupService
{
    /// <summary>Prozessweiter Singleton.</summary>
    public static BackupService Instance { get; } = new();

    private BackupService() { }

    // Retention: siehe CleanupOld.
    private const int RetentionDays = 7;
    private const int RetentionRuns = 5;
    private const long RetentionMaxBytes = 2L * 1024 * 1024 * 1024; // 2 GB

    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();

    /// <summary>Wurzelverzeichnis aller Backup-Durchläufe.</summary>
    public string StoreRoot => Path.Combine(AppPaths.DataDirectory, "Backups");

    /// <summary>
    /// Beginnt einen neuen Backup-Durchlauf für den angegebenen Herkunftsbereich.
    /// <paramref name="ownerWindow"/> ist das Eigentümerfenster-Handle für einen etwaigen
    /// Papierkorb-Nuke-Dialog (0 = keins).
    /// </summary>
    public BackupSession BeginSession(string area, IntPtr ownerWindow = default)
    {
        var now = DateTime.Now;
        // yyyyMMdd_HHmmss (sortierbar) + kurzer Zufallsanteil gegen Kollisionen im selben Sekundentakt.
        string id = $"{now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
        string dir = Path.Combine(StoreRoot, id);
        Directory.CreateDirectory(dir);

        var manifest = new BackupManifest
        {
            Id = id,
            Area = area,
            CreatedUtc = DateTime.UtcNow.ToString("o")
        };
        return new BackupSession(this, dir, manifest, ownerWindow);
    }

    /// <summary>Listet alle vorhandenen Durchläufe (neueste zuerst), best effort.</summary>
    public IReadOnlyList<BackupManifest> ListSessions()
    {
        var result = new List<BackupManifest>();
        try
        {
            if (!Directory.Exists(StoreRoot)) return result;
            foreach (var dir in Directory.GetDirectories(StoreRoot))
            {
                var manifest = TryLoadManifest(dir);
                if (manifest is not null) result.Add(manifest);
            }
        }
        catch { /* best effort */ }

        result.Sort((a, b) => string.CompareOrdinal(b.CreatedUtc, a.CreatedUtc));
        return result;
    }

    /// <summary>Spielt alle noch nicht wiederhergestellten Objekte eines Durchlaufs zurück.</summary>
    public UndoResult Restore(string id, IProgress<string>? progress = null)
    {
        string dir = Path.Combine(StoreRoot, id);
        var manifest = TryLoadManifest(dir);
        if (manifest is null) return new UndoResult(0, 0, 0);

        int restored = 0, failed = 0;
        long restoredBytes = 0;
        var remaining = new List<BackupEntry>();

        // Papierkorb-Zuordnung nur EINMAL aufbauen (und nur, wenn der Lauf überhaupt Papierkorb-
        // Einträge enthält) – Originalpfad -> ($R/$I). Das vermeidet ein O(n²)-Durchsuchen.
        Dictionary<string, (string data, string meta)>? recycleMap = null;
        if (manifest.Entries.Exists(e => e.Method == BackupMethod.RecycleBin))
            recycleMap = BuildRecycleMap();

        foreach (var entry in manifest.Entries)
        {
            progress?.Report(entry.OriginalPath);
            bool ok = RestoreEntry(dir, entry, recycleMap);
            if (ok)
            {
                restored++;
                restoredBytes += entry.SizeBytes;
            }
            else
            {
                failed++;
                remaining.Add(entry);
            }
        }

        // Vollständig wiederhergestellt -> Sitzung entfernen; sonst Manifest auf Reste eindampfen.
        if (remaining.Count == 0)
        {
            TryDeleteDirectory(dir);
        }
        else
        {
            manifest.Entries = remaining;
            SaveManifest(dir, manifest);
        }

        return new UndoResult(restored, failed, restoredBytes);
    }

    /// <summary>Entfernt einen Durchlauf endgültig (Backup-Kopien werden gelöscht; Papierkorb bleibt unberührt).</summary>
    public bool DeleteSession(string id)
    {
        string dir = Path.Combine(StoreRoot, id);
        return TryDeleteDirectory(dir);
    }

    /// <summary>
    /// Räumt alte Durchläufe auf: es bleiben Durchläufe der letzten <see cref="RetentionDays"/> Tage
    /// ODER die <see cref="RetentionRuns"/> jüngsten erhalten; überschreitet die Gesamtgröße
    /// <see cref="RetentionMaxBytes"/>, werden zusätzlich die ältesten entfernt.
    /// </summary>
    public void CleanupOld()
    {
        lock (_sync)
        {
            try
            {
                var sessions = ListSessions(); // neueste zuerst
                if (sessions.Count == 0) return;

                DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

                var keep = new List<BackupManifest>();
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    bool amongNewest = i < RetentionRuns;
                    bool recent = DateTime.TryParse(s.CreatedUtc, null,
                                      System.Globalization.DateTimeStyles.RoundtripKind, out var created)
                                  && created.ToUniversalTime() >= cutoff;

                    if (amongNewest || recent) keep.Add(s);
                    else DeleteSession(s.Id);
                }

                // Größenobergrenze: älteste der verbliebenen entfernen, bis unter dem Limit.
                long total = 0;
                foreach (var s in keep) total += DirectorySize(Path.Combine(StoreRoot, s.Id));

                for (int i = keep.Count - 1; i >= 1 && total > RetentionMaxBytes; i--)
                {
                    string sdir = Path.Combine(StoreRoot, keep[i].Id);
                    total -= DirectorySize(sdir);
                    DeleteSession(keep[i].Id);
                }
            }
            catch { /* Aufräumen ist best effort */ }
        }
    }

    // ---- intern: von BackupSession genutzt ----------------------------------

    internal void SaveManifest(string dir, BackupManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions));
        }
        catch { /* nicht schreibbar -> Undo für diesen Lauf evtl. nicht verfügbar */ }
    }

    internal static bool TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch { return false; }
    }

    private BackupManifest? TryLoadManifest(string dir)
    {
        try
        {
            string file = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(file)) return null;
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file), JsonOptions);
            if (manifest is null) return null;
            manifest.Id = Path.GetFileName(dir);
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Baut aus dem aktuellen Papierkorb-Inhalt eine Zuordnung Originalpfad -> ($R/$I).</summary>
    private static Dictionary<string, (string data, string meta)> BuildRecycleMap()
    {
        var map = new Dictionary<string, (string data, string meta)>(StringComparer.OrdinalIgnoreCase);
        var newest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var e in RecycleBin.Enumerate())
            {
                DateTime created;
                try { created = File.GetCreationTimeUtc(e.MetaPath); }
                catch { created = DateTime.MinValue; }

                // Bei mehreren Einträgen mit gleichem Originalpfad den jüngsten behalten.
                if (!newest.TryGetValue(e.OriginalPath, out var prev) || created >= prev)
                {
                    map[e.OriginalPath] = (e.DataPath, e.MetaPath);
                    newest[e.OriginalPath] = created;
                }
            }
        }
        catch { /* best effort */ }

        return map;
    }

    private static bool RestoreEntry(string sessionDir, BackupEntry entry,
        Dictionary<string, (string data, string meta)>? recycleMap)
    {
        try
        {
            if (entry.Method == BackupMethod.RecycleBin)
            {
                // $R/$I zur Undo-Zeit über den Originalpfad zuordnen (siehe BuildRecycleMap).
                if (recycleMap is null ||
                    !recycleMap.TryGetValue(entry.OriginalPath, out var loc))
                    return false;
                return RecycleBin.RestoreEntry(loc.data, loc.meta, entry.OriginalPath);
            }

            // Backup-Kopie: nichts überschreiben.
            if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath)) return false;
            if (string.IsNullOrEmpty(entry.BackupRelativePath)) return false;

            string source = Path.Combine(sessionDir, entry.BackupRelativePath);
            bool srcDir = Directory.Exists(source);
            bool srcFile = File.Exists(source);
            if (!srcDir && !srcFile) return false;

            string? parent = Path.GetDirectoryName(entry.OriginalPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (srcDir) MoveDirectory(source, entry.OriginalPath);
            else File.Move(source, entry.OriginalPath); // File.Move funktioniert laufwerksübergreifend
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verschiebt ein Verzeichnis an sein Ziel. <see cref="Directory.Move"/> scheitert laufwerks-
    /// übergreifend (der Backup-Store und der Originalort können auf verschiedenen Laufwerken liegen);
    /// in dem Fall wird der Baum kopiert und die Quelle danach entfernt. Wirft bei Fehlschlag, sodass
    /// der Aufrufer die Sicherung erhält und den Eintrag als „nicht wiederhergestellt" behält.
    /// </summary>
    private static void MoveDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
            return;
        }
        catch (IOException)
        {
            // Laufwerksübergreifend -> kopieren, dann Quelle löschen.
        }

        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: false);

        // Erst nach vollständigem Kopieren die Backup-Quelle entfernen.
        Directory.Delete(source, recursive: true);
    }

    internal static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* gesperrt -> überspringen */ }
            }
        }
        catch { /* Zugriff verweigert -> Teilsumme */ }
        return total;
    }
}
