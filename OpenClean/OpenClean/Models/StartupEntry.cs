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
    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher) ? Loc.T("startup.publisher.unknown") : Publisher;

    public StartupImpact Impact { get; set; } = StartupImpact.Unknown;

    /// <summary>True, wenn der Impact geschätzt wurde (kein gemessener Windows-Wert).</summary>
    public bool ImpactIsEstimated { get; set; }

    public string LocationDisplay => Location switch
    {
        StartupLocation.HkcuRun => Loc.T("startup.location.hkcuRun"),
        StartupLocation.HklmRun => Loc.T("startup.location.hklmRun"),
        StartupLocation.HklmRunWow6432 => Loc.T("startup.location.hklmRunWow"),
        StartupLocation.HkcuRunOnce => Loc.T("startup.location.hkcuRunOnce"),
        StartupLocation.HklmRunOnce => Loc.T("startup.location.hklmRunOnce"),
        StartupLocation.HklmPoliciesRun => Loc.T("startup.location.hklmPolicies"),
        StartupLocation.HkcuPoliciesRun => Loc.T("startup.location.hkcuPolicies"),
        StartupLocation.PackagedApp => Loc.T("startup.location.packaged"),
        StartupLocation.StartupFolderUser => Loc.T("startup.location.folderUser"),
        StartupLocation.StartupFolderCommon => Loc.T("startup.location.folderCommon"),
        _ => Location.ToString()
    };

    public string ImpactDisplay
    {
        get
        {
            string label = Impact switch
            {
                StartupImpact.High => Loc.T("startup.impact.high"),
                StartupImpact.Medium => Loc.T("startup.impact.medium"),
                StartupImpact.Low => Loc.T("startup.impact.low"),
                _ => Loc.T("startup.impact.unknown")
            };
            return ImpactIsEstimated && Impact != StartupImpact.Unknown
                ? Loc.T("startup.impact.estimated", label)
                : label;
        }
    }

    public string StatusDisplay => IsEnabled ? Loc.T("startup.state.enabled") : Loc.T("startup.state.disabled");
}
