using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>
/// Agent 1: Steuert Scan → Vorschau → Bereinigen für die Privatsphäre-Seite.
/// Es wird nie etwas gelöscht, bevor der Nutzer nach einem Scan explizit bestätigt.
/// Kategorien, die eine Bestätigung erfordern (z. B. Cookies), erhalten eine
/// besonders deutliche Warnung.
/// </summary>
public sealed class PrivacyViewModel : ViewModelBase
{
    private readonly PrivacyScannerService _scanner = new();

    private bool _hasScanned;
    private bool _suppressSelectionCallback;
    private bool _isBusy;
    private string _statusText = Loc.T("privacy.status.ready");

    private string _lastReportText = "";
    private bool _hasReport;

    public ObservableCollection<PrivacyGroup> Groups { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }

    public PrivacyViewModel()
    {
        // Nur verfügbare Provider werden als Kategorie angezeigt (install-gated).
        foreach (var provider in _scanner.AvailableProviders())
        {
            var group = new PrivacyGroup(provider);
            group.PropertyChanged += (_, _) => RefreshSelectionState();
            Groups.Add(group);
        }

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync());
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => CanClean);
        SelectAllCommand = new RelayCommand(_ => SetAllSelection(true), _ => CanChangeSelection);
        DeselectAllCommand = new RelayCommand(_ => SetAllSelection(false), _ => CanChangeSelection);
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

    /// <summary>Ergebnis-Banner nach dem Bereinigen (Anzahl gelöschter Einträge je Kategorie).</summary>
    public string LastReportText
    {
        get => _lastReportText;
        private set => SetProperty(ref _lastReportText, value);
    }

    public bool HasReport
    {
        get => _hasReport;
        private set => SetProperty(ref _hasReport, value);
    }

    public bool CanClean => _hasScanned && !IsBusy && SelectedCount > 0;
    public bool CanChangeSelection => !IsBusy && Groups.Any(g => g.Items.Count > 0);

    public int SelectedCount => Groups.Sum(g => g.SelectedCount);

    public string SelectionSummary =>
        _hasScanned
            ? Loc.T("privacy.selection.summary", SelectedCount)
            : Loc.T("common.notAnalyzed");

    private void SetAllSelection(bool selected)
    {
        // Bulk-Operation: pro-Item-Callback unterdrücken, um den Benachrichtigungssturm zu vermeiden.
        _suppressSelectionCallback = true;
        try
        {
            foreach (var group in Groups)
                foreach (var item in group.Items)
                    item.IsSelected = selected;
        }
        finally
        {
            _suppressSelectionCallback = false;
        }

        foreach (var group in Groups)
            group.RefreshTotals();
        RefreshSelectionState();
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("privacy.status.scanning");
        _hasScanned = false;
        HasReport = false;

        foreach (var group in Groups)
            group.Reset();

        // Provider-Scans laufen im Hintergrund (jeder Provider kapselt seine Fehler selbst).
        var enabled = Groups.Where(g => g.IsEnabled).ToList();
        var results = new List<(PrivacyGroup Group, IReadOnlyList<PrivacyItem> Items)>();
        foreach (var group in enabled)
        {
            IReadOnlyList<PrivacyItem> items;
            try { items = await group.Provider.ScanAsync(); }
            catch { items = Array.Empty<PrivacyItem>(); }
            results.Add((group, items));
        }

        // Ergebnisse auf dem UI-Thread anwenden.
        foreach (var (group, items) in results)
        {
            foreach (var item in items)
            {
                item.SelectionChanged = () =>
                {
                    if (_suppressSelectionCallback) return;
                    group.RefreshTotals();
                    RefreshSelectionState();
                };
                group.Items.Add(item);
            }
            group.RefreshTotals();
        }

        _hasScanned = true;
        IsBusy = false;
        RefreshSelectionState();
        StatusText = SelectedCount > 0
            ? Loc.T("privacy.status.doneSelectable")
            : Loc.T("privacy.status.doneEmpty");
    }

    private async Task CleanAsync()
    {
        // Nur Gruppen mit tatsächlich ausgewählten Einträgen.
        var affected = Groups.Where(g => g.SelectedCount > 0).ToList();
        if (affected.Count == 0) return;

        bool needsStrongWarning = affected.Any(g => g.RequiresConfirmation);
        string message = BuildConfirmMessage(affected, needsStrongWarning);

        bool confirmed = ConfirmDialog.Show(Application.Current?.MainWindow, message);
        if (!confirmed) return;

        IsBusy = true;
        HasReport = false;
        StatusText = Loc.T("privacy.status.cleaning");

        // Auswahl je Gruppe festhalten (Snapshot), dann im Hintergrund löschen.
        var snapshot = affected
            .Select(g => (Group: g, Selected: g.Items.Where(i => i.IsSelected).ToList()))
            .ToList();

        var reportLines = new List<string>();
        int totalDeleted = 0;

        foreach (var (group, selected) in snapshot)
        {
            int deleted;
            try { deleted = await group.Provider.CleanAsync(selected); }
            catch { deleted = 0; }

            totalDeleted += deleted;
            reportLines.Add(Loc.T("privacy.report.line", group.Name, deleted));
        }

        // Nach dem Löschen frisch scannen, damit die Liste den echten Zustand zeigt.
        await ScanAsync();

        LastReportText = Loc.T("privacy.report.total", totalDeleted, string.Join(" · ", reportLines));
        HasReport = true;
        StatusText = Loc.T("privacy.status.cleaned");
    }

    /// <summary>Baut den Warntext für den Bestätigungsdialog; bei Cookies o. Ä. besonders deutlich.</summary>
    private static string BuildConfirmMessage(IReadOnlyList<PrivacyGroup> affected, bool strongWarning)
    {
        var sb = new StringBuilder();
        int total = affected.Sum(g => g.SelectedCount);
        sb.Append(Loc.T("privacy.confirm.intro", total));
        foreach (var group in affected)
            sb.Append(Loc.T("privacy.confirm.categoryLine", group.Name, group.SelectedCount));

        if (strongWarning)
            sb.Append(Loc.T("privacy.confirm.cookieWarning"));

        sb.Append(Loc.T("privacy.confirm.proceed"));
        return sb.ToString();
    }

    private void RefreshSelectionState()
    {
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
        foreach (var group in Groups)
            group.RefreshLabels();
        if (!IsBusy && !_hasScanned)
            StatusText = Loc.T("privacy.status.ready");
    }
}
