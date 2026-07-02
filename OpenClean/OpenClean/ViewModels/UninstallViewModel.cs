using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>Sortierung der Programmliste.</summary>
public enum UninstallSort { Groesse, Name }

/// <summary>
/// Bereich „Deinstallation" (v0.9.0): listet installierte Programme mit Größe, erlaubt
/// einzelne und mehrfache (Batch-)Deinstallation und findet – nach dem Entfernen –
/// zurückgebliebene Ordner/Registry-Reste zur gründlichen Bereinigung.
/// </summary>
public sealed class UninstallViewModel : ViewModelBase
{
    private readonly InstalledAppsService _inventory = new();
    private readonly UninstallerService _uninstaller = new();
    private readonly LeftoverScannerService _leftoverScanner = new();

    private bool _isBusy;
    private string _statusText = Loc.T("uninstall.status.loading");
    private string _searchText = "";
    private UninstallSort _sort = UninstallSort.Groesse;
    private double _progressPercent;
    private bool _progressIsIndeterminate = true;
    private string _progressText = "";

    private string _leftoverAppName = "";
    private int _loadGeneration;

    public ObservableCollection<UninstallItemViewModel> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public ObservableCollection<LeftoverItem> Leftovers { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand UninstallSelectedCommand { get; }
    public AsyncRelayCommand RemoveLeftoversCommand { get; }
    public RelayCommand IgnoreLeftoversCommand { get; }

    public UninstallViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        UninstallSelectedCommand = new AsyncRelayCommand(_ => UninstallSelectedAsync(),
            _ => !IsBusy && SelectedCount > 0);
        RemoveLeftoversCommand = new AsyncRelayCommand(_ => RemoveLeftoversAsync(),
            _ => !IsBusy && Leftovers.Any(l => l.IsSelected));
        IgnoreLeftoversCommand = new RelayCommand(_ => ClearLeftovers());

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;

        // Live-Sortierung: nachträglich berechnete Größen positionieren die Zeile sanft neu,
        // ohne die ganze Ansicht neu aufzubauen (verhindert „Aufblitzen"/Verschwinden).
        if (AppsView is ICollectionViewLiveShaping live)
        {
            live.IsLiveSorting = true;
            live.LiveSortingProperties.Add(nameof(UninstallItemViewModel.SizeBytes));
            live.LiveSortingProperties.Add(nameof(UninstallItemViewModel.Name));
        }

        ApplySort();

        _ = LoadAsync();
    }

