using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;
using OpenClean.Services.Safety;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>
/// Agent 1 + 2: Steuert Scan → Vorschau → Bereinigen.
/// Es wird nie etwas gelöscht, bevor der Nutzer nach einem Scan explizit bestätigt.
/// </summary>
public sealed class CleanerViewModel : ViewModelBase
{
    private readonly TempScannerService _scanner = new();
    private readonly CleanerService _cleaner = new();

    private bool _hasScanned;
    private bool _suppressSelectionCallback;
    private string _statusText = Loc.T("cleaner.status.ready");
    private bool _isBusy;
    private bool _isLargeFilesTab;

    private double _scanProgressPercent;
    private string _scanProgressText = "";
    private string _scanEtaText = "";

    private string _lastReportText = "";
    private bool _hasReport;

    /// <summary>Id des zuletzt gesicherten Bereinigungs-Durchlaufs – Grundlage des „Rückgängig"-Buttons.</summary>
    private string? _lastBackupSessionId;

    public ObservableCollection<CleanupCategory> Categories { get; } = new();

    /// <summary>Untergeordnetes ViewModel des Große-Dateien-Tabs.</summary>
    public LargeFilesViewModel LargeFiles { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public AsyncRelayCommand UndoLastCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand ShowCategoriesCommand { get; private set; } = null!;
    public RelayCommand ShowLargeFilesCommand { get; private set; } = null!;

    public CleanerViewModel()
    {
        foreach (var category in _scanner.CreateCategories())
        {
            // Echtes Browser-Icon (Chrome/Edge/Brave/Firefox); null für andere Kategorien.
            // WICHTIG: vor HookCategory setzen – sonst löst das PropertyChanged bereits
            // RefreshSelectionState() aus, bevor die Commands initialisiert sind (NRE).
            category.IconImage = BrowserIcons.Resolve(category.Key);
            HookCategory(category);
            Categories.Add(category);
        }

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync());
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => CanClean);
        UndoLastCommand = new AsyncRelayCommand(_ => UndoLastAsync(), _ => CanUndoLast);
        SelectAllCommand = new RelayCommand(_ => SetAllSelection(true), _ => CanChangeSelection);
        DeselectAllCommand = new RelayCommand(_ => SetAllSelection(false), _ => CanChangeSelection);
        ShowCategoriesCommand = new RelayCommand(_ => IsLargeFilesTab = false);
        ShowLargeFilesCommand = new RelayCommand(_ => IsLargeFilesTab = true);
    }

    /// <summary>True, wenn der Große-Dateien-Tab aktiv ist.</summary>
    public bool IsLargeFilesTab
    {
        get => _isLargeFilesTab;
        set
        {
            if (SetProperty(ref _isLargeFilesTab, value))
                OnPropertyChanged(nameof(IsCategoriesTab));
        }
    }

    /// <summary>True, wenn der Kategorien-Tab aktiv ist (Gegenstück für die Sichtbarkeit).</summary>
    public bool IsCategoriesTab => !_isLargeFilesTab;

    /// <summary>Auswahl-Buttons nur sinnvoll, wenn es nach einem Scan überhaupt Items gibt.</summary>
    public bool CanChangeSelection => !IsBusy && Categories.Any(c => c.Items.Count > 0);

    /// <summary>Setzt bzw. entfernt die Auswahl aller Vorschau-Items über alle Kategorien.</summary>
    private void SetAllSelection(bool selected)
    {
        // Bulk-Operation: pro-Item-Callback unterdrücken, um den Benachrichtigungssturm
        // (O(n²)) zu vermeiden – nur die billigen IsSelected-PropertyChanged feuern.
        _suppressSelectionCallback = true;
        try
        {
            foreach (var category in Categories)
                foreach (var item in category.Items)
                    item.IsSelected = selected;
        }
        finally
        {
            _suppressSelectionCallback = false;
        }

        // Genau EINE Gesamt-Aktualisierung nach der Schleife.
        foreach (var category in Categories)
            category.RefreshTotals();
        RefreshSelectionState();
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CleanCommand.RaiseCanExecuteChanged();
                UndoLastCommand.RaiseCanExecuteChanged();
                SelectAllCommand.RaiseCanExecuteChanged();
                DeselectAllCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanChangeSelection));
                OnPropertyChanged(nameof(CanUndoLast));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Fortschritt der laufenden Analyse in Prozent (0–100) für die determinierte ProgressBar.</summary>
    public double ScanProgressPercent
    {
        get => _scanProgressPercent;
        private set => SetProperty(ref _scanProgressPercent, value);
    }

    /// <summary>Text unter der ProgressBar, z. B. „Analysiere: C:\…“.</summary>
    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    /// <summary>Geschätzte Restzeit, z. B. „ca. 00:12 verbleibend“.</summary>
    public string ScanEtaText
    {
        get => _scanEtaText;
        private set => SetProperty(ref _scanEtaText, value);
    }

    /// <summary>Ergebnis-Banner nach dem Bereinigen, z. B. „12 Dateien gelöscht · 34 MB freigegeben“.</summary>
    public string LastReportText
    {
        get => _lastReportText;
        private set => SetProperty(ref _lastReportText, value);
    }

    /// <summary>Ob ein Ergebnis-Banner angezeigt werden soll.</summary>
    public bool HasReport
    {
        get => _hasReport;
        private set => SetProperty(ref _hasReport, value);
    }

    /// <summary>True, wenn der letzte Durchlauf gesichert wurde und rückgängig gemacht werden kann.</summary>
    public bool CanUndoLast => _lastBackupSessionId is not null && !IsBusy;

    // !IntegrityState.IsBlocked: bei erkannter Manipulation (OPCL-20) bleibt der Scan nutzbar,
    // der Bereinigen-Knopf aber deaktiviert. CleanerService sperrt zusätzlich selbst.
    public bool CanClean => _hasScanned && !IsBusy && SelectedCount > 0 && !IntegrityState.IsBlocked;

    public long TotalSelectedBytes => Categories.Sum(c => c.SelectedBytes);
    public int SelectedCount => Categories.Sum(c => c.SelectedCount);

    public string SelectionSummary =>
        _hasScanned
            ? Loc.T("cleaner.selection.summary", SelectedCount, ByteFormatter.Format(TotalSelectedBytes))
            : Loc.T("common.notAnalyzed");

    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("cleaner.status.scanning");
        _hasScanned = false;
        HasReport = false;
        ScanProgressPercent = 0;
        ScanProgressText = Loc.T("cleaner.status.preparing");
        ScanEtaText = "";

        var enabled = Categories.Where(c => c.IsEnabled).ToList();

        // IProgress auf dem UI-Thread erzeugt → Callbacks werden automatisch dorthin marshalled.
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressPercent = p.Percent;
            ScanProgressText = string.IsNullOrEmpty(p.CurrentPath)
                ? Loc.T("cleaner.progress.analyzing")
                : Loc.T("cleaner.progress.analyzingPath", p.CurrentPath);
            ScanEtaText = FormatEta(stopwatch.Elapsed, p.Done, p.Total);
        });

        // I/O im Hintergrund: reine Berechnung, keine Collection-Mutation.
        var scanned = await Task.Run(() => _scanner.ScanAll(enabled, progress));
        stopwatch.Stop();

        // Ergebnisse auf dem UI-Thread anwenden (ObservableCollection nur hier anfassen).
        foreach (var category in Categories)
            category.Reset();

        foreach (var (category, items) in scanned)
        {
            foreach (var item in items)
                category.Items.Add(item);

            // Nach dem Befüllen in Unterkategorien gruppieren (referenziert dieselben Items).
            category.RebuildSubcategories();

            // Ein Einzel-Toggle aktualisiert Kategorie (kaskadiert zu allen Unterkategorien)
            // und die Gesamtauswahl.
            foreach (var item in category.Items)
            {
                item.SelectionChanged = () =>
                {
                    if (_suppressSelectionCallback) return;
                    category.RefreshTotals();
                    RefreshSelectionState();
                };
            }
        }

        foreach (var category in Categories)
            category.RefreshTotals();

        _hasScanned = true;
        IsBusy = false;
        ScanProgressPercent = 100;
        ScanProgressText = "";
        ScanEtaText = "";
        RefreshSelectionState();
        StatusText = TotalSelectedBytes > 0
            ? Loc.T("cleaner.status.doneSelectable")
            : Loc.T("cleaner.status.doneEmpty");
    }

    /// <summary>Schätzt aus verstrichener Zeit und Fortschritt die verbleibende Restzeit.</summary>
    private static string FormatEta(TimeSpan elapsed, int done, int total)
    {
        if (done <= 0 || total <= 0 || done >= total) return "";
        double perItem = elapsed.TotalSeconds / done;
        double remainingSeconds = perItem * (total - done);
        if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds) || remainingSeconds < 0)
            return "";
        var remaining = TimeSpan.FromSeconds(remainingSeconds);
        string text = remaining.TotalHours >= 1
            ? remaining.ToString(@"h\:mm\:ss")
            : remaining.ToString(@"mm\:ss");
        return Loc.T("cleaner.eta.remaining", text);
    }

    private async Task CleanAsync()
    {
        long expectedBytes = TotalSelectedBytes;
        int expectedCount = SelectedCount;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("cleaner.confirm.message", expectedCount, ByteFormatter.Format(expectedBytes)));

        if (!confirmed) return;

        IsBusy = true;
        HasReport = false;
        StatusText = Loc.T("cleaner.progress.deleting");
        ScanProgressPercent = 0;
        ScanProgressText = Loc.T("cleaner.confirm.preparing");
        ScanEtaText = "";

        // Sicherheitsnetze VOR dem Löschen: Wiederherstellungspunkt (mit Rückfrage bei Fehlschlag)
        // und Backup-Sitzung. Bricht der Nutzer am Gate ab, wird nichts gelöscht.
        SafetyPreparation prep = await SafetyPrompt.PrepareAsync(
            Application.Current?.MainWindow, "cleanup", msg => ScanProgressText = msg);

        if (!prep.Proceed)
        {
            IsBusy = false;
            StatusText = Loc.T("safety.aborted");
            ScanProgressText = "";
            return;
        }

        // IProgress auf dem UI-Thread → Callbacks marshallen automatisch zurück.
        var progress = new Progress<CleanupProgress>(p =>
        {
            ScanProgressPercent = p.Percent;
            ScanProgressText = string.IsNullOrEmpty(p.CurrentPath)
                ? Loc.T("cleaner.progress.deleting")
                : Loc.T("cleaner.progress.deletingPath", p.CurrentPath);
            ScanEtaText = "";
        });

        var snapshot = Categories.ToList();
        var safety = new CleanupSafetyContext { Session = prep.Session, CreateRestorePointHere = false };
        try
        {
            CleanupReport report = await Task.Run(() => _cleaner.Clean(snapshot, progress, safety));

            // Undo-Ziel merken (nur wenn wirklich etwas gesichert wurde).
            _lastBackupSessionId = (prep.Session is not null && prep.Session.Count > 0) ? prep.Session.Id : null;

            // Nach dem Löschen frisch scannen, damit die Liste den echten Zustand zeigt.
            await ScanAsync();

            StatusText = report.Summary;
            LastReportText = Loc.T("cleaner.report", report.DeletedCount, ByteFormatter.Format(report.FreedBytes))
                + (report.Skipped.Count > 0 ? Loc.T("cleaner.report.skipped", report.Skipped.Count) : "");
            HasReport = true;
        }
        finally
        {
            // Manifest IMMER schreiben (auch bei unerwartetem Fehler) – sonst wären bereits gelöschte
            // Dateien ohne Undo und der Ordner würde von der Retention nie aufgeräumt. Commit ist idempotent.
            prep.Session?.Commit();
            // UI nie gesperrt zurücklassen (ScanAsync setzt IsBusy im Normalfall bereits zurück).
            if (IsBusy) IsBusy = false;
            OnPropertyChanged(nameof(CanUndoLast));
            UndoLastCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Macht den zuletzt gesicherten Bereinigungs-Durchlauf rückgängig (stellt die Dateien wieder her).</summary>
    private async Task UndoLastAsync()
    {
        string? id = _lastBackupSessionId;
        if (id is null) return;

        IsBusy = true;
        StatusText = Loc.T("safety.undo.running");

        UndoResult result = await Task.Run(() => BackupService.Instance.Restore(id));

        _lastBackupSessionId = null;
        HasReport = false;

        // Frisch scannen, damit die wiederhergestellten Dateien wieder in der Vorschau auftauchen.
        await ScanAsync();

        IsBusy = false;
        StatusText = result.Failed > 0
            ? Loc.T("safety.undo.partial", result.Restored, result.Failed)
            : Loc.T("safety.undo.done", result.Restored, ByteFormatter.Format(result.RestoredBytes));
        OnPropertyChanged(nameof(CanUndoLast));
        UndoLastCommand.RaiseCanExecuteChanged();
    }

    private void HookCategory(CleanupCategory category)
    {
        // Reagiert auf IsEnabled/AllSelected-Änderungen der Kategorie (Item-Callbacks
        // werden beim Befüllen in ScanAsync gesetzt).
        category.PropertyChanged += (_, _) => RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(TotalSelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(CanChangeSelection));
        CleanCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        DeselectAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel alle berechneten Texte.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        // Kategorie-Name/Beschreibung/Kopfzeile neu auswerten.
        foreach (var category in Categories)
            category.RefreshLabels();
        if (!IsBusy && !_hasScanned)
            StatusText = Loc.T("cleaner.status.ready");
        LargeFiles.Relocalize();
    }
}
