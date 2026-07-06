using System.Windows.Input;

namespace OpenClean.Models;

/// <summary>
/// Dringlichkeit einer Empfehlung (steuert Farbe/Icon in der UI).
/// </summary>
public enum RecommendationSeverity
{
    Positive,
    Info,
    Warning,
    Critical
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

    /// <summary>Beschriftung des Aktions-Buttons, z. B. „Bereinigen“. Null = keine Aktion.</summary>
    public string? ActionLabel { get; init; }

    public ICommand? ActionCommand { get; init; }

    public bool HasAction => ActionCommand is not null && !string.IsNullOrEmpty(ActionLabel);
}