    // ---- Eigenschaften ------------------------------------------------------

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                UninstallSelectedCommand.RaiseCanExecuteChanged();
                RemoveLeftoversCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                AppsView.Refresh();
        }
    }

    public UninstallSort Sort
    {
        get => _sort;
        set
        {
            if (SetProperty(ref _sort, value))
                ApplySort();
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool ProgressIsIndeterminate
    {
        get => _progressIsIndeterminate;
        private set => SetProperty(ref _progressIsIndeterminate, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public int SelectedCount => Apps.Count(a => a.IsSelected);
    public long TotalSelectedBytes => Apps.Where(a => a.IsSelected).Sum(a => a.SizeBytes);

    public string SelectionSummary =>
        Loc.T("uninstall.selection.summary", SelectedCount, ByteFormatter.Format(TotalSelectedBytes));

    public bool HasLeftovers => Leftovers.Count > 0;

    public string LeftoverHeading =>
        Loc.T("uninstall.leftover.heading", _leftoverAppName);

    // ---- Laden --------------------------------------------------------------

    public async Task LoadAsync()
    {
        int generation = ++_loadGeneration; // laufende Größenberechnung eines früheren Laufs entwerten
        IsBusy = true;
        ProgressIsIndeterminate = true;
        StatusText = Loc.T("uninstall.status.loading");

        var apps = await Task.Run(() => _inventory.GetInstalledApps());

        Apps.Clear();
        foreach (var app in apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            Apps.Add(new UninstallItemViewModel(app, UninstallOneAsync, OnSelectionChanged));

        ApplySort();
        IsBusy = false;
        RefreshSelection();
        StatusText = Apps.Count == 0
            ? Loc.T("uninstall.status.none")
            : Loc.T("uninstall.status.summary", Apps.Count);

        // Fehlende Größen (z. B. Steam-Spiele ohne EstimatedSize) im Hintergrund aus dem
        // Installationsordner nachberechnen und live nachtragen.
        _ = ComputeMissingSizesAsync(generation);
    }

    /// <summary>
    /// Berechnet die Größe für Programme ohne Registry-Größe aus ihrem Installationsordner
    /// und trägt sie live nach. Bricht ab, sobald ein neuer Ladevorgang gestartet wurde.
    /// </summary>
    private async Task ComputeMissingSizesAsync(int generation)
    {
        var pending = Apps.Where(a => a.NeedsSizeCalculation).ToList();
        if (pending.Count == 0) return;

        var dispatcher = Application.Current?.Dispatcher;

        // Mehrere Ordner gleichzeitig vermessen (I/O-lastig) – sonst dauert es bei vielen
        // Spielen sehr lange, bis alle Größen erscheinen. UI-Updates über den Dispatcher.
        await Task.Run(() => Parallel.ForEach(pending,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            (item, state) =>
            {
                if (generation != _loadGeneration) { state.Stop(); return; } // neuer Load -> abbrechen
                long size = InstalledAppsService.TryGetFolderSize(item.SizeFolder);
                if (size <= 0 || generation != _loadGeneration) return;

                dispatcher?.InvokeAsync(() =>
                {
                    if (generation != _loadGeneration) return;
                    // Live-Sortierung ordnet die Zeile automatisch neu ein (kein harter Refresh).
                    item.SetComputedSize(size);
                    RefreshSelection();
                });
            }));
    }

    private bool FilterApp(object item)
    {
        if (item is not UninstallItemViewModel vm) return true;
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        string q = _searchText.Trim();
        return vm.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               vm.PublisherDisplay.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ApplySort()
    {
        AppsView.SortDescriptions.Clear();
        if (_sort == UninstallSort.Groesse)
            AppsView.SortDescriptions.Add(new SortDescription(nameof(UninstallItemViewModel.SizeBytes),
                ListSortDirection.Descending));
        // Name immer als sekundärer Schlüssel: stabile Reihenfolge bei gleicher Größe
        // (sonst „springen" Einträge ohne Größe bei jedem Live-Sort-Update).
        AppsView.SortDescriptions.Add(new SortDescription(nameof(UninstallItemViewModel.Name),
            ListSortDirection.Ascending));
    }

    // ---- Deinstallation -----------------------------------------------------

    private async Task UninstallOneAsync(UninstallItemViewModel item)
    {
        if (IsBusy) return;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("uninstall.confirm.single", item.Name),
            Loc.T("uninstall.confirm.title"),
            Loc.T("uninstall.action.uninstall"));
        if (!confirmed) return;

        IsBusy = true;
        ProgressIsIndeterminate = true;
        StatusText = Loc.T("uninstall.status.uninstalling", item.Name);

        var app = item.Model;
        bool ok = await _uninstaller.UninstallAsync(app, silent: false);

        // Reste des (versuchten) Deinstallats suchen.
        await ScanLeftoversAsync(new[] { app }, app.Name);

        await LoadAsync();

        StatusText = ok
            ? Loc.T("uninstall.status.doneSingle", app.Name)
            : Loc.T("uninstall.status.failedSingle", app.Name);
    }

    private async Task UninstallSelectedAsync()
    {
        var selected = Apps.Where(a => a.IsSelected && a.CanUninstall).Select(a => a.Model).ToList();
        if (selected.Count == 0) return;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("uninstall.confirm.batch", selected.Count),
            Loc.T("uninstall.confirm.title"),
            Loc.T("uninstall.action.uninstallSelected"));
        if (!confirmed) return;

        IsBusy = true;
        ProgressIsIndeterminate = false;
        ProgressPercent = 0;

        int done = 0;
        foreach (var app in selected)
        {
            StatusText = Loc.T("uninstall.status.uninstallingBatch", done + 1, selected.Count, app.Name);
            ProgressText = StatusText;
            await _uninstaller.UninstallAsync(app, silent: true);
            done++;
            ProgressPercent = done * 100.0 / selected.Count;
        }

        // Reste aller entfernten Programme sammeln.
        await ScanLeftoversAsync(selected, Loc.T("uninstall.leftover.multiple", selected.Count));

        ProgressText = "";
        await LoadAsync();
        StatusText = Loc.T("uninstall.status.doneBatch", selected.Count);
    }

    // ---- Reste --------------------------------------------------------------

    private async Task ScanLeftoversAsync(IReadOnlyList<InstalledApp> apps, string headingName)
    {
        var found = await Task.Run(() =>
        {
            var map = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps)
                foreach (var item in _leftoverScanner.Scan(app))
                    map.TryAdd(item.Path, item);
            return map.Values.ToList();
        });

        Leftovers.Clear();
        foreach (var item in found.OrderByDescending(i => i.SizeBytes))
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LeftoverItem.IsSelected))
                    RemoveLeftoversCommand.RaiseCanExecuteChanged();
            };
            Leftovers.Add(item);
        }

        _leftoverAppName = headingName;
        OnPropertyChanged(nameof(HasLeftovers));
        OnPropertyChanged(nameof(LeftoverHeading));
        RemoveLeftoversCommand.RaiseCanExecuteChanged();
    }

    private async Task RemoveLeftoversAsync()
    {
        var selected = Leftovers.Where(l => l.IsSelected).ToList();
        if (selected.Count == 0) return;

        long bytes = selected.Where(l => !l.IsRegistry).Sum(l => l.SizeBytes);
        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("uninstall.leftover.confirm", selected.Count, ByteFormatter.Format(bytes)),
            Loc.T("uninstall.leftover.confirmTitle"),
            Loc.T("uninstall.leftover.action"));
        if (!confirmed) return;

        IsBusy = true;
        ProgressIsIndeterminate = true;
        StatusText = Loc.T("uninstall.leftover.removing");

        var result = await Task.Run(() => _leftoverScanner.DeleteSelected(selected));

        ClearLeftovers();
        IsBusy = false;
        StatusText = Loc.T("uninstall.leftover.done", result.DeletedCount, ByteFormatter.Format(result.FreedBytes));
    }

    private void ClearLeftovers()
    {
        Leftovers.Clear();
        _leftoverAppName = "";
        OnPropertyChanged(nameof(HasLeftovers));
        OnPropertyChanged(nameof(LeftoverHeading));
        RemoveLeftoversCommand.RaiseCanExecuteChanged();
    }

    // ---- Auswahl / Lokalisierung -------------------------------------------

    private void OnSelectionChanged() => RefreshSelection();

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalSelectedBytes));
        OnPropertyChanged(nameof(SelectionSummary));
        UninstallSelectedCommand.RaiseCanExecuteChanged();
    }

    public void Relocalize()
    {
        foreach (var item in Apps)
            item.Relocalize();
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(LeftoverHeading));
        if (!IsBusy)
        {
            StatusText = Apps.Count == 0
                ? Loc.T("uninstall.status.none")
                : Loc.T("uninstall.status.summary", Apps.Count);
        }
    }
}
