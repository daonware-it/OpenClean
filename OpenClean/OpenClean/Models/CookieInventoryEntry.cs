using OpenClean.Services;
using OpenClean.ViewModels;

namespace OpenClean.Models;

/// <summary>
/// Ein Inventar-Eintrag der read-only Cookie-Übersicht: eine normalisierte Domain samt
/// Cookie-Anzahl, beteiligten Browsern und Whitelist-Status. Anders als beim Löschdienst
/// werden hier auch bereits gelistete Domains angezeigt (markiert über <see cref="IsWhitelisted"/>).
/// </summary>
public sealed class CookieInventoryEntry : ViewModelBase
{
    /// <summary>Normalisierte Domain (Kandidat für die Whitelist).</summary>
    public required string Domain { get; init; }
    /// <summary>Anzahl Cookies dieser Domain über alle Browser.</summary>
    public required long Count { get; init; }
    /// <summary>Beteiligte Browser, z. B. "Chrome, Firefox".</summary>
    public required string Browsers { get; init; }

    /// <summary>
    /// Namen der Cookies dieser Domain (distinct, alphabetisch). Stehen in der Browser-DB im
    /// Klartext – der verschlüsselte Cookie-Wert wird bewusst nicht gelesen.
    /// </summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

    /// <summary>Anzeige-Detailzeile, im Stil von CookiesProvider ("… · N Cookie(s)").</summary>
    public string Detail => $"{Browsers} · {Count} Cookie(s)";

    /// <summary>
    /// Gekürzte Namensliste für die zweite Anzeigezeile, z. B. "sid, _ga, session +3 weitere".
    /// Leer, wenn keine Namen vorliegen.
    /// </summary>
    public string NamesPreview
    {
        get
        {
            if (Names.Count == 0) return "";
            const int max = 4;
            var shown = string.Join(", ", Names.Take(max));
            return Names.Count > max
                ? Loc.T("privacy.browserCookies.namesMore", shown, Names.Count - max)
                : shown;
        }
    }

    /// <summary>Ob eine Namenszeile angezeigt werden soll.</summary>
    public bool HasNames => Names.Count > 0;

    /// <summary>
    /// Ob der Eintrag zum Suchbegriff passt: Treffer in der Domain ODER in einem Cookie-Namen
    /// (groß/klein-egal, Teilstring). Leerer Begriff passt immer.
    /// </summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();

        if (Domain.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var name in Names)
        {
            if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private bool _isWhitelisted;
    /// <summary>Ob die Domain bereits durch die Whitelist geschützt ist (dann kein Hinzufügen nötig).</summary>
    public bool IsWhitelisted
    {
        get => _isWhitelisted;
        set { if (SetProperty(ref _isWhitelisted, value)) OnPropertyChanged(nameof(CanAdd)); }
    }
    /// <summary>Ob die Domain noch zur Whitelist hinzugefügt werden kann (Gegenteil von IsWhitelisted).</summary>
    public bool CanAdd => !_isWhitelisted;
}
