using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>
/// Die auswählbaren Hauptbereiche der App (Reihenfolge = Reihenfolge in der Navigation).
/// </summary>
public enum AppSection
{
    Uebersicht,
    System,
    Bereinigung,
    Autostart,
    Privatsphaere,
    Updates,
    Zeitplan,
    Deinstallation,
    Duplikate
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
    public PrivacyViewModel Privacy { get; } = new();
    public UpdaterViewModel Updater { get; } = new();
    public ScheduleViewModel Schedule { get; } = new();
    public UninstallViewModel Uninstall { get; } = new();
    public DuplicatesViewModel Duplicates { get; } = new();
    public DashboardViewModel Dashboard { get; }

    public MainViewModel()
    {
        // Zuletzt aktiven Bereich (Startphase) aus den Einstellungen wiederherstellen.
        // Direkt das Feld setzen (nicht die Property), damit weder gespeichert noch
        // EnsureAnalyzed vor der Dashboard-Initialisierung ausgelöst wird.
        if (Enum.TryParse(SettingsService.Instance.Current.LastSection, out AppSection saved))
            _currentSection = saved;

        // Das Dashboard bekommt einen Navigations-Callback und den Cleaner (für den
        // „Bereinigen“-Empfehlungs-Button, der zusätzlich den Scan auslöst).
        Dashboard = new DashboardViewModel(section => CurrentSection = section, Cleaner);
        Dashboard.EnsureAnalyzed();

        // Bei Sprachwechsel die berechneten Texte aller Bereiche neu aufbauen.
        Loc.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AppTagline));
            Dashboard.Relocalize();
            Cleaner.Relocalize();
            Privacy.Relocalize();
            Startup.Relocalize();
            Updater.Relocalize();
            Schedule.Relocalize();
            Uninstall.Relocalize();
            Duplicates.Relocalize();
        };
    }

    /// <summary>Aktiver Hauptbereich; steuert die Sichtbarkeit der Content-Views.</summary>
    public AppSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (!SetProperty(ref _currentSection, value)) return;

            // Startphase sofort persistieren (write-through, überlebt unsauberes Beenden).
            SettingsService.Instance.Current.LastSection = value.ToString();
            SettingsService.Instance.Save();

            if (value == AppSection.Uebersicht)
                Dashboard.EnsureAnalyzed();
        }
    }

    public string AppTitle => "OpenClean";
    public string AppTagline => Loc.T("app.tagline");
}
