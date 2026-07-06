namespace OpenClean.Models;

/// <summary>
/// Momentaufnahme des Lösch-Fortschritts. Wird vom <see cref="Services.CleanerService"/>
/// pro gelöschtem/übersprungenem Item gemeldet und vom ViewModel für ProgressBar/Text genutzt.
/// Rein datentragend – keinerlei UI-Bezug.
/// </summary>
public sealed class CleanupProgress
{
    /// <summary>Aktuell gelöschter Pfad. Kann leer sein (z. B. Papierkorb).</summary>
    public string CurrentPath { get; init; } = "";

    /// <summary>Bereits verarbeitete (gelöschte oder übersprungene) Items.</summary>
    public int Done { get; init; }

    /// <summary>Gesamtzahl der ausgewählten Items.</summary>
    public int Total { get; init; }

    /// <summary>Fortschritt in Prozent (0–100), robust gegen Total = 0.</summary>
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Done * 100.0 / Total, 0, 100);
}
