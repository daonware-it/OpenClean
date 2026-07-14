using OpenClean.Contracts;
using OpenClean.Services;
using OpenClean.Services.Integrity;
using OpenClean.Services.Licensing;
using OpenClean.Views;

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
    Kontextmenue,
    Privatsphaere,
    Updates,
    Zeitplan,
    Deinstallation,
    Duplikate,
    Lizenz
}

/// <summary>
/// Wurzel-ViewModel: hält die Bereiche Übersicht, Speicher &amp; RAM, Bereinigung, Autostart
/// und steuert die aktive Sektion (für programmatische Navigation aus dem Dashboard).
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private AppSection _currentSection = AppSection.Uebersicht;

    private object? _scheduleSectionContent;
    private LockedScheduleViewModel? _lockedSchedule;

    public CleanerViewModel Cleaner { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public ContextMenuViewModel ContextMenu { get; } = new();
    public PrivacyViewModel Privacy { get; } = new();
    public UpdaterViewModel Updater { get; } = new();
    public UninstallViewModel Uninstall { get; } = new();
    public DuplicatesViewModel Duplicates { get; } = new();
    public DashboardViewModel Dashboard { get; }
    public LicensePageViewModel License { get; } = new();
    public StorageAnalysisViewModel StorageAnalysis { get; } = new();

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

        // Zeitplan-Bereich: Premium-Modul (wenn lizenziert + geladen) oder Locked-Ansicht.
        // Lizenzänderungen (Aktivierung im Dialog, Hintergrund-Refresh) bauen den Bereich neu.
        BuildScheduleSection();
        PremiumService.Instance.Changed += (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) BuildScheduleSection();
            else dispatcher.InvokeAsync(BuildScheduleSection);
        };

        // Bei Sprachwechsel die berechneten Texte aller Bereiche neu aufbauen.
        Loc.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AppTagline));
            OnPropertyChanged(nameof(IntegrityBannerText));
            Dashboard.Relocalize();
            Cleaner.Relocalize();
            Privacy.Relocalize();
            Startup.Relocalize();
            ContextMenu.Relocalize();
            Updater.Relocalize();
            _lockedSchedule?.Relocalize();
            Uninstall.Relocalize();
            Duplicates.Relocalize();
            License.Relocalize();
            StorageAnalysis.Relocalize();
        };
    }

    // ---- Integrität (OPCL-20) ---------------------------------------------------------
    // Der Zustand steht beim Erzeugen des ViewModels bereits fest (App.OnStartup prüft davor)
    // und ändert sich danach nicht mehr – deshalb reine Nur-Lese-Eigenschaften ohne Ereignis.

    /// <summary>True, wenn die Selbstprüfung angeschlagen hat: Banner einblenden.</summary>
    public bool IsIntegrityDegraded => IntegrityState.IsDegraded;

    /// <summary>True, wenn deswegen die ändernden Funktionen gesperrt sind.</summary>
    public bool IsIntegrityBlocked => IntegrityState.IsBlocked;

    /// <summary>Text des Integritäts-Banners.</summary>
    public string IntegrityBannerText => IntegrityState.IsBlocked
        ? Loc.T("integrity.banner.tampered")
        : Loc.T("integrity.banner.unsigned");

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

            // Beim Öffnen des Premium-Bereichs bei Bedarf das Lizenz-Token im
            // Hintergrund erneuern (nie beim App-Start, nie blockierend).
            if (value == AppSection.Zeitplan)
                PremiumService.Instance.RefreshInBackground();
        }
    }

    /// <summary>
    /// Inhalt des Zeitplan-Bereichs: die View des Premium-Moduls oder – ohne gültige
    /// Lizenz bzw. ohne Modul – die <see cref="LockedSectionView"/> mit Kauf/Aktivierung.
    /// </summary>
    public object? ScheduleSectionContent
    {
        get => _scheduleSectionContent;
        private set => SetProperty(ref _scheduleSectionContent, value);
    }

    /// <summary>True, wenn der Zeitplan freigeschaltet ist (steuert das Schloss-Symbol in der Nav).</summary>
    public bool IsScheduleUnlocked => _scheduleSectionContent is not null and not LockedSectionView;

    /// <summary>True bei gültiger Lizenz – steuert die Pro-Karte im Sidebar-Footer.</summary>
    public bool IsPremium => PremiumService.Instance.IsPremium;

    private void BuildScheduleSection()
    {
        IPremiumSection? section = PremiumService.Instance.HasFeature(PremiumContract.FeatureSchedule)
            ? PremiumService.Instance.GetSection(nameof(AppSection.Zeitplan))
            : null;

        if (section is not null)
        {
            _lockedSchedule = null;
            ScheduleSectionContent = section.CreateView();
        }
        else
        {
            _lockedSchedule = new LockedScheduleViewModel();
            ScheduleSectionContent = new LockedSectionView { DataContext = _lockedSchedule };
        }

        OnPropertyChanged(nameof(IsScheduleUnlocked));
        OnPropertyChanged(nameof(IsPremium));
    }

    public string AppTitle => "OpenClean";
    public string AppTagline => Loc.T("app.tagline");
}
