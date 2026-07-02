namespace OpenClean.Models;

/// <summary>
/// Momentaufnahme des Update-Fortschritts. Wird vom <see cref="Services.WingetService"/>
/// vor jedem Upgrade gemeldet und vom ViewModel für ProgressBar/Statustext genutzt.
/// Rein datentragend – keinerlei UI-Bezug.
/// </summary>
public sealed class UpdateProgress
{
    /// <summary>Aktuell aktualisiertes Programm. Kann leer sein.</summary>
    public string CurrentApp { get; init; } = "";

    /// <summary>Bereits verarbeitete Programme.</summary>
    public int Done { get; init; }

    /// <summary>Gesamtzahl der zu aktualisierenden Programme.</summary>
    public int Total { get; init; }

    /// <summary>Fortschritt in Prozent (0–100), robust gegen Total = 0.</summary>
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Done * 100.0 / Total, 0, 100);
}
