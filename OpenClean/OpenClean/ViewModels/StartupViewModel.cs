using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using OpenClean.Services;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>Statusfilter für die Autostart-Liste.</summary>
public enum StartupStatusFilter
{
    Alle,
    Aktiviert,
    Deaktiviert
}

/// <summary>Eine wählbare Verzögerungsstufe der Autostart-Liste.</summary>
public sealed class DelayOption : ViewModelBase
{
    public DelayOption(int seconds) => Seconds = seconds;

    /// <summary>Verzögerung in Sekunden (0 = keine).</summary>
    public int Seconds { get; }

    /// <summary>Lokalisierte Beschriftung („Keine", „30 Sek.", „2 Min." …).</summary>
    public string Display => DelayedStartupService.DisplayFor(Seconds);

    /// <summary>Nach einem Sprachwechsel die Beschriftung neu ziehen (Instanz bleibt erhalten,
    /// damit die ComboBox-Auswahl nicht verloren geht).</summary>
    public void Relocalize() => OnPropertyChanged(nameof(Display));
}

/// <summary>
/// Agent 3: Lädt Autostart-Einträge, erlaubt das Umschalten des Status und das verzögerte
/// Starten einzelner Programme (siehe <see cref="DelayedStartupService"/>).
/// </summary>
public sealed class StartupViewModel : ViewModelBase
{
    private readonly StartupService _service = new();
    private readonly DelayedStartupService _delayService;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;

    // Ladelauf-Zähler: bricht veraltete Icon-Hintergrundläufe ab, sobald neu geladen wird.
    private int _loadGeneration;

    private bool _isBusy;
    private string _statusText = Loc.T("startup.status.loading");
    private StartupStatusFilter _statusFilter = StartupStatusFilter.Alle;

    public ObservableCollection<StartupItemViewModel> Entries { get; } = new();

    /// <summary>Gefilterte Sicht auf <see cref="Entries"/> (an die UI gebunden).</summary>
    public ICollectionView EntriesView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Verteilt die aktivierten Einträge automatisch auf gestaffelte Verzögerungen.</summary>
    public AsyncRelayCommand StaggerCommand { get; }

    /// <summary>Hebt alle eingerichteten Verzögerungen wieder auf.</summary>
    public AsyncRelayCommand ClearDelaysCommand { get; }

    public IReadOnlyList<StartupStatusFilter> StatusFilterOptions { get; } =
        new[] { StartupStatusFilter.Alle, StartupStatusFilter.Aktiviert, StartupStatusFilter.Deaktiviert };

    /// <summary>Auswahlliste der Verzögerungsstufen (an die ComboBox jeder Zeile gebunden).</summary>
    public IReadOnlyList<DelayOption> DelayOptions { get; } =
        DelayedStartupService.DelayOptions.Select(s => new DelayOption(s)).ToList();

    public StartupViewModel(IDialogService? dialogs = null, IUiDispatcher? ui = null)
    {
        _dialogs = dialogs ?? DialogService.Default;
        _ui = ui ?? UiDispatcher.Default;
        _delayService = new DelayedStartupService(_service);

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
        StaggerCommand = new AsyncRelayCommand(_ => StaggerAsync());
        ClearDelaysCommand = new AsyncRelayCommand(_ => ClearDelaysAsync());

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntry;

        _ = LoadAsync();
    }

    /// <summary>True, wenn mindestens ein Eintrag verzögert gestartet wird (blendet „Alle aufheben" ein).</summary>
    public bool HasDelays => Entries.Any(e => e.DelaySeconds > 0);

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
        int generation = ++_loadGeneration;
        IsBusy = true;
        StatusText = Loc.T("startup.status.loading");

        var entries = await Task.Run(() =>
        {
            var list = _service.GetEntries();
            // Aufgaben, die der Nutzer außerhalb von OpenClean gelöscht hat, dürfen nicht als
            // Verzögerung weiterleben – sonst zeigt die Liste einen Zustand, den es nicht gibt.
            _delayService.Reconcile(list);
            return list;
        });

        foreach (var existing in Entries)
            existing.PropertyChanged -= OnItemPropertyChanged;
        Entries.Clear();

        foreach (var entry in entries.OrderByDescending(e => e.Impact).ThenBy(e => e.Name))
        {
            var item = new StartupItemViewModel(entry, _service, _delayService, ReportError);
            item.PropertyChanged += OnItemPropertyChanged;
            Entries.Add(item);
        }

