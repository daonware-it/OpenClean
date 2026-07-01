using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>Statusfilter für die Autostart-Liste.</summary>
public enum StartupStatusFilter
{
    Alle,
    Aktiviert,
    Deaktiviert
}

/// <summary>
/// Agent 3: Lädt Autostart-Einträge und erlaubt das Umschalten des Status.
/// </summary>
public sealed class StartupViewModel : ViewModelBase
{
    private readonly StartupService _service = new();
    private bool _isBusy;
    private string _statusText = "Autostart-Programme werden geladen …";
    private StartupStatusFilter _statusFilter = StartupStatusFilter.Alle;

    public ObservableCollection<StartupItemViewModel> Entries { get; } = new();

    /// <summary>Gefilterte Sicht auf <see cref="Entries"/> (an die UI gebunden).</summary>
    public ICollectionView EntriesView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public IReadOnlyList<StartupStatusFilter> StatusFilterOptions { get; } =
        new[] { StartupStatusFilter.Alle, StartupStatusFilter.Aktiviert, StartupStatusFilter.Deaktiviert };

    public StartupViewModel()
    {
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

    private bool FilterEntry(object item)
    {
        if (item is not StartupItemViewModel vm)
            return true;

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
        IsBusy = true;
        StatusText = "Autostart-Programme werden geladen …";

        var entries = await Task.Run(() => _service.GetEntries());

        foreach (var existing in Entries)
            existing.PropertyChanged -= OnItemPropertyChanged;
        Entries.Clear();

        foreach (var entry in entries.OrderByDescending(e => e.Impact).ThenBy(e => e.Name))
        {
            var item = new StartupItemViewModel(entry, _service, ReportError);
            item.PropertyChanged += OnItemPropertyChanged;
            Entries.Add(item);
        }

        IsBusy = false;
        StatusText = Entries.Count == 0
            ? "Keine Autostart-Einträge gefunden."
            : $"{Entries.Count} Autostart-Programm(e). {Entries.Count(e => e.IsEnabled)} aktiviert.";
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupItemViewModel.IsEnabled))
            EntriesView.Refresh();
    }

    private static void ReportError(string message)
        => MessageBox.Show(message, "OpenClean", MessageBoxButton.OK, MessageBoxImage.Warning);
}
