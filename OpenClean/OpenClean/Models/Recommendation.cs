using System.Windows.Input;

namespace OpenClean.Models;

/// <summary>
/// Dringlichkeit einer Empfehlung (steuert Farbe des Icon-Badges und des Status-Tags).
/// </summary>
public enum RecommendationSeverity
{
    Positive,
    Info,
    Warning,
    Critical
}

/// <summary>
/// Thematische Art einer Empfehlung – bestimmt das Symbol im Icon-Badge
/// (dieselben Symbole wie die Statistik-Kacheln im unteren Dashboard-Bereich).
/// </summary>
public enum RecommendationKind
{
    Storage,
    Reclaimable,
    Startup,
    Ram,
    AllGood
}

/// <summary>
/// Eine aus der Analyse abgeleitete Handlungsempfehlung. Optional mit einer
/// Aktion (z. B. „In Bereinigung wechseln und scannen“).
/// </summary>
public sealed class Recommendation
{
    public required string Title { get; init; }
    public string Text { get; init; } = "";
    public RecommendationSeverity Severity { get; init; } = RecommendationSeverity.Info;

    /// <summary>Thematisches Symbol des Icon-Badges.</summary>
    public RecommendationKind Kind { get; init; } = RecommendationKind.AllGood;

    /// <summary>Lokalisierter Status-Tag neben dem Titel („Kritisch“ / „Empfohlen“ / „Hinweis“).</summary>
    public string SeverityTag { get; init; } = "";

    /// <summary>Kennzahl rechts, z. B. „152,26 GB“ oder „38“. Leer = keine Kennzahl.</summary>
    public string ValueDisplay { get; init; } = "";

    /// <summary>Einheit/Beschriftung unter der Kennzahl, z. B. „frei“ / „bereinigbar“ / „aktiv“.</summary>
    public string ValueUnit { get; init; } = "";

    public bool HasValue => !string.IsNullOrEmpty(ValueDisplay);

    /// <summary>Beschriftung des Aktions-Buttons, z. B. „Bereinigen“. Null = keine Aktion.</summary>
    public string? ActionLabel { get; init; }

    public ICommand? ActionCommand { get; init; }

    public bool HasAction => ActionCommand is not null && !string.IsNullOrEmpty(ActionLabel);
}
