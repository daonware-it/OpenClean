namespace OpenClean.Models;

/// <summary>
/// Eine einzelne Datei innerhalb einer Duplikat-Gruppe. Reines Datenobjekt –
/// beschreibt nur, was gefunden wurde; gelöscht wird erst nach Nutzer-Auswahl.
/// </summary>
public sealed class DuplicateFile
{
    /// <summary>Vollständiger Pfad der Datei.</summary>
    public required string Path { get; init; }

    /// <summary>Dateigröße in Bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Erstellungszeitpunkt (lokal).</summary>
    public DateTime Created { get; init; }

    /// <summary>Letzte Änderung (lokal).</summary>
    public DateTime Modified { get; init; }
}

/// <summary>
/// Eine Gruppe inhaltsgleicher Dateien (identischer SHA-256-Hash und identische
/// Größe). Enthält immer mindestens zwei Dateien.
/// </summary>
public sealed class DuplicateGroup
{
    /// <summary>SHA-256-Hash des Dateiinhalts (hex, klein geschrieben).</summary>
    public required string Hash { get; init; }

    /// <summary>Größe EINER Datei der Gruppe (alle sind gleich groß).</summary>
    public long SizeBytes { get; init; }

    /// <summary>Alle Fundorte, sortiert nach Pfad.</summary>
    public required IReadOnlyList<DuplicateFile> Files { get; init; }

    /// <summary>Verschwendeter Speicher: alle Kopien außer einer.</summary>
    public long WastedBytes => SizeBytes * (Files.Count - 1);
}

/// <summary>Fortschritt eines Duplikat-Scans (für die UI-Statusanzeige).</summary>
public sealed class DuplicateScanProgress
{
    /// <summary>Aktuelle Phase: "collect" (Dateien einsammeln) oder "hash" (Inhalte prüfen).</summary>
    public required string Phase { get; init; }

    /// <summary>Bereits verarbeitete Dateien der Phase.</summary>
    public int Done { get; init; }

    /// <summary>Gesamtzahl der Phase (0 = noch unbekannt).</summary>
    public int Total { get; init; }
}
