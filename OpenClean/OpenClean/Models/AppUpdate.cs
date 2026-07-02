namespace OpenClean.Models;

/// <summary>
/// Ein von winget als aktualisierbar gemeldetes Programm. Rein datentragend –
/// wird vom <see cref="Services.WingetService"/> beim Parsen der Upgrade-Tabelle erzeugt.
/// </summary>
public sealed class AppUpdate
{
    /// <summary>Anzeigename des Programms (1. Spalte der winget-Tabelle).</summary>
    public required string Name { get; init; }

    /// <summary>Eindeutige winget-Paket-Id (2. Spalte), z. B. "Mozilla.Firefox".</summary>
    public required string Id { get; init; }

    /// <summary>Aktuell installierte Version (3. Spalte).</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>Verfügbare neue Version (4. Spalte).</summary>
    public required string AvailableVersion { get; init; }

    /// <summary>Quelle des Pakets (5. Spalte), z. B. "winget". Kann leer sein.</summary>
    public string Source { get; init; } = "";
}
