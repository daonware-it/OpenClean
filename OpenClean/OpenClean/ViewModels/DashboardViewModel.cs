using System.Collections.ObjectModel;
using System.Windows.Threading;
using OpenClean.Models;
using OpenClean.Services;

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

    /// <summary>Startet den RAM-Timer und – einmalig – die schwere Analyse.</summary>
    public void EnsureAnalyzed()
    {
        if (!_ramTimer.IsEnabled) _ramTimer.Start();
        if (_analyzed) return;
        _analyzed = true;
        RefreshCommand.Execute(null);
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
        int score = 100;
        var factors = new List<ScoreFactor>();

        void Penalty(int points, ScoreFactorKind kind, string description)
        {
            if (points <= 0) return;
            score -= points;
            factors.Add(new ScoreFactor(description, -points, kind));
        }

        // 1) Speicherplatz je Laufwerk (echte DriveInfo-Werte).
        foreach (var drive in Drives)
        {
            double freePct = drive.FreePercent;
            if (freePct < 10)
                Penalty(25, ScoreFactorKind.Storage, Loc.T("score.factor.driveCritical", drive.Letter, freePct.ToString("0")));
            else if (freePct < 20)
                Penalty(10, ScoreFactorKind.Storage, Loc.T("score.factor.driveLow", drive.Letter, freePct.ToString("0")));
        }

        // 2) Bereinigbare Temp-/Cache-Größe (echtes ScanAll-Ergebnis).
        double tempGb = _tempBytes / (1024.0 * 1024 * 1024);
        double tempMb = _tempBytes / (1024.0 * 1024);
        if (tempGb > 5)
            Penalty(20, ScoreFactorKind.Reclaimable, Loc.T("score.factor.reclaimable", ByteFormatter.Format(_tempBytes)));
        else if (tempGb > 1)
            Penalty(10, ScoreFactorKind.Reclaimable, Loc.T("score.factor.reclaimable", ByteFormatter.Format(_tempBytes)));
        else if (tempMb > 200)
            Penalty(5, ScoreFactorKind.Reclaimable, Loc.T("score.factor.reclaimable", ByteFormatter.Format(_tempBytes)));

        // 3) Autostart (echte StartupService-Daten).
        if (_startupEnabled > 20)
            Penalty(15, ScoreFactorKind.Startup, Loc.T("score.factor.startupActive", _startupEnabled));
        else if (_startupEnabled > 10)
            Penalty(8, ScoreFactorKind.Startup, Loc.T("score.factor.startupActive", _startupEnabled));

        if (_startupHighImpact > 0)
        {
            int highPenalty = Math.Min(_startupHighImpact * 3, 12);
            Penalty(highPenalty, ScoreFactorKind.Startup, Loc.T("score.factor.startupHigh", _startupHighImpact));
        }

        // 4) RAM-Auslastung (echtes dwMemoryLoad).
        if (_ramValid)
        {
            if (_ramPercent > 90)
                Penalty(15, ScoreFactorKind.Ram, Loc.T("score.factor.ramLoad", _ramPercent.ToString("0")));
            else if (_ramPercent > 80)
                Penalty(8, ScoreFactorKind.Ram, Loc.T("score.factor.ramLoad", _ramPercent.ToString("0")));
        }

        score = Math.Clamp(score, 0, 100);

        var (level, label) = score >= 80
            ? (ScoreLevel.Good, Loc.T("score.good"))
            : score >= 50
                ? (ScoreLevel.Improvable, Loc.T("score.improvable"))
                : (ScoreLevel.Critical, Loc.T("score.critical"));

        _score = new ScoreResult
        {
            Score = score,
            Level = level,
            Label = label,
            Factors = factors
        };

        ScoreFactors.Clear();
        foreach (var f in factors)
            ScoreFactors.Add(f);

        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(ScoreLabel));
        OnPropertyChanged(nameof(ScoreLevel));
        OnPropertyChanged(nameof(HasScoreFactors));
        OnPropertyChanged(nameof(ScoreExplanation));
    }

    public bool HasScoreFactors => ScoreFactors.Count > 0;

    public string ScoreExplanation => HasScoreFactors
        ? Loc.T("score.explanation.factors")
        : Loc.T("score.explanation.clean");

    // ---- Empfehlungen ------------------------------------------------------

    private void BuildRecommendations()
    {
        Recommendations.Clear();
        var list = new List<Recommendation>();

        double tempMb = _tempBytes / (1024.0 * 1024);
        if (tempMb > 200)
        {
            var severity = tempMb > 1024 ? RecommendationSeverity.Warning : RecommendationSeverity.Info;
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.reclaimable.title", ByteFormatter.Format(_tempBytes)),
                Text = Loc.T("reco.reclaimable.text", _tempItems),
                Severity = severity,
                Kind = RecommendationKind.Reclaimable,
                SeverityTag = SeverityTag(severity),
                ValueDisplay = ByteFormatter.Format(_tempBytes),
                ValueUnit = Loc.T("reco.unit.reclaimable"),
                ActionLabel = Loc.T("common.clean"),
                ActionCommand = new RelayCommand(_ =>
                {
                    _navigate(AppSection.Bereinigung);
                    if (_cleaner.ScanCommand.CanExecute(null))
                        _cleaner.ScanCommand.Execute(null);
                })
            });
        }

        // Laufwerke mit kritisch/knapp wenig Speicher.
        foreach (var drive in Drives.Where(d => d.FreePercent < 15))
        {
            var severity = drive.FreePercent < 10 ? RecommendationSeverity.Critical : RecommendationSeverity.Warning;
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.driveFull.title", drive.Letter, drive.UsedPercent.ToString("0")),
                Text = Loc.T("reco.driveFull.text", drive.FreeDisplay),
                Severity = severity,
                Kind = RecommendationKind.Storage,
                SeverityTag = SeverityTag(severity),
                ValueDisplay = drive.FreeDisplay,
                ValueUnit = Loc.T("reco.unit.free"),
                ActionLabel = Loc.T("reco.action.check"),
                ActionCommand = new RelayCommand(_ => _navigate(AppSection.Bereinigung))
            });
        }

        if (_startupEnabled > 10 || _startupHighImpact > 0)
        {
            var severity = _startupHighImpact > 0 ? RecommendationSeverity.Warning : RecommendationSeverity.Info;
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.startup.title", _startupEnabled),
                Text = Loc.T("reco.startup.text", _startupHighImpact),
                Severity = severity,
                Kind = RecommendationKind.Startup,
                SeverityTag = SeverityTag(severity),
                ValueDisplay = _startupEnabled.ToString(),
                ValueUnit = Loc.T("reco.unit.active"),
                ActionLabel = Loc.T("reco.action.view"),
                ActionCommand = new RelayCommand(_ => _navigate(AppSection.Autostart))
            });
        }

        // RAM: rein informativ – KEINE „RAM-Booster“-Aktion (wäre Fake), nur Aktualisieren.
        if (_ramValid && _ramPercent > 85)
        {
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.ram.title", _ramPercent.ToString("0")),
                Text = Loc.T("reco.ram.text"),
                Severity = RecommendationSeverity.Info,
                Kind = RecommendationKind.Ram,
                SeverityTag = SeverityTag(RecommendationSeverity.Info),
                ValueDisplay = _ramPercent.ToString("0") + " %",
                ValueUnit = Loc.T("reco.unit.load"),
                ActionLabel = Loc.T("common.refresh"),
                ActionCommand = RefreshRamCommand
            });
        }

        if (list.Count == 0)
        {
            list.Add(new Recommendation
            {
                Title = Loc.T("reco.allGood.title"),
                Text = Loc.T("reco.allGood.text"),
                Severity = RecommendationSeverity.Positive,
                Kind = RecommendationKind.AllGood,
                SeverityTag = SeverityTag(RecommendationSeverity.Positive)
            });
        }

        // Dringlichste zuerst (Kritisch → Warnung → Hinweis), Reihenfolge sonst stabil.
        foreach (var r in list.OrderByDescending(r => (int)r.Severity))
            Recommendations.Add(r);

        OnPropertyChanged(nameof(HasRecommendations));
        OnPropertyChanged(nameof(RecommendationCount));
        OnPropertyChanged(nameof(HasActionableRecommendations));
    }

    /// <summary>Lokalisierter Status-Tag je Dringlichkeit (Kritisch/Empfohlen/Hinweis/Optimal).</summary>
    private static string SeverityTag(RecommendationSeverity severity) => severity switch
    {
        RecommendationSeverity.Critical => Loc.T("reco.tag.critical"),
        RecommendationSeverity.Warning => Loc.T("reco.tag.recommended"),
        RecommendationSeverity.Info => Loc.T("reco.tag.hint"),
        _ => Loc.T("reco.tag.good")
    };

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
