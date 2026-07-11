namespace OpenClean.Models;

/// <summary>
/// Warnstufe eines Laufwerks anhand des freien Speichers.
/// </summary>
public enum DriveWarningLevel
{
    /// <summary>Genug frei (≥ 20 %).</summary>
    Normal,
    /// <summary>Knapp (&lt; 20 % frei) – Amber-Hinweis.</summary>
    Low,
    /// <summary>Kritisch (&lt; 10 % frei) – DangerBrush.</summary>
    Critical
}

/// <summary>
/// Belegung eines einzelnen (festen) Laufwerks – rein aus echten DriveInfo-Werten.
/// </summary>
public sealed class DriveUsage
{
    /// <summary>Wurzelpfad, z. B. "C:\".</summary>
    public required string Name { get; init; }

    /// <summary>Laufwerksbuchstabe inkl. Doppelpunkt, z. B. "C:".</summary>
    public string Letter => Name.Length >= 2 ? Name[..2] : Name.TrimEnd('\\');

    public string VolumeLabel { get; init; } = "";

    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes { get; init; }

    /// <summary>Belegt in Prozent (0–100).</summary>
    public double UsedPercent => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100.0 : 0;

    /// <summary>Frei in Prozent (0–100).</summary>
    public double FreePercent => TotalBytes > 0 ? (double)FreeBytes / TotalBytes * 100.0 : 0;

    // ---- Anzeige-Helfer ----
    public string TitleDisplay =>
        string.IsNullOrWhiteSpace(VolumeLabel) ? Letter : $"{Letter} · {VolumeLabel}";

    public string UsedDisplay => Services.ByteFormatter.Format(UsedBytes);
    public string FreeDisplay => Services.ByteFormatter.Format(FreeBytes);
    public string TotalDisplay => Services.ByteFormatter.Format(TotalBytes);

    public string SummaryDisplay =>
        Services.Localization.LocalizationManager.Instance.Get(
            "system.drive.summary",
            Services.ByteFormatter.Format(UsedBytes),
            Services.ByteFormatter.Format(TotalBytes),
            Services.ByteFormatter.Format(FreeBytes));

    public string PercentDisplay => $"{UsedPercent:0} %";

    // ---- Warnstufe ----
    public DriveWarningLevel WarningLevel =>
        TotalBytes <= 0 ? DriveWarningLevel.Normal
        : FreePercent < 10 ? DriveWarningLevel.Critical
        : FreePercent < 20 ? DriveWarningLevel.Low
        : DriveWarningLevel.Normal;

    public bool HasWarning => WarningLevel != DriveWarningLevel.Normal;

    public string WarningText => WarningLevel switch
    {
        DriveWarningLevel.Critical => Services.Localization.LocalizationManager.Instance.Get(
            "system.drive.warnCritical", $"{FreePercent:0}"),
        DriveWarningLevel.Low => Services.Localization.LocalizationManager.Instance.Get(
            "system.drive.warnLow", $"{FreePercent:0}"),
        _ => ""
    };
}
