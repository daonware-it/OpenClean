namespace OpenClean.ViewModels;

/// <summary>
/// Die auswählbaren Hauptbereiche der App (Reihenfolge = Reihenfolge in der Navigation).
/// </summary>
public enum AppSection
{
    Uebersicht,
    System,
    Bereinigung,
    Autostart
}

/// <summary>
/// Wurzel-ViewModel: hält die Bereiche Übersicht, Speicher &amp; RAM, Bereinigung, Autostart
/// und steuert die aktive Sektion (für programmatische Navigation aus dem Dashboard).
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private AppSection _currentSection = AppSection.Uebersicht;

    public CleanerViewModel Cleaner { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public DashboardViewModel Dashboard { get; }

    public MainViewModel()
    {
        // Das Dashboard bekommt einen Navigations-Callback und den Cleaner (für den
        // „Bereinigen“-Empfehlungs-Button, der zusätzlich den Scan auslöst).
        Dashboard = new DashboardViewModel(section => CurrentSection = section, Cleaner);
        Dashboard.EnsureAnalyzed();
    }

    /// <summary>Aktiver Hauptbereich; steuert die Sichtbarkeit der Content-Views.</summary>
    public AppSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (SetProperty(ref _currentSection, value) && value == AppSection.Uebersicht)
                Dashboard.EnsureAnalyzed();
        }
    }

    public string AppTitle => "OpenClean";
    public string AppTagline => "Ehrliche Systembereinigung · Open Source · keine Telemetrie";
}
