using System.Windows;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 8 (Agent 3): Zwischenablage leeren.
///
/// Zeigt EIN Info-Item mit dem aktuellen Inhaltstyp (Text / Bild / Dateien) und leert
/// die Zwischenablage per <see cref="System.Windows.Clipboard.Clear"/> – sofort, ohne
/// Vorschau (<see cref="RequiresConfirmation"/> = false).
///
/// Wichtig: <see cref="System.Windows.Clipboard"/> ist ein STA-/UI-Thread-Typ. Der
/// Provider läuft evtl. in einem Background-Task, daher erfolgt JEDER Zugriff über den
/// UI-Dispatcher (<c>Application.Current.Dispatcher.Invoke</c>) und ist null-sicher.
///
/// Robustheit: Ist die Zwischenablage von einem anderen Prozess gesperrt oder existiert
/// kein Dispatcher, wird eine leere Liste bzw. 0 zurückgegeben – nie eine Ausnahme.
/// </summary>
public sealed class ClipboardProvider : IPrivacyProvider
{
    public string Name => "Zwischenablage";
    public string Description => "Der aktuelle Inhalt der Windows-Zwischenablage.";

    // Sofortiges Leeren ohne Vorschau erforderlich.
    public bool RequiresConfirmation => false;

    public bool IsAvailable => true;

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync()
    {
        var items = new List<PrivacyItem>();
        try
        {
            string? contentType = OnUiThread(DescribeContent);

            // null oder "leer" -> nichts zu löschen.
            if (!string.IsNullOrEmpty(contentType))
            {
                items.Add(new PrivacyItem
                {
                    Name = "Zwischenablage-Inhalt",
                    Detail = contentType
                });
            }
        }
        catch { /* Zugriffsfehler -> leere Liste */ }

        return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);
    }

    public Task<int> CleanAsync(IEnumerable<PrivacyItem> selected)
    {
        // Nur leeren, wenn überhaupt ein Eintrag ausgewählt wurde.
        if (!selected.Any())
            return Task.FromResult(0);

        int cleared = 0;
        try
        {
            cleared = OnUiThread(() =>
            {
                try
                {
                    System.Windows.Clipboard.Clear();
                    return 1;
                }
                catch { return 0; }
            });
        }
        catch { /* Dispatcher/Zugriffsfehler -> 0 */ }

        return Task.FromResult(cleared);
    }

    // ---- Hilfsfunktionen ---------------------------------------------------

    /// <summary>
    /// Ermittelt den aktuellen Inhaltstyp der Zwischenablage. Liefert null, wenn leer
    /// oder nicht lesbar (dann gibt es nichts zu löschen).
    /// </summary>
    private static string? DescribeContent()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText()) return "Text";
            if (System.Windows.Clipboard.ContainsImage()) return "Bild";
            if (System.Windows.Clipboard.ContainsFileDropList()) return "Dateien";
            if (System.Windows.Clipboard.ContainsAudio()) return "Audio";
            return null;
        }
        catch
        {
            // Zwischenablage gesperrt/nicht lesbar -> als "nichts zu löschen" behandeln.
            return null;
        }
    }

    /// <summary>
    /// Führt eine Zwischenablage-Operation auf dem UI-Thread aus. Ohne verfügbaren
    /// Dispatcher wird <paramref name="fallback"/> zurückgegeben (Standard: default).
    /// </summary>
    private static T OnUiThread<T>(Func<T> action, T fallback = default!)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return fallback;

            if (dispatcher.CheckAccess())
                return action();

            return dispatcher.Invoke(action);
        }
        catch { return fallback; }
    }
}
