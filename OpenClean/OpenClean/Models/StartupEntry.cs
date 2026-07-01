namespace OpenClean.Models;

/// <summary>
/// Herkunft eines Autostart-Eintrags.
/// </summary>
public enum StartupLocation
{
    HkcuRun,
    HklmRun,
    HklmRunWow6432,
    HkcuRunOnce,
    HklmRunOnce,
    HklmPoliciesRun,
    HkcuPoliciesRun,
    PackagedApp,
    StartupFolderUser,
    StartupFolderCommon
}

/// <summary>
/// Grobe Einschätzung der Startbelastung.
/// </summary>
public enum StartupImpact
{
    Unknown,
    Low,
    Medium,
    High
}

/// <summary>
/// Ein einzelnes Autostart-Programm.
/// </summary>
public sealed class StartupEntry
{
    public required string Name { get; init; }

    /// <summary>Vollständiger Befehl inkl. Argumente, wie in der Registry hinterlegt.</summary>
    public required string Command { get; init; }

    /// <summary>Auflösbarer EXE-Pfad (best effort), sonst null.</summary>
    public string? ExecutablePath { get; init; }

    public required StartupLocation Location { get; init; }

    public bool IsEnabled { get; set; }

    /// <summary>
    /// False, wenn der Eintrag nicht über OpenClean/StartupApproved geschaltet werden kann
    /// (z. B. per Gruppenrichtlinie gesetzte Policy-Run-Keys).
    /// </summary>
    public bool CanToggle { get; init; } = true;

    /// <summary>
    /// Für Store-Apps (UWP): voller HKCU-relativer Pfad zum Task-Schlüssel
    /// (…\SystemAppData\&lt;PackageFamilyName&gt;\&lt;TaskId&gt;), dessen State-Wert
    /// SetEnabled beschreibt. Bei anderen Locations null.
    /// </summary>
    public string? PackagedStateKeyPath { get; init; }

    /// <summary>
    /// Verifizierter Herausgeber (Authenticode-Signierer der EXE bzw. Store-App-Publisher).
    /// Leer, wenn nicht ermittelbar.
    /// </summary>
    public string Publisher { get; init; } = "";

    /// <summary>Anzeigename des Herausgebers; „Unbekannt", wenn leer.</summary>
    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher) ? "Unbekannt" : Publisher;

    public StartupImpact Impact { get; set; } = StartupImpact.Unknown;

    /// <summary>True, wenn der Impact geschätzt wurde (kein gemessener Windows-Wert).</summary>
    public bool ImpactIsEstimated { get; set; }

    public string LocationDisplay => Location switch
    {
        StartupLocation.HkcuRun => "Registry (Aktueller Benutzer)",
        StartupLocation.HklmRun => "Registry (Alle Benutzer)",
        StartupLocation.HklmRunWow6432 => "Registry (Alle Benutzer, 32-Bit)",
        StartupLocation.HkcuRunOnce => "Registry (Aktueller Benutzer, RunOnce)",
        StartupLocation.HklmRunOnce => "Registry (Alle Benutzer, RunOnce)",
        StartupLocation.HklmPoliciesRun => "Registry (Richtlinie, Alle Benutzer)",
        StartupLocation.HkcuPoliciesRun => "Registry (Richtlinie, Aktueller Benutzer)",
        StartupLocation.PackagedApp => "Store-App (UWP)",
        StartupLocation.StartupFolderUser => "Autostart-Ordner (Benutzer)",
        StartupLocation.StartupFolderCommon => "Autostart-Ordner (Alle Benutzer)",
        _ => Location.ToString()
    };

    public string ImpactDisplay
    {
        get
        {
            string label = Impact switch
            {
                StartupImpact.High => "Hoch",
                StartupImpact.Medium => "Mittel",
                StartupImpact.Low => "Niedrig",
                _ => "Unbekannt"
            };
            return ImpactIsEstimated && Impact != StartupImpact.Unknown ? $"~{label} (geschätzt)" : label;
        }
    }

    public string StatusDisplay => IsEnabled ? "Aktiviert" : "Deaktiviert";
}
