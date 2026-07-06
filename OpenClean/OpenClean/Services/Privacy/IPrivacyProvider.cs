using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Ein Anbieter einer Privatsphäre-Kategorie (z. B. Browser-Verlauf, Cookies,
/// zuletzt verwendete Dateien). Jeder Provider kapselt genau eine Domäne:
/// er meldet, ob er auf diesem System verfügbar ist, listet beim Scan die
/// löschbaren Einträge auf und löscht auf Anforderung die ausgewählten Einträge.
///
/// WICHTIG für Agent 2 &amp; 3: Diese Signatur ist fix. Implementiert eure Provider
/// exakt gegen dieses Interface – ändert es NICHT.
/// </summary>
public interface IPrivacyProvider
{
    /// <summary>Anzeigename der Kategorie (deutsch).</summary>
    string Name { get; }

    /// <summary>Kurzbeschreibung, was gelöscht wird (deutsch).</summary>
    string Description { get; }

    /// <summary>
    /// Ob vor dem Löschen eine explizite Bestätigung nötig ist
    /// (z. B. Cookies = true, da Anmeldungen verloren gehen).
    /// </summary>
    bool RequiresConfirmation { get; }

    /// <summary>
    /// Ob diese Kategorie auf dem aktuellen System vorhanden ist
    /// (install-gated, z. B. per <c>Directory.Exists</c>). Nicht verfügbare
    /// Provider werden gar nicht erst als Kategorie angezeigt.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Ermittelt die löschbaren Einträge. Rein lesend – löscht nichts.
    /// Muss robust gegen gesperrte Dateien/Fehler sein und darf NIE eine
    /// Ausnahme bis zum Aufrufer durchlassen (im Zweifel leere Liste).
    /// </summary>
    Task<IReadOnlyList<PrivacyItem>> ScanAsync();

    /// <summary>
    /// Löscht die übergebenen (ausgewählten) Einträge und liefert die Anzahl
    /// tatsächlich gelöschter Einträge zurück. Übersprungene/gesperrte Einträge
    /// werden nicht mitgezählt und dürfen keinen Absturz verursachen.
    /// </summary>
    Task<int> CleanAsync(IEnumerable<PrivacyItem> selected);
}
