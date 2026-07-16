using System.Text.Json.Serialization;

namespace OpenClean.Models;

/// <summary>
/// Art, wie ein gelöschtes Objekt vor der Löschung gesichert wurde – entscheidet, wie das
/// Rückgängigmachen abläuft.
/// </summary>
public enum BackupMethod
{
    /// <summary>Datei/Ordner wurde in das OpenClean-Backup-Verzeichnis kopiert (App-Undo restauriert von dort).</summary>
    Backup,

    /// <summary>Objekt wurde in den Windows-Papierkorb verschoben (Undo holt es über die $R/$I-Struktur zurück).</summary>
    RecycleBin
}

/// <summary>
/// Ein einzelnes vor der Löschung gesichertes Objekt innerhalb eines Backup-Durchlaufs.
/// Trägt genug Information, um es an den Originalort zurückzuspielen.
/// </summary>
public sealed class BackupEntry
{
    /// <summary>Ursprünglicher, vollständiger Pfad (Ziel eines späteren Restores).</summary>
    public string OriginalPath { get; set; } = "";

    /// <summary>True, wenn ein Verzeichnis (statt einer Datei) gesichert wurde.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>Größe in Bytes (für Anzeige und Retention-Berechnung).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Sicherungsart (Backup-Kopie oder Papierkorb).</summary>
    public BackupMethod Method { get; set; }

    /// <summary>
    /// Bei <see cref="BackupMethod.Backup"/>: Pfad der Sicherungskopie RELATIV zum Sitzungsordner
    /// (portabel, falls das Datenverzeichnis verschoben wird). Sonst null.
    /// </summary>
    public string? BackupRelativePath { get; set; }
}

/// <summary>
/// Beschreibt genau EINEN Bereinigungs-Durchlauf, dessen gelöschte Objekte gesichert wurden.
/// Wird als <c>manifest.json</c> im Sitzungsordner (<c>&lt;Datenverzeichnis&gt;\Backups\&lt;Id&gt;\</c>)
/// abgelegt und ermöglicht ein gezieltes Undo pro Durchlauf.
/// </summary>
public sealed class BackupManifest
{
    /// <summary>Format-Version des Manifests (für spätere Migrationen).</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Verzeichnisname des Sitzungsordners = eindeutige Id des Durchlaufs. Nicht serialisiert
    /// (wird beim Laden aus dem Ordnernamen gesetzt), damit Umbenennen nicht zu Inkonsistenzen führt.
    /// </summary>
    [JsonIgnore]
    public string Id { get; set; } = "";

    /// <summary>
    /// Herkunftsbereich des Durchlaufs (Loc-Key-Suffix): "cleanup", "duplicates", "leftover",
    /// "privacy.recent", … – für die Anzeige in der Wiederherstellen-Ansicht.
    /// </summary>
    public string Area { get; set; } = "";

    /// <summary>Erstellzeitpunkt in UTC (ISO 8601), für Sortierung und Retention.</summary>
    public string CreatedUtc { get; set; } = "";

    /// <summary>Die gesicherten Objekte dieses Durchlaufs.</summary>
    public List<BackupEntry> Entries { get; set; } = new();

    /// <summary>Anzahl gesicherter Objekte.</summary>
    [JsonIgnore]
    public int Count => Entries.Count;

    /// <summary>Gesamtgröße der gesicherten Objekte in Bytes.</summary>
    [JsonIgnore]
    public long TotalBytes
    {
        get
        {
            long sum = 0;
            foreach (var e in Entries) sum += e.SizeBytes;
            return sum;
        }
    }
}
