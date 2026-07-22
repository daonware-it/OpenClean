using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using OpenClean.Services;
using OpenClean.Services.Network;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>Sortierung der Verbindungsliste.</summary>
public enum NetworkSort
{
    Prozess,
    Ziel,
    Status
}

/// <summary>
/// Netzwerk-Transparenz (lokal): zeigt in Echtzeit, welche Programme Verbindungen nach
/// außen halten. Ausschließlich lesend – es wird nichts blockiert, beendet oder verändert.
///
/// Ressourcenschonend: die Abfrage läuft nur, solange die Ansicht sichtbar ist
/// (<see cref="Start"/>/<see cref="Stop"/>), im Zwei-Sekunden-Takt, und die Liste wird
/// inkrementell abgeglichen statt neu aufgebaut.
/// </summary>
public sealed class NetworkViewModel : ViewModelBase
{
    /// <summary>Abfragetakt – Kompromiss aus „Echtzeit" und Systemlast.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly NetworkConnectionService _service = NetworkConnectionService.Instance;
    private readonly DispatcherTimer _timer;
    private readonly IUiDispatcher _ui;

    private bool _isRunning;
    private bool _isRefreshing;

    /// <summary>Win32-Fehlercode der letzten Abfrage (0 = alles gut); steuert die Statusmeldung.</summary>
    private uint _lastError;

    private string _searchText = "";
    private bool _externalOnly = true;
    private bool _resolveHostNames;
    private NetworkSort _sort = NetworkSort.Prozess;
    private string _statusText = Loc.T("network.status.loading");

    public ObservableCollection<NetworkConnectionViewModel> Connections { get; } = new();

    /// <summary>Gefilterte und sortierte Sicht auf <see cref="Connections"/> (an die UI gebunden).</summary>
    public ICollectionView ConnectionsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand SortByProcessCommand { get; }
    public RelayCommand SortByRemoteCommand { get; }
    public RelayCommand SortByStateCommand { get; }

    public NetworkViewModel(IUiDispatcher? ui = null)
    {
        _ui = ui ?? UiDispatcher.Default;

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        SortByProcessCommand = new RelayCommand(_ => Sort = NetworkSort.Prozess);
        SortByRemoteCommand = new RelayCommand(_ => Sort = NetworkSort.Ziel);
        SortByStateCommand = new RelayCommand(_ => Sort = NetworkSort.Status);

        ConnectionsView = CollectionViewSource.GetDefaultView(Connections);
        ConnectionsView.Filter = FilterConnection;

        // Live-Sortierung: eine Zeile, deren Zustand sich ändert oder deren Hostname
        // nachträglich eintrifft, ordnet sich sanft neu ein, statt die Ansicht neu aufzubauen.
        if (ConnectionsView is ICollectionViewLiveShaping live)
        {
            live.IsLiveSorting = true;
            live.LiveSortingProperties.Add(nameof(NetworkConnectionViewModel.ProcessSortKey));
            live.LiveSortingProperties.Add(nameof(NetworkConnectionViewModel.StateDisplay));
        }

        ApplySort();

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    // ---- Lebenszyklus -------------------------------------------------------

    /// <summary>
    /// Startet die Live-Abfrage. Wird beim Anzeigen der Ansicht aufgerufen – außerhalb
    /// läuft bewusst nichts, damit im Hintergrund keine Last entsteht.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _timer.Start();
        _ = RefreshAsync(); // nicht erst nach 2 s etwas zeigen
    }