        IsBusy = false;
        StatusText = Entries.Count == 0
            ? Loc.T("startup.status.none")
            : Loc.T("startup.status.summary", Entries.Count, Entries.Count(e => e.IsEnabled));
        OnPropertyChanged(nameof(HasDelays));

        // Programm-Icons im Hintergrund aus der EXE extrahieren und live nachtragen
        // (AppIconService-Cache macht erneutes Laden sofort). Bis dahin Buchstaben-Avatar.
        _ = LoadIconsAsync(generation);
    }

    /// <summary>
    /// Extrahiert die Programm-Icons parallel im Hintergrund (eingefroren, thread-sicher
    /// übergebbar) und trägt sie per Dispatcher live nach. Bricht ab, sobald ein neuer
    /// Ladevorgang startet.
    /// </summary>
    private async Task LoadIconsAsync(int generation)
    {
        var pending = Entries.Where(e => !string.IsNullOrWhiteSpace(e.IconPath)).ToList();
        if (pending.Count == 0) return;

        await Task.Run(() => Parallel.ForEach(pending,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            (item, state) =>
            {
                if (generation != _loadGeneration) { state.Stop(); return; }
                var icon = AppIconService.GetIcon(item.IconPath);
                if (icon is null || generation != _loadGeneration) return;

                _ui.Post(() =>
                {
                    if (generation == _loadGeneration)
                        item.SetIcon(icon);
                });
            }));
    }

    // ---- Verzögerungen (Staffeln / Aufheben) --------------------------------

    /// <summary>
    /// Verteilt die aktivierten, verzögerbaren Einträge auf gestaffelte Startzeiten: der
    /// Eintrag mit der höchsten Startbelastung zuerst, danach jeweils die nächste Stufe.
    /// Ab der letzten Stufe (10 Min.) bleibt es bei dieser.
    /// </summary>
    private async Task StaggerAsync()
    {
        var targets = Entries
            .Where(e => e.IsEnabled && e.CanDelay)
            .OrderByDescending(e => e.Impact)
            .ThenBy(e => e.Name)
            .Select(e => e.Entry)
            .ToList();

        if (targets.Count == 0)
        {
            ReportError(Loc.T("startup.stagger.nothing"));
            return;
        }

        if (!_dialogs.AskYesNo(Loc.T("startup.stagger.confirm", targets.Count), "OpenClean")) return;

        // Stufen ohne die 0 – jeder weitere Eintrag landet auf der höchsten Stufe.
        int[] ladder = DelayedStartupService.DelayOptions.Where(s => s > 0).ToArray();

        IsBusy = true;
        var failed = await Task.Run(() =>
        {
            var errors = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                int delay = ladder[Math.Min(i, ladder.Length - 1)];
                try { _delayService.Apply(targets[i], delay); }
                catch (Exception ex) { errors.Add(Loc.T("startup.delay.error", targets[i].Name, ex.Message)); }
            }
            return errors;
        });
        IsBusy = false;

        if (failed.Count > 0)
            ReportError(string.Join(Environment.NewLine, failed));

        await LoadAsync();
    }

    /// <summary>Hebt alle Verzögerungen auf und aktiviert die Original-Einträge wieder.</summary>
    private async Task ClearDelaysAsync()
    {
        var known = Entries.Select(e => e.Entry).ToList();

        IsBusy = true;
        await Task.Run(() => _delayService.RemoveAll(known));
        IsBusy = false;

        await LoadAsync();
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel Statuszeile und alle Einträge.</summary>
    public void Relocalize()
    {
        foreach (var item in Entries)
            item.Relocalize();

        foreach (var option in DelayOptions)
            option.Relocalize();

        if (!IsBusy)
        {
            StatusText = Entries.Count == 0
                ? Loc.T("startup.status.none")
                : Loc.T("startup.status.summary", Entries.Count, Entries.Count(e => e.IsEnabled));
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupItemViewModel.IsEnabled))
            EntriesView.Refresh();

        if (e.PropertyName is nameof(StartupItemViewModel.IsEnabled)
            or nameof(StartupItemViewModel.DelaySeconds))
            OnPropertyChanged(nameof(HasDelays));
    }

    private void ReportError(string message)
        => _dialogs.ShowError(message);
}
