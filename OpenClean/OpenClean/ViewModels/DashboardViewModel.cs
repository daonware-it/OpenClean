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
    private ScoreResult _score = new() { Score = 100, Level = ScoreLevel.Good, Label = "Gut" };

    // ---- Startup ----
    private int _startupTotal;
    private int _startupEnabled;
    private int _startupHighImpact;

    // ---- Temp ----
    private long _tempBytes;
    private int _tempItems;
    private bool _tempAnalyzed;

    private bool _isBusy;
    private string _statusText = "Analyse wird vorbereitet …";

    public ObservableCollection<DriveUsage> Drives { get; } = new();
    public ObservableCollection<ScoreFactor> ScoreFactors { get; } = new();
    public ObservableCollection<Recommendation> Recommendations { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand RefreshRamCommand { get; }
    public RelayCommand OpenSystemCommand { get; }
    public RelayCommand OpenCleanerCommand { get; }
    public RelayCommand OpenStartupCommand { get; }

    public DashboardViewModel(Action<AppSection> navigate, CleanerViewModel cleaner)
    {
        _navigate = navigate;
        _cleaner = cleaner;

        RefreshCommand = new AsyncRelayCommand(_ => AnalyzeAsync());
        RefreshRamCommand = new RelayCommand(_ => RefreshRam());
        OpenSystemCommand = new RelayCommand(_ => _navigate(AppSection.System));
        OpenCleanerCommand = new RelayCommand(_ => _navigate(AppSection.Bereinigung));
        OpenStartupCommand = new RelayCommand(_ => _navigate(AppSection.Autostart));

        // RAM live halten (nur Anzeige, keine Aktion). Alle 3 s aktualisieren.
        _ramTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
        ? $"{UsedRam} von {TotalRam} belegt · {AvailableRam} frei"
        : "RAM-Auslastung nicht verfügbar.";

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
    }

    public string DrivesSummary
    {
        get
        {
            if (Drives.Count == 0) return "Keine festen Laufwerke gefunden.";
            long total = Drives.Sum(d => d.TotalBytes);
            long free = Drives.Sum(d => d.FreeBytes);
            return $"{Drives.Count} Laufwerk(e) · {ByteFormatter.Format(free)} von {ByteFormatter.Format(total)} frei";
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
        $"{StartupEnabled} aktiv · {StartupHighImpact} mit hoher Startlast";

    // ---- Temp ----
    public string TempReclaimable => _tempAnalyzed ? ByteFormatter.Format(_tempBytes) : "–";
    public int TempItems { get => _tempItems; private set => SetProperty(ref _tempItems, value); }
    public string TempSummary => _tempAnalyzed
        ? $"{ByteFormatter.Format(_tempBytes)} in {_tempItems} Objekt(en) bereinigbar"
        : "Noch nicht analysiert.";

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
        StatusText = "Analysiere System (Temp-/Cache-Größe, Autostart, Laufwerke) …";

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
        StatusText = $"Analyse abgeschlossen · Score {Score}/100 ({ScoreLabel}).";
    }

    // ---- Score-Berechnung (nur echte Messwerte) ----------------------------

    private void ComputeScore()
    {
        int score = 100;
        var factors = new List<ScoreFactor>();

        void Penalty(int points, string description)
        {
            if (points <= 0) return;
            score -= points;
            factors.Add(new ScoreFactor(description, -points));
        }

        // 1) Speicherplatz je Laufwerk (echte DriveInfo-Werte).
        foreach (var drive in Drives)
        {
            double freePct = drive.FreePercent;
            if (freePct < 10)
                Penalty(25, $"Laufwerk {drive.Letter} nur {freePct:0} % frei (kritisch)");
            else if (freePct < 20)
                Penalty(10, $"Laufwerk {drive.Letter} nur {freePct:0} % frei (knapp)");
        }

        // 2) Bereinigbare Temp-/Cache-Größe (echtes ScanAll-Ergebnis).
        double tempGb = _tempBytes / (1024.0 * 1024 * 1024);
        double tempMb = _tempBytes / (1024.0 * 1024);
        if (tempGb > 5)
            Penalty(20, $"{ByteFormatter.Format(_tempBytes)} bereinigbar (Temp/Cache)");
        else if (tempGb > 1)
            Penalty(10, $"{ByteFormatter.Format(_tempBytes)} bereinigbar (Temp/Cache)");
        else if (tempMb > 200)
            Penalty(5, $"{ByteFormatter.Format(_tempBytes)} bereinigbar (Temp/Cache)");

        // 3) Autostart (echte StartupService-Daten).
        if (_startupEnabled > 20)
            Penalty(15, $"{_startupEnabled} Autostart-Programme aktiv");
        else if (_startupEnabled > 10)
            Penalty(8, $"{_startupEnabled} Autostart-Programme aktiv");

        if (_startupHighImpact > 0)
        {
            int highPenalty = Math.Min(_startupHighImpact * 3, 12);
            Penalty(highPenalty, $"{_startupHighImpact} Autostart mit hoher Startlast");
        }

        // 4) RAM-Auslastung (echtes dwMemoryLoad).
        if (_ramValid)
        {
            if (_ramPercent > 90)
                Penalty(15, $"RAM-Auslastung {_ramPercent:0} %");
            else if (_ramPercent > 80)
                Penalty(8, $"RAM-Auslastung {_ramPercent:0} %");
        }

        score = Math.Clamp(score, 0, 100);

        var (level, label) = score >= 80
            ? (ScoreLevel.Good, "Gut")
            : score >= 50
                ? (ScoreLevel.Improvable, "Verbesserbar")
                : (ScoreLevel.Critical, "Kritisch");

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
        ? "Diese Faktoren haben Punkte gekostet:"
        : "Keine messbaren Probleme gefunden – alles im grünen Bereich.";

    // ---- Empfehlungen ------------------------------------------------------

    private void BuildRecommendations()
    {
        Recommendations.Clear();

        double tempMb = _tempBytes / (1024.0 * 1024);
        if (tempMb > 200)
        {
            Recommendations.Add(new Recommendation
            {
                Title = $"{ByteFormatter.Format(_tempBytes)} bereinigbar",
                Text = $"{_tempItems} temporäre Dateien/Caches können freigegeben werden.",
                Severity = tempMb > 1024 ? RecommendationSeverity.Warning : RecommendationSeverity.Info,
                ActionLabel = "Bereinigen",
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
            Recommendations.Add(new Recommendation
            {
                Title = $"Laufwerk {drive.Letter} ist zu {drive.UsedPercent:0} % belegt",
                Text = $"Nur noch {drive.FreeDisplay} frei. Bereinigen schafft Platz.",
                Severity = drive.FreePercent < 10 ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                ActionLabel = "Bereinigen",
                ActionCommand = new RelayCommand(_ => _navigate(AppSection.Bereinigung))
            });
        }

        if (_startupEnabled > 10 || _startupHighImpact > 0)
        {
            Recommendations.Add(new Recommendation
            {
                Title = $"{_startupEnabled} Autostart-Programme prüfen",
                Text = _startupHighImpact > 0
                    ? $"{_startupHighImpact} davon mit hoher Startlast – Abschalten kann den Systemstart beschleunigen."
                    : "Viele Programme starten mit Windows – prüfe, was du wirklich brauchst.",
                Severity = _startupHighImpact > 0 ? RecommendationSeverity.Warning : RecommendationSeverity.Info,
                ActionLabel = "Autostart öffnen",
                ActionCommand = new RelayCommand(_ => _navigate(AppSection.Autostart))
            });
        }

        // RAM: rein informativ – KEINE „RAM-Booster“-Aktion (wäre Fake), nur Aktualisieren.
        if (_ramValid && _ramPercent > 85)
        {
            Recommendations.Add(new Recommendation
            {
                Title = $"Hohe RAM-Auslastung ({_ramPercent:0} %)",
                Text = "Nur Information: Schließe nicht benötigte Programme. OpenClean gibt keinen Arbeitsspeicher „frei“.",
                Severity = RecommendationSeverity.Info,
                ActionLabel = "Aktualisieren",
                ActionCommand = RefreshRamCommand
            });
        }

        if (Recommendations.Count == 0)
        {
            Recommendations.Add(new Recommendation
            {
                Title = "Alles im grünen Bereich",
                Text = "Keine messbaren Probleme gefunden. Dein System ist sauber und gut ausgelastet.",
                Severity = RecommendationSeverity.Positive
            });
        }

        OnPropertyChanged(nameof(HasRecommendations));
    }

    public bool HasRecommendations => Recommendations.Count > 0;
}
