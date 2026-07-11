namespace OpenClean.Models;

/// <summary>
/// Bewertungsstufe des Gesamt-Scores.
/// </summary>
public enum ScoreLevel
{
    /// <summary>≥ 80 – keine oder nur geringe messbare Probleme.</summary>
    Good,
    /// <summary>50–79 – Verbesserungspotenzial vorhanden.</summary>
    Improvable,
    /// <summary>&lt; 50 – deutlicher Handlungsbedarf.</summary>
    Critical
}

/// <summary>
/// Art eines Score-Faktors – bestimmt das thematische Icon in der Overview
/// (Laufwerk, bereinigbarer Speicher, Autostart, RAM).
/// </summary>
public enum ScoreFactorKind
{
    Storage,
    Reclaimable,
    Startup,
    Ram
}

/// <summary>
/// Ein einzelner, nachvollziehbarer Faktor, der den Score gemindert hat.
/// <paramref name="Points"/> ist der (negative) Punktabzug, z. B. -10.
/// </summary>
public sealed record ScoreFactor(string Description, int Points, ScoreFactorKind Kind)
{
    /// <summary>Anzeige wie „−10 Punkte“ (echtes Minuszeichen).</summary>
    public string PointsDisplay => $"−{Math.Abs(Points)}";

    /// <summary>Balkenlänge relativ zum größten Einzelabzug (Kappung bei 25 Punkten) in Prozent.</summary>
    public double BarPercent => System.Math.Min(System.Math.Abs(Points) / 25.0 * 100.0, 100.0);
}

/// <summary>
/// Ergebnis der Score-Berechnung: ausschließlich aus echten Messwerten abgeleitet.
/// Enthält die Einzel-Faktoren, damit die UI transparent zeigen kann, WORAUS sich
/// der Score ergibt (kein Fake-/Marketing-Score).
/// </summary>
public sealed class ScoreResult
{
    /// <summary>Score von 0 (kritisch) bis 100 (optimal).</summary>
    public int Score { get; init; }

    public ScoreLevel Level { get; init; }

    /// <summary>„Gut“ / „Verbesserbar“ / „Kritisch“.</summary>
    public string Label { get; init; } = "";

    /// <summary>Die einzelnen Punktabzüge (leer = 100/„Gut“, nichts messbar).</summary>
    public IReadOnlyList<ScoreFactor> Factors { get; init; } = Array.Empty<ScoreFactor>();

    public bool HasFactors => Factors.Count > 0;
}
