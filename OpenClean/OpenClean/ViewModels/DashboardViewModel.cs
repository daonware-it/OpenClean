using System.Collections.ObjectModel;
using System.Windows.Threading;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Dashboard;
using OpenClean.Services.DriveHealth;

namespace OpenClean.ViewModels;

/// <summary>
/// „Übersicht“ + „Speicher &amp; RAM“: fasst reale Messwerte zu einem transparenten
/// Dashboard zusammen. NICHTS wird beschönigt – der Score ergibt sich deterministisch
/// aus echten Signalen, RAM ist reine Info-Anzeige (kein „Booster“).
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly SystemInfoService _systemInfo = new();
    private readonly StartupService _startup = new();
    private readonly TempScannerService _temp = new();

    private readonly Action<AppSection> _navigate;
    private readonly CleanerViewModel _cleaner;

    private readonly DispatcherTimer _ramTimer;
    private bool _analyzed;
    private bool _diskHealthLoaded;
    private bool _isDiskHealthLoading;

    // ---- RAM ----
    private string _usedRam = "–";
    private string _totalRam = "–";
    private string _availableRam = "–";
    private double _ramPercent;
    private bool _ramValid;

    // ---- Score ----
    private ScoreResult _score = new() { Score = 100, Level = ScoreLevel.Good, Label = Loc.T("score.good") };

    // ---- Startup ----
    private int _startupTotal;
    private int _startupEnabled;
    private int _startupHighImpact;

    // ---- Temp ----
    private long _tempBytes;
    private int _tempItems;
    private bool _tempAnalyzed;

    private bool _isBusy;
    private string _statusText = Loc.T("dashboard.status.preparing");

    public ObservableCollection<DriveUsage> Drives { get; } = new();
    public ObservableCollection<ScoreFactor> ScoreFactors { get; } = new();
    public ObservableCollection<Recommendation> Recommendations { get; } = new();

    /// <summary>
    /// Gesundheit der physischen Datenträger. Wird NICHT beim Start gefüllt, sondern erst beim
    /// ersten Öffnen des System-Bereichs (<see cref="EnsureDiskHealthLoaded"/>) – WMI ist
    /// langsam und würde sonst den App-Start bremsen.
    /// </summary>
    public ObservableCollection<DriveHealthViewModel> DiskHealth { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand RefreshRamCommand { get; }
    public RelayCommand OpenSystemCommand { get; }
    public RelayCommand OpenCleanerCommand { get; }
    public RelayCommand OpenStartupCommand { get; }
    public RelayCommand RunAllRecommendationsCommand { get; }

    public DashboardViewModel(Action<AppSection> navigate, CleanerViewModel cleaner)
    {
        _navigate = navigate;
        _cleaner = cleaner;

        RefreshCommand = new AsyncRelayCommand(_ => AnalyzeAsync());
        RefreshRamCommand = new RelayCommand(_ => RefreshRam());
        OpenSystemCommand = new RelayCommand(_ => _navigate(AppSection.System));
        OpenCleanerCommand = new RelayCommand(_ => _navigate(AppSection.Bereinigung));
        OpenStartupCommand = new RelayCommand(_ => _navigate(AppSection.Autostart));
        RunAllRecommendationsCommand = new RelayCommand(_ => RunAllRecommendations());

        // RAM live halten (nur Anzeige, keine Aktion). Jede Sekunde aktualisieren,
        // damit die Wellenform-Kurve lebendig mitläuft.
        _ramTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ramTimer.Tick += (_, _) => RefreshRam();

        // Günstige Werte sofort; die schwere Analyse startet EnsureAnalyzed().
        RefreshRam();
        RefreshDrives();
    }

    // ---- RAM (Info-only) ---------------------------------------------------

    public string UsedRam { get => _usedRam; private set => SetProperty(ref _usedRam, value); }
    public string TotalRam { get => _totalRam; private set => SetProperty(ref _totalRam, value); }
    public string AvailableRam { get => _availableRam; private set => SetProperty(ref _availableRam, value); }

    /// <summary>Echte RAM-Auslastung (dwMemoryLoad), 0–100.</summary>
    public double RamPercent { get => _ramPercent; private set => SetProperty(ref _ramPercent, value); }
    public double RamFreePercent => Math.Clamp(100 - _ramPercent, 0, 100);
    public string RamPercentDisplay => $"{_ramPercent:0} %";
    public bool RamValid { get => _ramValid; private set => SetProperty(ref _ramValid, value); }

    public string RamSummary => _ramValid
        ? Loc.T("dashboard.ram.summary", UsedRam, TotalRam, AvailableRam)
        : Loc.T("dashboard.ram.unavailable");

    private void RefreshRam()
    {
        var mem = _systemInfo.GetMemory();
        RamValid = mem.IsValid;
        if (!mem.IsValid)
        {
            RamPercent = 0;
            OnPropertyChanged(nameof(RamFreePercent));
            OnPropertyChanged(nameof(RamPercentDisplay));
            OnPropertyChanged(nameof(RamSummary));
            return;
        }

        UsedRam = ByteFormatter.Format(mem.UsedBytes);
        TotalRam = ByteFormatter.Format(mem.TotalBytes);
        AvailableRam = ByteFormatter.Format(mem.AvailableBytes);
        RamPercent = mem.LoadPercent;
        OnPropertyChanged(nameof(RamFreePercent));
        OnPropertyChanged(nameof(RamPercentDisplay));
        OnPropertyChanged(nameof(RamSummary));
    }

    // ---- Laufwerke ---------------------------------------------------------

    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var drive in _systemInfo.GetFixedDrives())
            Drives.Add(drive);
        OnPropertyChanged(nameof(DrivesSummary));
        OnPropertyChanged(nameof(DriveCountDisplay));
    }

    /// <summary>Anzahl Laufwerke als lokalisierter Text (Kachel "SPEICHERPLATZ").</summary>
    public string DriveCountDisplay => Loc.T("dashboard.driveCount", Drives.Count);

    public string DrivesSummary
    {
        get
        {
            if (Drives.Count == 0) return Loc.T("dashboard.drives.none");
            long total = Drives.Sum(d => d.TotalBytes);
            long free = Drives.Sum(d => d.FreeBytes);
            return Loc.T("dashboard.drives.summary",
                Drives.Count, ByteFormatter.Format(free), ByteFormatter.Format(total));
        }
    }

    // ---- Score -------------------------------------------------------------

    public int Score { get => _score.Score; }
    public string ScoreLabel => _score.Label;
    public ScoreLevel ScoreLevel => _score.Level;

    // ---- Startup ----
    public int StartupTotal { get => _startupTotal; private set { if (SetProperty(ref _startupTotal, value)) OnPropertyChanged(nameof(StartupSummary)); } }
    public int StartupEnabled { get => _startupEnabled; private set { if (SetProperty(ref _startupEnabled, value)) OnPropertyChanged(nameof(StartupSummary)); } }
    public int StartupHighImpact { get => _startupHighImpact; private set { if (SetProperty(ref _startupHighImpact, value)) OnPropertyChanged(nameof(StartupSummary)); } }

    public string StartupSummary =>
        Loc.T("dashboard.startup.summary", StartupEnabled, StartupHighImpact);

    // ---- Temp ----
    public string TempReclaimable => _tempAnalyzed ? ByteFormatter.Format(_tempBytes) : "–";
    public int TempItems { get => _tempItems; private set => SetProperty(ref _tempItems, value); }
    public string TempSummary => _tempAnalyzed
        ? Loc.T("dashboard.temp.summary", ByteFormatter.Format(_tempBytes), _tempItems)
        : Loc.T("common.notAnalyzed");

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>True, solange der WMI-Scan läuft – steuert den Ladebalken der Health-Sektion.</summary>
    public bool IsDiskHealthLoading
    {
        get => _isDiskHealthLoading;
        private set
        {
            if (!SetProperty(ref _isDiskHealthLoading, value)) return;
            OnPropertyChanged(nameof(HasNoDiskHealth));
        }
    }

    /// <summary>True, wenn der Scan fertig ist und nichts gefunden hat – steuert den Leerzustand.</summary>
    public bool HasNoDiskHealth => !_isDiskHealthLoading && _diskHealthLoaded && DiskHealth.Count == 0;

    /// <summary>Startet den RAM-Timer und – einmalig – die schwere Analyse.</summary>
    public void EnsureAnalyzed()
    {
        if (!_ramTimer.IsEnabled) _ramTimer.Start();
        if (_analyzed) return;
        _analyzed = true;
        RefreshCommand.Execute(null);
    }

    /// <summary>
    /// Startet einmalig den Gesundheits-Scan der Datenträger. Wird vom
    /// <c>MainViewModel</c> beim ersten Öffnen des System-Bereichs aufgerufen.
    ///
    /// <para>Bewusst „fire and forget“ mit eigener Fehlerbehandlung: die Sektion ist rein
    /// informativ; scheitert der Scan, bleibt die Liste leer und der Leerzustand erscheint –
    /// das darf weder den System-Bereich blockieren noch die App stören.</para>
    /// </summary>
    public void EnsureDiskHealthLoaded()
    {
        if (_diskHealthLoaded || _isDiskHealthLoading) return;

        IsDiskHealthLoading = true;

        _ = LoadDiskHealthAsync();
    }

    private async Task LoadDiskHealthAsync()
    {
        try
        {
            // WMI blockiert – niemals auf dem UI-Thread.
            IReadOnlyList<DriveHealthInfo> drives =
                await Task.Run(() => DriveHealthService.Instance.GetDriveHealth()).ConfigureAwait(true);

            DiskHealth.Clear();
            foreach (DriveHealthInfo drive in drives)
                DiskHealth.Add(new DriveHealthViewModel(drive));
        }
        catch
        {
            DiskHealth.Clear();
        }
        finally
        {
            _diskHealthLoaded = true;
            IsDiskHealthLoading = false;
            OnPropertyChanged(nameof(HasNoDiskHealth));
        }
    }

    /// <summary>Recomputet ALLES: RAM, Laufwerke, Startup, Temp, Score und Empfehlungen.</summary>
    public async Task AnalyzeAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("dashboard.status.analyzing");

        RefreshRam();
        RefreshDrives();

        // Schwere I/O im Hintergrund: Temp-Scan + Autostart-Enumeration.
        var (tempBytes, tempItems, startupTotal, startupEnabled, startupHigh) =
            await Task.Run(() =>
            {
                long tBytes = 0;
                int tItems = 0;
                try
                {
                    var cats = _temp.CreateCategories();
                    var res = _temp.ScanAll(cats, null);
                    foreach (var (_, items) in res)
                        foreach (var item in items)
                        {
                            tBytes += item.SizeBytes;
                            tItems++;
                        }
                }
                catch { /* Temp-Analyse fehlgeschlagen -> 0 */ }

                int sTotal = 0, sEnabled = 0, sHigh = 0;
                try
                {
                    var entries = _startup.GetEntries();
                    sTotal = entries.Count;
                    sEnabled = entries.Count(e => e.IsEnabled);
                    sHigh = entries.Count(e => e.IsEnabled && e.Impact == StartupImpact.High);
                }
                catch { /* Autostart-Analyse fehlgeschlagen -> 0 */ }

                return (tBytes, tItems, sTotal, sEnabled, sHigh);
            });

        _tempBytes = tempBytes;
        _tempAnalyzed = true;
        TempItems = tempItems;
        OnPropertyChanged(nameof(TempReclaimable));
        OnPropertyChanged(nameof(TempSummary));

        StartupTotal = startupTotal;
        StartupEnabled = startupEnabled;
        StartupHighImpact = startupHigh;

        ComputeScore();
        BuildRecommendations();

        IsBusy = false;
        StatusText = Loc.T("dashboard.status.done", Score, ScoreLabel);
    }

    // ---- Score-Berechnung (nur echte Messwerte) ----------------------------

    private void ComputeScore()
    {
        // Reine Rechen-Engine (WPF-frei, testbar); das VM sammelt nur die Messwerte
        // und spiegelt das Ergebnis in seine gebundenen Collections/Properties.
        _score = SystemScoreCalculator.Compute(CollectMeasurements());

        ScoreFactors.Clear();
        foreach (var f in _score.Factors)
            ScoreFactors.Add(f);

        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(ScoreLabel));
        OnPropertyChanged(nameof(ScoreLevel));
        OnPropertyChanged(nameof(HasScoreFactors));
        OnPropertyChanged(nameof(ScoreExplanation));
    }

    /// <summary>Sammelt die aktuellen Roh-Messwerte für Score-/Empfehlungs-Engine.</summary>
    private SystemMeasurements CollectMeasurements()
    {
        var drives = Drives
            .Select(d => new DriveMeasurement(d.Letter, d.FreePercent, d.UsedPercent, d.FreeDisplay))
            .ToList();

        return new SystemMeasurements(
            drives, _tempBytes, _tempItems, _startupEnabled, _startupHighImpact, _ramValid, _ramPercent);
    }

    public bool HasScoreFactors => ScoreFactors.Count > 0;

    public string ScoreExplanation => HasScoreFactors
        ? Loc.T("score.explanation.factors")
        : Loc.T("score.explanation.clean");

    // ---- Empfehlungen ------------------------------------------------------

    private void BuildRecommendations()
    {
        Recommendations.Clear();

        // Nebenwirkungen (Navigation, Bereinigungs-Scan, RAM-Refresh) als Aktions-Bündel an
        // die reine Engine reichen; das „In Bereinigung wechseln und scannen“ bleibt identisch.
        var actions = new RecommendationActions(
            _navigate,
            new RelayCommand(_ =>
            {
                _navigate(AppSection.Bereinigung);
                if (_cleaner.ScanCommand.CanExecute(null))
                    _cleaner.ScanCommand.Execute(null);
            }),
            RefreshRamCommand);

        // Die Engine liefert bereits dringlichste zuerst (Kritisch → Warnung → Hinweis).
        foreach (var r in RecommendationBuilder.Build(CollectMeasurements(), actions))
            Recommendations.Add(r);

        OnPropertyChanged(nameof(HasRecommendations));
        OnPropertyChanged(nameof(RecommendationCount));
        OnPropertyChanged(nameof(HasActionableRecommendations));
    }

    /// <summary>Führt die sinnvollen Empfehlungs-Aktionen aus („Alle ausführen“).</summary>
    private void RunAllRecommendations()
    {
        RefreshRam();

        // Speicher-/Bereinigungs-Empfehlungen bündeln in einem Scan im Bereinigen-Bereich.
        if (Recommendations.Any(r => r.Kind is RecommendationKind.Reclaimable or RecommendationKind.Storage))
        {
            _navigate(AppSection.Bereinigung);
            if (_cleaner.ScanCommand.CanExecute(null))
                _cleaner.ScanCommand.Execute(null);
        }
        else if (Recommendations.Any(r => r.Kind == RecommendationKind.Startup))
        {
            _navigate(AppSection.Autostart);
        }
    }

    public bool HasRecommendations => Recommendations.Count > 0;

    /// <summary>Anzahl der Empfehlungen (für den Zähler-Badge im Kopf).</summary>
    public int RecommendationCount => Recommendations.Count;

    /// <summary>Gibt es mindestens eine Empfehlung mit ausführbarer Aktion? (steuert „Alle ausführen“).</summary>
    public bool HasActionableRecommendations => Recommendations.Any(r => r.HasAction);

    /// <summary>Baut nach einem Sprachwechsel alle berechneten Texte neu auf.</summary>
    public void Relocalize()
    {
        // Reine Anzeige-Properties neu auswerten lassen.
        OnPropertyChanged(nameof(RamSummary));
        OnPropertyChanged(nameof(DrivesSummary));
        OnPropertyChanged(nameof(DriveCountDisplay));
        OnPropertyChanged(nameof(StartupSummary));
        OnPropertyChanged(nameof(TempReclaimable));
        OnPropertyChanged(nameof(TempSummary));

        foreach (DriveHealthViewModel drive in DiskHealth)
            drive.Relocalize();

        if (_analyzed && !IsBusy)
        {
            // Score-Faktoren und Empfehlungen enthalten übersetzten Text -> neu berechnen.
            ComputeScore();
            BuildRecommendations();
            StatusText = Loc.T("dashboard.status.done", Score, ScoreLabel);
        }
        else if (!_analyzed)
        {
            StatusText = Loc.T("dashboard.status.preparing");
        }
    }
}
