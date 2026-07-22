namespace OpenClean.Models;

/// <summary>
/// Optionen des sicheren Löschens. Reines DTO – das mehrfache Überschreiben und das
/// endgültige Entfernen liegen ausschließlich im Premium-Modul.
/// </summary>
public sealed class SecureDeleteOptions
{
    /// <summary>Erlaubte Überschreib-Durchgänge (mehr = gründlicher, aber langsamer).</summary>
    public static readonly IReadOnlyList<int> AllowedPasses = new[] { 1, 3, 7 };

    /// <summary>Anzahl der Überschreib-Durchgänge; erlaubt sind {1, 3, 7}. Standard: 1.</summary>
    public int Passes { get; init; } = 1;

    /// <summary>Liefert den nächstgelegenen erlaubten Wert aus {1, 3, 7}.</summary>
    public static int NormalizePasses(int value)
    {
        int best = AllowedPasses[0];
        int bestDistance = Math.Abs(value - best);
        foreach (int allowed in AllowedPasses)
        {
            int distance = Math.Abs(value - allowed);
            if (distance < bestDistance)
            {
                best = allowed;
                bestDistance = distance;
            }
        }
        return best;
    }
}

/// <summary>Fortschritt eines sicheren Löschlaufs (für die UI-Statusanzeige).</summary>
public sealed class SecureDeleteProgress
{
    /// <summary>Vollständiger Pfad der aktuell verarbeiteten Datei.</summary>
    public required string CurrentFile { get; init; }

    /// <summary>Index der aktuellen Datei (1-basiert).</summary>
    public int FileIndex { get; init; }

    /// <summary>Gesamtzahl der zu löschenden Dateien.</summary>
    public int FileCount { get; init; }

    /// <summary>Aktueller Überschreib-Durchgang (1-basiert).</summary>
    public int CurrentPass { get; init; }

    /// <summary>Gesamtzahl der Überschreib-Durchgänge.</summary>
    public int PassCount { get; init; }
}

/// <summary>Ergebnis eines sicheren Löschlaufs (Zusammenfassung nach Abschluss).</summary>
public sealed class SecureDeleteResult
{
    /// <summary>Anzahl der erfolgreich und unwiderruflich gelöschten Dateien.</summary>
    public int DeletedCount { get; init; }

    /// <summary>Anzahl der Dateien, die nicht gelöscht werden konnten.</summary>
    public int FailedCount { get; init; }

    /// <summary>Pfade der Dateien, die nicht gelöscht werden konnten.</summary>
    public IReadOnlyList<string> FailedPaths { get; init; } = Array.Empty<string>();
}
