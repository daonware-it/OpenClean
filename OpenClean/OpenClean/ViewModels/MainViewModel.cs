using OpenClean.Contracts;
using OpenClean.Services;
using OpenClean.Services.Integrity;
using OpenClean.Services.Licensing;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>
/// Die auswählbaren Hauptbereiche der App (Reihenfolge = Reihenfolge in der Navigation).
/// </summary>
public enum AppSection
{
    Uebersicht,
    System,
    Bereinigung,
    Tiefenreinigung,
    Autostart,
    Kontextmenue,
    Privatsphaere,
    Netzwerk,
    Updates,
    Zeitplan,
    Deinstallation,
    Duplikate,
    Wiederherstellen,
    Einstellungen
}

/// <summary>
/// Wurzel-ViewModel: hält die Bereiche Übersicht, Speicher &amp; RAM, Bereinigung, Autostart
/// und steuert die aktive Sektion (für programmatische Navigation aus dem Dashboard).
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private AppSection _currentSection = AppSection.Uebersicht;
    private bool _isCleaningExpanded;

    private object? _scheduleSectionContent;
    private LockedScheduleViewModel? _lockedSchedule;
    private bool _isScheduleUnlocked;
    private readonly IUiDispatcher _ui;

    public CleanerViewModel Cleaner { get; } = new();
    public DeepCleanViewModel DeepClean { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public ContextMenuViewModel ContextMenu { get; } = new();
    public PrivacyViewModel Privacy { get; } = new();
    public NetworkViewModel Network { get; } = new();
    public UpdaterViewModel Updater { get; } = new();
    public UninstallViewModel Uninstall { get; } = new();
    public DuplicatesViewModel Duplicates { get; } = new();
    public BackupHistoryViewModel BackupHistory { get; } = new();
    public DashboardViewModel Dashboard { get; }
    public LicensePageViewModel License { get; } = new();
    public StorageAnalysisViewModel StorageAnalysis { get; } = new();

    /// <summary>
    /// Zentraler Einstellungsbereich (Zahnrad in der Titelleiste). Bündelt Design, Sprache
    /// und Lizenz. Die bestehende <see cref="License"/>-Instanz wird durchgereicht (nicht neu
    /// gebaut), damit Aktivierung/Freigabe unverändert über denselben Zustand laufen.
    /// </summary>
    public SettingsViewModel Settings { get; }

    public MainViewModel(IUiDispatcher? ui = null)
    {
        _ui = ui ?? UiDispatcher.Default;

        // Zuletzt aktiven Bereich (Startphase) aus den Einstellungen wiederherstellen.
        // Direkt das Feld setzen (nicht die Property), damit weder gespeichert noch
        // EnsureAnalyzed vor der Dashboard-Initialisierung ausgelöst wird.
        if (Enum.TryParse(SettingsService.Instance.Current.LastSection, out AppSection saved))
            _currentSection = saved;

        // Die Reinigungs-Nav-Gruppe von Anfang an aufgeklappt zeigen, wenn beim Start bereits
        // einer ihrer Unterpunkte aktiv ist – sonst wäre der aktive Eintrag verborgen.
        _isCleaningExpanded = _currentSection is AppSection.Bereinigung or AppSection.Tiefenreinigung;

        // Das Dashboard bekommt einen Navigations-Callback und den Cleaner (für den
        // „Bereinigen“-Empfehlungs-Button, der zusätzlich den Scan auslöst).
        Dashboard = new DashboardViewModel(section => CurrentSection = section, Cleaner);
        Dashboard.EnsureAnalyzed();

        // Einstellungsbereich mit der bestehenden Lizenz-Instanz aufbauen (Wiederverwendung).
        Settings = new SettingsViewModel(License);

        // Wurde der System-Bereich als zuletzt aktiver Bereich wiederhergestellt, wurde
        // _currentSection oben direkt gesetzt – der CurrentSection-Setter (und damit sein
        // EnsureDiskHealthLoaded) feuert dann nicht. Ohne diesen Anstoß bliebe die
        // Gesundheits-Sektion beim ersten Anzeigen leer, bis der Nutzer einmal weg- und
        // zurücknavigiert. Deshalb hier einmalig starten – analog zu EnsureAnalyzed oben.
        // EnsureDiskHealthLoaded ist idempotent (Reentrancy-Guard), also kein Doppel-Scan.
        if (_currentSection == AppSection.System)
            Dashboard.EnsureDiskHealthLoaded();

        // Zeitplan-Bereich: Premium-Modul (wenn lizenziert + geladen) oder Locked-Ansicht.
        // Lizenzänderungen (Aktivierung im Dialog, Hintergrund-Refresh) bauen den Bereich neu.
        BuildScheduleSection();
        PremiumService.Instance.Changed += (_, _) =>
        {
            if (_ui.CheckAccess()) BuildScheduleSection();
            else _ui.Post(BuildScheduleSection);
        };

        // Bei Sprachwechsel die berechneten Texte aller Bereiche neu aufbauen.
        Loc.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AppTagline));
            OnPropertyChanged(nameof(SidebarVersion));
            OnPropertyChanged(nameof(IntegrityBannerText));
            Dashboard.Relocalize();
            Cleaner.Relocalize();
            DeepClean.Relocalize();
            Privacy.Relocalize();
            Network.Relocalize();
            Startup.Relocalize();
            ContextMenu.Relocalize();
            Updater.Relocalize();
            _lockedSchedule?.Relocalize();
            Uninstall.Relocalize();
            Duplicates.Relocalize();
            BackupHistory.Relocalize();
            License.Relocalize();
            StorageAnalysis.Relocalize();
            Settings.Relocalize();
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

            // Der Einstellungsbereich wird nicht über einen Nav-RadioButton, sondern über das
            // Zahnrad in der Titelleiste angesteuert – seine Sichtbarkeit hängt an IsSettings.
            OnPropertyChanged(nameof(IsSettings));

            // Wird eine der beiden zusammengefassten Reinigungs-Sektionen aktiv (auch programmatisch,
            // z.B. über eine Dashboard-Verknüpfung), die Nav-Gruppe automatisch aufklappen, damit der
            // aktive Unterpunkt sichtbar bleibt.
            if (value is AppSection.Bereinigung or AppSection.Tiefenreinigung)
                IsCleaningExpanded = true;

            // Startphase sofort persistieren (write-through, überlebt unsauberes Beenden).
            SettingsService.Instance.Current.LastSection = value.ToString();
            SettingsService.Instance.Save();

            if (value == AppSection.Uebersicht)
                Dashboard.EnsureAnalyzed();

            // Der Gesundheits-Scan liest per WMI und ist langsam: erst beim Öffnen starten,
            // nie beim App-Start. Läuft danach nicht erneut.
            if (value == AppSection.System)
                Dashboard.EnsureDiskHealthLoaded();

            // Beim Öffnen des Wiederherstellen-Bereichs die Liste der Sicherungen aktualisieren.
            if (value == AppSection.Wiederherstellen)
                _ = BackupHistory.RefreshAsync();

            // Beim Öffnen des Premium-Bereichs bei Bedarf das Lizenz-Token im
            // Hintergrund erneuern (nie beim App-Start, nie blockierend).
            if (value == AppSection.Zeitplan)
                PremiumService.Instance.RefreshInBackground();
        }
    }

    /// <summary>
    /// Inhalt des Zeitplan-Bereichs: die View des Premium-Moduls oder – ohne gültige
    /// Lizenz bzw. ohne Modul – das <c>LockedScheduleViewModel</c> (per DataTemplate als
    /// Locked-Ansicht mit Kauf/Aktivierung gerendert).
    /// </summary>
    public object? ScheduleSectionContent
    {
        get => _scheduleSectionContent;
        private set => SetProperty(ref _scheduleSectionContent, value);
    }

    /// <summary>True, wenn der Zeitplan freigeschaltet ist (steuert das Schloss-Symbol in der Nav).</summary>
    public bool IsScheduleUnlocked => _isScheduleUnlocked;

    /// <summary>True bei gültiger Lizenz – steuert die Pro-Karte im Sidebar-Footer.</summary>
    public bool IsPremium => PremiumService.Instance.IsPremium;

    /// <summary>True, wenn der Einstellungsbereich aktiv ist (steuert die Sichtbarkeit der SettingsView).</summary>
    public bool IsSettings => _currentSection == AppSection.Einstellungen;

    /// <summary>
    /// Steuert, ob die zusammengefasste Reinigungs-Nav-Gruppe (Bereinigung + Tiefenreinigung)
    /// in der Seitenleiste aufgeklappt ist. Wird beim Aktivieren eines der Unterpunkte automatisch
    /// gesetzt und kann vom Nutzer über den Gruppenkopf umgeschaltet werden.
    /// </summary>
    public bool IsCleaningExpanded
    {
        get => _isCleaningExpanded;
        set => SetProperty(ref _isCleaningExpanded, value);
    }

    private void BuildScheduleSection()
    {
        IPremiumSection? section = PremiumService.Instance.HasFeature(PremiumContract.FeatureSchedule)
            ? PremiumService.Instance.GetSection(nameof(AppSection.Zeitplan))
            : null;

        if (section is not null)
        {
            // Freigeschaltet: Das Premium-Modul liefert seine eigene View (Plugin-Grenze).
            _lockedSchedule = null;
            _isScheduleUnlocked = true;
            ScheduleSectionContent = section.CreateView();
        }
        else
        {
            // Gesperrt: nur das ViewModel halten – die zugehörige LockedSectionView wählt ein
            // DataTemplate (App.xaml) aus. So bleibt der MainViewModel frei von View-Referenzen.
            _lockedSchedule = new LockedScheduleViewModel();
            _isScheduleUnlocked = false;
            ScheduleSectionContent = _lockedSchedule;
        }

        OnPropertyChanged(nameof(IsScheduleUnlocked));
        OnPropertyChanged(nameof(IsPremium));
    }

    public string AppTitle => "OpenClean";
    public string AppTagline => Loc.T("app.tagline");

    /// <summary>Versionshinweis in der Sidebar-Fußzeile. Die Versionsnummer stammt
    /// zentral aus <see cref="AppInfo.Version"/>; die Sprachdatei liefert nur noch
    /// den lokalisierten Zusatz (<c>{0}</c> = Version).</summary>
    public string SidebarVersion => Loc.T("sidebar.version", AppInfo.Version);
}
