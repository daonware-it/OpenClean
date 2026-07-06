using OpenClean.ViewModels;

namespace OpenClean.Models;

/// <summary>
/// Ein einzelner, zur Löschung vorgeschlagener Privatsphäre-Eintrag
/// (z. B. eine besuchte URL, ein Cookie-Host, ein zuletzt geöffnetes Dokument).
/// Der Provider erzeugt diese Objekte beim Scan – gelöscht wird nichts, bis der
/// Nutzer explizit bestätigt und die Bereinigung startet.
/// </summary>
public sealed class PrivacyItem : ViewModelBase
{
    private bool _isSelected = true;

    /// <summary>Anzeigetext (z. B. URL, Host oder Dateipfad).</summary>
    public required string Name { get; init; }

    /// <summary>Optionale Zusatzinfo (z. B. Zeitstempel, Domain, Anzahl).</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Opaker Lösch-Handle: trägt providerspezifische Informationen (z. B. DB-Pfad +
    /// Zeilen-ID oder Host), die der erzeugende Provider in <c>CleanAsync</c> auswertet.
    /// Für das ViewModel/die UI bewusst undurchsichtig (object).
    /// </summary>
    public object? Tag { get; init; }

    /// <summary>Ob dieser Eintrag beim Bereinigen gelöscht wird (Standard: ja).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    /// <summary>Wird von der Gruppe abonniert, um die Auswahl-Summe live zu aktualisieren.</summary>
    public Action? SelectionChanged { get; set; }
}
