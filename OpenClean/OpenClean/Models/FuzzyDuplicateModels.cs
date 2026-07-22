namespace OpenClean.Models;

/// <summary>
/// Optionen der Ähnlichkeits-Duplikatsuche (visuell ähnliche Bilder). Reines DTO –
/// die eigentliche Suche (Perceptual-Hash) liegt ausschließlich im Premium-Modul.
/// </summary>
public sealed class FuzzyScanOptions
{
    /// <summary>
    /// Ähnlichkeitsschwelle in Prozent (50–100). Höher = strenger, d. h. nur sehr
    /// ähnliche Bilder werden zusammengefasst. Standard: 90.
    /// </summary>
    public int SimilarityThreshold { get; init; } = 90;

    /// <summary>Dateien kleiner als dieser Wert (Bytes) werden ignoriert. 0 = keine Untergrenze.</summary>
    public long MinFileBytes { get; init; }

    /// <summary>Begrenzt <paramref name="value"/> auf den gültigen Bereich 50..100.</summary>
    public static int NormalizeThreshold(int value)
    {
        if (value < 50) return 50;
        if (value > 100) return 100;
        return value;
    }
}

/// <summary>
/// Eine einzelne Datei innerhalb einer Ähnlichkeits-Duplikat-Gruppe. Reines Datenobjekt –
/// beschreibt nur, was gefunden wurde; gelöscht wird erst nach Nutzer-Auswahl.
/// </summary>
public sealed class FuzzyDuplicateFile
{
    /// <summary>Vollständiger Pfad der Datei.</summary>
    public required string Path { get; init; }

    /// <summary>Dateigröße in Bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Erstellungszeitpunkt (lokal).</summary>
    public DateTime Created { get; init; }

    /// <summary>Letzte Änderung (lokal).</summary>
    public DateTime Modified { get; init; }

    /// <summary>Ähnlichkeit (0–100) zum Referenzbild der Gruppe; 100 = praktisch identisch.</summary>
    public int SimilarityScore { get; init; }
}

/// <summary>
/// Eine Gruppe visuell ähnlicher Bilder (nach Perceptual-Hash). Enthält immer mindestens
/// zwei Dateien; <see cref="ReferencePath"/> ist das Bezugsbild, gegen das die Ähnlichkeit
/// der übrigen Dateien gemessen wurde.
/// </summary>
public sealed class FuzzyDuplicateGroup
{
    /// <summary>Repräsentativer Referenzpfad der Gruppe (Bezugsbild für die Ähnlichkeit).</summary>
    public required string ReferencePath { get; init; }

    /// <summary>Alle ähnlichen Bilder der Gruppe (inklusive Referenzbild).</summary>
    public required IReadOnlyList<FuzzyDuplicateFile> Files { get; init; }

    /// <summary>Verschwendeter Speicher: alle Dateien außer der größten (die behalten wird).</summary>
    public long WastedBytes => Files.Sum(f => f.SizeBytes) - Files.Max(f => f.SizeBytes);
}