    /// <summary>Stoppt die Live-Abfrage (Ansicht verlassen/Fenster geschlossen).</summary>
    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer.Stop();
    }

    // ---- Eigenschaften ------------------------------------------------------

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ConnectionsView.Refresh();
        }
    }

    /// <summary>True = nur Verbindungen nach außen; False = auch Loopback/Lauschsockets.</summary>
    public bool ExternalOnly
    {
        get => _externalOnly;
        set
        {
            if (SetProperty(ref _externalOnly, value))
            {
                ConnectionsView.Refresh();
                UpdateStatus();
            }
        }
    }

    /// <summary>
    /// Optionale Rückwärtsauflösung der Zieladressen. Standardmäßig aus: sie erzeugt
    /// zusätzliche DNS-Anfragen. Läuft ausschließlich asynchron und gecacht.
    /// </summary>
    public bool ResolveHostNames
    {
        get => _resolveHostNames;
        set
        {
            if (SetProperty(ref _resolveHostNames, value) && value)
                ResolveHosts();
        }
    }

    public NetworkSort Sort
    {
        get => _sort;
        private set
        {
            if (SetProperty(ref _sort, value))
            {
                OnPropertyChanged(nameof(IsSortProcess));
                OnPropertyChanged(nameof(IsSortRemote));
                OnPropertyChanged(nameof(IsSortState));
                ApplySort();
            }
        }
    }

    public bool IsSortProcess => _sort == NetworkSort.Prozess;
    public bool IsSortRemote => _sort == NetworkSort.Ziel;
    public bool IsSortState => _sort == NetworkSort.Status;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ---- Abfrage ------------------------------------------------------------

    private async Task RefreshAsync()
    {
        // Ein noch laufender Durchgang darf sich nicht mit dem nächsten Takt überlappen.
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            var fresh = await Task.Run(() => _service.GetConnections());
            _lastError = _service.LastError;
            Merge(fresh);
            UpdateStatus();
            LoadIcons();
            if (_resolveHostNames) ResolveHosts();
        }
        catch (Exception ex)
        {
            // Eine Ansicht, die nur zusieht, darf an einem Win32-Fehler nicht sterben.
            StatusText = Loc.T("network.status.error", ex.Message);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Gleicht die vorhandenen Zeilen mit der neuen Momentaufnahme ab: bestehende werden
    /// aktualisiert, neue ergänzt, verschwundene entfernt. Kein Clear/Neuaufbau – sonst
    /// flackerte die Liste im Zwei-Sekunden-Takt und die Auswahl ginge verloren.
    /// </summary>
    private void Merge(IReadOnlyList<NetworkConnection> fresh)
    {
        var existing = new Dictionary<string, NetworkConnectionViewModel>(Connections.Count);
        foreach (var vm in Connections) existing[vm.Key] = vm;

        var seen = new HashSet<string>(fresh.Count);

        foreach (var connection in fresh)
        {
            if (!seen.Add(connection.Key)) continue; // identische Zeile doppelt gemeldet

            if (existing.TryGetValue(connection.Key, out var vm))
                vm.Update(connection);
            else
                Connections.Add(new NetworkConnectionViewModel(connection));
        }

        for (int i = Connections.Count - 1; i >= 0; i--)
            if (!seen.Contains(Connections[i].Key))
                Connections.RemoveAt(i);
    }

    private void UpdateStatus()
    {
        var visible = Connections.Where(c => !_externalOnly || !c.IsLocalOnly).ToList();
        if (visible.Count == 0)
        {
            // Leeres Ergebnis UND ein Win32-Fehler heißt nicht „keine Verbindungen“, sondern
            // „unbekannt“. Gerade bei einer Transparenz-Ansicht wäre die Meldung sonst die
            // glatte Umkehrung der Wahrheit: Der Nutzer schlösse daraus, sein Rechner
            // telefoniere nicht nach außen, obwohl wir es nur nicht lesen konnten.
            // Bei vorhandenen Zeilen bleibt es bei der Zusammenfassung – dann hat lediglich
            // eine einzelne Tabelle gefehlt (z. B. IPv6 deaktiviert), und das ist harmlos.
            StatusText = _lastError != 0
                ? Loc.T("network.status.error", new Win32Exception((int)_lastError).Message)
                : Loc.T("network.status.none");
            return;
        }

        int processes = visible.Select(c => c.ProcessId).Distinct().Count();
        int active = visible.Count(c => c.IsActive);
        StatusText = Loc.T("network.status.summary", visible.Count, processes, active);
    }

    /// <summary>
    /// Zieht die Programm-Icons für neue Zeilen im Hintergrund nach (AppIconService cacht,
    /// wiederholte Läufe sind daher billig). Bis dahin steht ein Buchstaben-Avatar.
    /// </summary>
    private void LoadIcons()
    {
        var pending = Connections
            .Where(c => !c.HasIcon && !string.IsNullOrWhiteSpace(c.IconPath))
            .ToList();
        if (pending.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var item in pending)
            {
                if (!_isRunning) return; // Ansicht verlassen – nicht weiter arbeiten
                var icon = AppIconService.GetIcon(item.IconPath);
                if (icon is null) continue;
                _ui.Post(() => item.SetIcon(icon));
            }
        });
    }

    /// <summary>
    /// Fragt Hostnamen für die Gegenstellen an – nie blockierend: bereits Bekanntes kommt
    /// aus dem Cache, alles andere wird im Hintergrund aufgelöst und nachgetragen.
    /// </summary>
    private void ResolveHosts()
    {
        var cache = ReverseDnsCache.Instance;

        foreach (var item in Connections.ToList())
        {
            var remote = item.Model.Remote;
            if (remote is null || item.HasHostName || item.IsLocalOnly) continue;

            string? known = cache.TryGet(remote.Address);
            if (known is not null)
            {
                item.SetHostName(known);
                continue;
            }

            var target = item;
            cache.Request(remote.Address, host =>
            {
                if (_ui.CheckAccess()) target.SetHostName(host);
                else _ui.Post(() => target.SetHostName(host));
            });
        }
    }

    // ---- Filter / Sortierung ------------------------------------------------

    private bool FilterConnection(object item)
    {
        if (item is not NetworkConnectionViewModel vm) return true;

        return NetworkFilter.Matches(
            vm.ProcessDisplay, vm.PathDisplay, vm.RemoteDisplay, vm.HostName,
            vm.IsLocalOnly, _searchText, _externalOnly);
    }

    private void ApplySort()
    {
        ConnectionsView.SortDescriptions.Clear();

        switch (_sort)
        {
            case NetworkSort.Ziel:
                ConnectionsView.SortDescriptions.Add(new SortDescription(
                    nameof(NetworkConnectionViewModel.RemoteSortKey), ListSortDirection.Ascending));
                break;
            case NetworkSort.Status:
                ConnectionsView.SortDescriptions.Add(new SortDescription(
                    nameof(NetworkConnectionViewModel.StateDisplay), ListSortDirection.Ascending));
                break;
        }

        // Prozess immer als (sekundärer) Schlüssel: sonst springen gleichwertige Zeilen
        // bei jedem Abfragezyklus umher.
        ConnectionsView.SortDescriptions.Add(new SortDescription(
            nameof(NetworkConnectionViewModel.ProcessSortKey), ListSortDirection.Ascending));
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel Statuszeile und alle Zeilen.</summary>
    public void Relocalize()
    {
        foreach (var item in Connections) item.Relocalize();
        UpdateStatus();
    }
}
