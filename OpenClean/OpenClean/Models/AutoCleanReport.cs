namespace OpenClean.Models;

/// <summary>
/// Bericht EINER automatischen (geplanten) Reinigung. Wird nach jedem Lauf persistiert,
/// damit der Nutzer im Bereich „Zeitplan" nachvollziehen kann, was gelöscht wurde –
/// auch wenn die Reinigung unbeaufsichtigt lief.
/// </summary>
public sealed class AutoCleanReport
{
    /// <summary>Zeitpunkt der Reinigung (lokale Zeit).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Verwendetes Profil ("TempOnly" / "Full" / "Custom").</summary>
    public string Profile { get; set; } = "";

    /// <summary>Anzahl gelöschter Objekte (Dateien/Ordner).</summary>
    public int DeletedCount { get; set; }

    /// <summary>Freigegebener Speicher in Bytes.</summary>
    public long FreedBytes { get; set; }

    /// <summary>Anzahl übersprungener Objekte (in Benutzung / kein Zugriff).</summary>
    public int SkippedCount { get; set; }

    /// <summary>
    /// Pfade der gelöschten Objekte – für die Nachvollziehbarkeit im Bericht.
    /// Aus Größengründen gedeckelt (siehe <see cref="Services.AutoCleanReportStore"/>).
    /// </summary>
    public List<string> DeletedPaths { get; set; } = new();

    /// <summary>True, wenn die Liste der Pfade gedeckelt wurde (es gab mehr als gespeichert).</summary>
    public bool PathsTruncated { get; set; }
}
