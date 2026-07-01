using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using OpenClean.Models;
using OpenClean.Services;
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
    private string _statusText = "Bereit. Klicke auf »Analysieren«, um zu sehen, was bereinigt werden kann.";
    private bool _isBusy;

    private double _scanProgressPercent;
    private string _scanProgressText = "";
    private string _scanEtaText = "";

    private string _lastReportText = "";
    private bool _hasReport;

    public ObservableCollection<CleanupCategory> Categories { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }

    public CleanerViewModel()
    {
        foreach (var category in _scanner.CreateCategories())
        {
            HookCategory(category);
            Categories.Add(category);
        }

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync());
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => CanClean);
        SelectAllCommand = new RelayCommand(_ => SetAllSelection(true), _ => CanChangeSelection);
        DeselectAllCommand = new RelayCommand(_ => SetAllSelection(false), _ => CanChangeSelection);
    }

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
                SelectAllCommand.RaiseCanExecuteChanged();
                DeselectAllCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanChangeSelection));
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

    public bool CanClean => _hasScanned && !IsBusy && SelectedCount > 0;

    public long TotalSelectedBytes => Categories.Sum(c => c.SelectedBytes);
    public int SelectedCount => Categories.Sum(c => c.SelectedCount);

    public string SelectionSummary =>
        _hasScanned
            ? $"{SelectedCount} Objekt(e) ausgewählt · {ByteFormatter.Format(TotalSelectedBytes)} werden freigegeben"
            : "Noch nicht analysiert.";

    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusText = "Analysiere Temp- und Cache-Verzeichnisse …";
        _hasScanned = false;
        HasReport = false;
        ScanProgressPercent = 0;
        ScanProgressText = "Analyse wird vorbereitet …";
        ScanEtaText = "";

        var enabled = Categories.Where(c => c.IsEnabled).ToList();

        // IProgress auf dem UI-Thread erzeugt → Callbacks werden automatisch dorthin marshalled.
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressPercent = p.Percent;
            ScanProgressText = string.IsNullOrEmpty(p.CurrentPath)
                ? "Analysiere …"
                : $"Analysiere: {p.CurrentPath}";
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
            {
                item.SelectionChanged = () =>
                {
                    if (_suppressSelectionCallback) return;
                    category.RefreshTotals();
                    RefreshSelectionState();
                };
                category.Items.Add(item);
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
            ? "Analyse abgeschlossen. Prüfe die Liste und klicke auf »Bereinigen«."
            : "Analyse abgeschlossen – nichts zu bereinigen gefunden.";
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
        return $"ca. {remaining:mm\\:ss} verbleibend";
    }

    private async Task CleanAsync()
    {
        long expectedBytes = TotalSelectedBytes;
        int expectedCount = SelectedCount;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            $"{expectedCount} Objekt(e) mit insgesamt {ByteFormatter.Format(expectedBytes)} werden unwiderruflich gelöscht.\n\n" +
            "Möchtest du fortfahren?");

        if (!confirmed) return;

        IsBusy = true;
        HasReport = false;
        StatusText = "Bereinige …";
        ScanProgressPercent = 0;
        ScanProgressText = "Bereinigung wird vorbereitet …";
        ScanEtaText = "";

        // IProgress auf dem UI-Thread → Callbacks marshallen automatisch zurück.
        var progress = new Progress<CleanupProgress>(p =>
        {
            ScanProgressPercent = p.Percent;
            ScanProgressText = string.IsNullOrEmpty(p.CurrentPath) ? "Lösche …" : $"Lösche: {p.CurrentPath}";
            ScanEtaText = "";
        });

        var snapshot = Categories.ToList();
        CleanupReport report = await Task.Run(() => _cleaner.Clean(snapshot, progress));

        // Nach dem Löschen frisch scannen, damit die Liste den echten Zustand zeigt.
        await ScanAsync();

        StatusText = report.Summary;
        LastReportText = $"{report.DeletedCount} Datei(en) gelöscht · {ByteFormatter.Format(report.FreedBytes)} freigegeben"
            + (report.Skipped.Count > 0 ? $" · {report.Skipped.Count} übersprungen" : "");
        HasReport = true;
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
}
