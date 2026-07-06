namespace OpenClean.Models;

/// <summary>
/// Momentaufnahme des Analyse-Fortschritts. Wird vom <see cref="Services.TempScannerService"/>
/// pro verarbeitetem Top-Level-Eintrag gemeldet und vom ViewModel für ProgressBar/ETA genutzt.
/// Rein datentragend – keinerlei UI-Bezug.
/// </summary>
public sealed class ScanProgress
{
    /// <summary>Aktuell analysierter Pfad (Datei oder Ordner). Kann leer sein (z. B. Papierkorb).</summary>
    public string CurrentPath { get; init; } = "";

    /// <summary>Bereits verarbeitete Top-Level-Einträge.</summary>
    public int Done { get; init; }

    /// <summary>Geschätzte Gesamtzahl der Top-Level-Einträge.</summary>
    public int Total { get; init; }

    /// <summary>Fortschritt in Prozent (0–100), robust gegen Total = 0.</summary>
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Done * 100.0 / Total, 0, 100);
}
