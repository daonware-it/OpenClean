using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>Ein Eintrag des Ort-Filters; <c>null</c> steht für „Alle Orte".</summary>
public sealed class ContextMenuLocationOption : ViewModelBase
{
    public ContextMenuLocationOption(ContextMenuLocation? location) => Location = location;

    public ContextMenuLocation? Location { get; }

    public string Display => Location is null
        ? Loc.T("contextmenu.location.all")
        : ContextMenuEntry.DisplayFor(Location.Value);

    /// <summary>Nach einem Sprachwechsel die Beschriftung neu ziehen (Instanz bleibt erhalten,
    /// damit die ComboBox-Auswahl nicht verloren geht).</summary>
    public void Relocalize() => OnPropertyChanged(nameof(Display));
}

/// <summary>
/// Lädt die Einträge des Windows-Kontextmenüs und erlaubt das reversible Ab-/Anschalten
/// (Muster: <see cref="StartupViewModel"/>).
/// </summary>
public sealed class ContextMenuViewModel : ViewModelBase
{
    private readonly ContextMenuService _service = new();

    // Ladelauf-Zähler: bricht veraltete Icon-Hintergrundläufe ab, sobald neu geladen wird.
    private int _loadGeneration;

    private bool _isBusy;
    private string _statusText = Loc.T("contextmenu.status.loading");
    private StartupStatusFilter _statusFilter = StartupStatusFilter.Alle;
    private ContextMenuLocationOption _locationFilter;

    public ObservableCollection<ContextMenuItemViewModel> Entries { get; } = new();

    /// <summary>Gefilterte Sicht auf <see cref="Entries"/> (an die UI gebunden).</summary>
    public ICollectionView EntriesView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Status-Segmentfilter – dieselben Stufen wie im Autostart-Bereich.</summary>
    public IReadOnlyList<StartupStatusFilter> StatusFilterOptions { get; } =
        new[] { StartupStatusFilter.Alle, StartupStatusFilter.Aktiviert, StartupStatusFilter.Deaktiviert };

    /// <summary>Auswahlliste des Ort-Filters (erster Eintrag = alle Orte).</summary>
    public IReadOnlyList<ContextMenuLocationOption> LocationOptions { get; }

    public ContextMenuViewModel()
    {
        var options = new List<ContextMenuLocationOption> { new(null) };
        options.AddRange(Enum.GetValues<ContextMenuLocation>().Select(l => new ContextMenuLocationOption(l)));
        LocationOptions = options;
        _locationFilter = options[0];

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntry;

        _ = LoadAsync();
    }

    public StartupStatusFilter StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, value))
                EntriesView.Refresh();
        }
    }

    public ContextMenuLocationOption LocationFilter
    {
        get => _locationFilter;
        set
        {
            if (SetProperty(ref _locationFilter, value))
                EntriesView.Refresh();
        }
    }

    private bool FilterEntry(object item)
    {
        if (item is not ContextMenuItemViewModel vm)
            return true;

        if (_locationFilter.Location is { } location && !vm.Locations.Contains(location))
            return false;

        return _statusFilter switch
        {
            StartupStatusFilter.Aktiviert => vm.IsEnabled,
            StartupStatusFilter.Deaktiviert => !vm.IsEnabled,
            _ => true
        };
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task LoadAsync()
    {
        int generation = ++_loadGeneration;
        IsBusy = true;
        StatusText = Loc.T("contextmenu.status.loading");

        var entries = await Task.Run(() => _service.GetEntries());

        foreach (var existing in Entries)
            existing.PropertyChanged -= OnItemPropertyChanged;
        Entries.Clear();

        foreach (var entry in entries.OrderBy(e => e.Kind).ThenBy(e => e.Name))
        {
            var item = new ContextMenuItemViewModel(entry, _service, ReportError);
            item.PropertyChanged += OnItemPropertyChanged;
            Entries.Add(item);
        }

        IsBusy = false;
        StatusText = Entries.Count == 0
            ? Loc.T("contextmenu.status.none")
            : Loc.T("contextmenu.status.summary", Entries.Count, Entries.Count(e => e.IsEnabled));

        // Icons der Handler-Dateien im Hintergrund nachtragen; bis dahin Buchstaben-Avatar.
        _ = LoadIconsAsync(generation);
    }

    /// <summary>
    /// Extrahiert die Icons der Handler-Dateien parallel im Hintergrund und trägt sie per
    /// Dispatcher live nach. Bricht ab, sobald ein neuer Ladevorgang startet.
    /// </summary>
    private async Task LoadIconsAsync(int generation)
    {
        var pending = Entries.Where(e => !string.IsNullOrWhiteSpace(e.IconPath)).ToList();
        if (pending.Count == 0) return;

        var dispatcher = Application.Current?.Dispatcher;

        await Task.Run(() => Parallel.ForEach(pending,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            (item, state) =>
            {
                if (generation != _loadGeneration) { state.Stop(); return; }
                var icon = AppIconService.GetIcon(item.IconPath);
                if (icon is null || generation != _loadGeneration) return;

                dispatcher?.InvokeAsync(() =>
                {
                    if (generation == _loadGeneration)
                        item.SetIcon(icon);
                });
            }));
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel Statuszeile, Filter und alle Einträge.</summary>
    public void Relocalize()
    {
        foreach (var item in Entries)
            item.Relocalize();

        foreach (var option in LocationOptions)
            option.Relocalize();

        if (!IsBusy)
        {
            StatusText = Entries.Count == 0
                ? Loc.T("contextmenu.status.none")
                : Loc.T("contextmenu.status.summary", Entries.Count, Entries.Count(e => e.IsEnabled));
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuItemViewModel.IsEnabled))
            EntriesView.Refresh();
    }

    private static void ReportError(string message)
        => MessageBox.Show(message, "OpenClean", MessageBoxButton.OK, MessageBoxImage.Warning);
}
