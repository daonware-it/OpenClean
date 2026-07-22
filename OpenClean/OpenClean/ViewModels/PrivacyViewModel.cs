using System.Collections.ObjectModel;
using System.Text;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;
using OpenClean.Services.Privacy;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>
/// Agent 1: Steuert Scan → Vorschau → Bereinigen für die Privatsphäre-Seite.
/// Es wird nie etwas gelöscht, bevor der Nutzer nach einem Scan explizit bestätigt.
/// Kategorien, die eine Bestätigung erfordern (z. B. Cookies), erhalten eine
/// besonders deutliche Warnung.
/// </summary>
public sealed class PrivacyViewModel : ScanViewModelBase
{
    private readonly PrivacyScannerService _scanner = new();
    private readonly IDialogService _dialogs;

    private bool _hasScanned;
    private bool _suppressSelectionCallback;
    private string _statusText = Loc.T("privacy.status.ready");

    private string _lastReportText = "";
    private bool _hasReport;

    public ObservableCollection<PrivacyGroup> Groups { get; } = new();

    /// <summary>Domains, deren Browser-Cookies bei der Bereinigung geschützt sind (alphabetisch).</summary>
    public ObservableCollection<string> WhitelistDomains { get; } = new();

    /// <summary>
    /// Angezeigtes Cookie-Inventar (aggregiert je Domain) – bereits nach
    /// <see cref="CookieSearch"/> gefiltert. Die ungefilterte Menge steht in <see cref="_allCookies"/>.
    /// </summary>
    public ObservableCollection<CookieInventoryEntry> BrowserCookies { get; } = new();

    /// <summary>Ungefiltertes Ergebnis des letzten Scans (Quelle der Wahrheit für den Filter).</summary>
    private readonly List<CookieInventoryEntry> _allCookies = new();

    private bool _cookiesLoaded;
    private string _cookieHint = "";
    private string _cookieSearch = "";

    /// <summary>
    /// Suchbegriff für das Cookie-Inventar. Filtert live nach Domain ODER Cookie-Name.
    /// </summary>
    public string CookieSearch
    {
        get => _cookieSearch;
        set
        {
            if (SetProperty(ref _cookieSearch, value))
                ApplyCookieFilter();
        }
    }

    /// <summary>Ob überhaupt ein Suchbegriff gesetzt ist (steuert das „Leeren"-Kreuz).</summary>
    public bool HasCookieSearch => !string.IsNullOrWhiteSpace(_cookieSearch);

    /// <summary>Übernimmt <see cref="_allCookies"/> gefiltert in die angezeigte Liste.</summary>
    private void ApplyCookieFilter()
    {
        BrowserCookies.Clear();
        foreach (var entry in _allCookies)
        {
            if (entry.Matches(_cookieSearch))
                BrowserCookies.Add(entry);
        }

        OnPropertyChanged(nameof(HasCookieSearch));
        OnPropertyChanged(nameof(CookieEmptyText));
        OnPropertyChanged(nameof(CookieCountText));
    }

    /// <summary>Zeigt „N von M Domains", solange gefiltert wird; sonst leer.</summary>
    public string CookieCountText =>
        HasCookieSearch && _allCookies.Count > 0
            ? Loc.T("privacy.browserCookies.filtered", BrowserCookies.Count, _allCookies.Count)
            : "";

    /// <summary>
    /// Hinweis, warum das Inventar leer/unvollständig ist (z. B. „Chrome läuft"). Leer,
    /// wenn alle Cookie-DBs gelesen werden konnten.
    /// </summary>
    public string CookieHint
    {
        get => _cookieHint;
        private set
        {
            if (SetProperty(ref _cookieHint, value))
                OnPropertyChanged(nameof(HasCookieHint));
        }
    }

    /// <summary>Ob ein Hinweis zum Cookie-Inventar anzuzeigen ist.</summary>
    public bool HasCookieHint => !string.IsNullOrEmpty(_cookieHint);

    /// <summary>
    /// Text des Leerzustands: vor dem ersten Laden die Aufforderung, danach die Meldung,
    /// dass wirklich keine Cookies gefunden wurden.
    /// </summary>
    public string CookieEmptyText
    {
        get
        {
            if (!_cookiesLoaded) return Loc.T("privacy.browserCookies.empty");
            // Zwischen „gar keine Cookies" und „Suche ohne Treffer" unterscheiden.
            if (_allCookies.Count > 0 && HasCookieSearch)
                return Loc.T("privacy.browserCookies.noMatch", _cookieSearch.Trim());
            return Loc.T("privacy.browserCookies.none");
        }
    }

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand AddWhitelistCommand { get; }
    public RelayCommand RemoveWhitelistCommand { get; }
    public AsyncRelayCommand LoadCookiesCommand { get; }
    public RelayCommand AddCookieToWhitelistCommand { get; }

    private string _newWhitelistDomain = "";
    /// <summary>Eingabefeld für eine neue Whitelist-Domain.</summary>
    public string NewWhitelistDomain
    {
        get => _newWhitelistDomain;
        set
        {
            if (SetProperty(ref _newWhitelistDomain, value))
                AddWhitelistCommand.RaiseCanExecuteChanged();
        }
    }

    public PrivacyViewModel(IDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? DialogService.Default;

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
        AddWhitelistCommand = new RelayCommand(
            _ => AddWhitelist(), _ => !string.IsNullOrWhiteSpace(NewWhitelistDomain));
        RemoveWhitelistCommand = new RelayCommand(param => RemoveWhitelist(param as string));
        LoadCookiesCommand = new AsyncRelayCommand(_ => LoadCookiesAsync(), _ => !IsBusy);
        AddCookieToWhitelistCommand = new RelayCommand(
            param => AddCookieToWhitelist(param as CookieInventoryEntry),
            param => param is CookieInventoryEntry e && e.CanAdd);

        // Command-Verfügbarkeit an den Busy-Zustand koppeln (Gerüst in ScanViewModelBase).
        RegisterBusyCommands(ScanCommand, CleanCommand, SelectAllCommand, DeselectAllCommand, LoadCookiesCommand);

        ReloadWhitelist();
    }

    /// <summary>Zusätzlich zur Command-Neubewertung hängt <see cref="CanChangeSelection"/> am Busy-Zustand.</summary>
    protected override void OnBusyChanged() => OnPropertyChanged(nameof(CanChangeSelection));

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

    // Bei erkannter Manipulation (OPCL-20) gesperrt. Die Privacy-Provider löschen direkt,
    // ohne über einen der abgesicherten Services zu laufen – hier ist das der Choke Point.
    public bool CanClean => _hasScanned && !IsBusy && SelectedCount > 0 && !IntegrityState.IsBlocked;
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
        // Zweite Verteidigungslinie neben CanClean: die Privacy-Provider löschen selbst,
        // ohne über einen der abgesicherten Services zu laufen.
        if (IntegrityState.IsBlocked)
        {
            _dialogs.ShowError(Loc.T("integrity.blocked.action"));
            return;
        }

        // Nur Gruppen mit tatsächlich ausgewählten Einträgen.
        var affected = Groups.Where(g => g.SelectedCount > 0).ToList();
        if (affected.Count == 0) return;

        bool needsStrongWarning = affected.Any(g => g.RequiresConfirmation);
        string message = BuildConfirmMessage(affected, needsStrongWarning);

        bool confirmed = _dialogs.ConfirmThemed(message);
        if (!confirmed) return;

        IsBusy = true;
        HasReport = false;

        // Sicherheitsnetz: Vor dem Löschen echter Nutzerdaten einen Wiederherstellungspunkt anlegen.
        // Bricht der Nutzer am Fehler-Gate ab, wird nichts gelöscht.
        if (!await SafetyPrompt.EnsureRestorePointAsync(_dialogs, s => StatusText = s))
        {
            StatusText = Loc.T("safety.aborted");
            IsBusy = false;
            return;
        }

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

        // Cookies sind dank Schattenkopie sichtbar, LÖSCHEN geht bei laufendem Browser aber
        // nicht. Lieber vorher warnen als hinterher kommentarlos „0 gelöscht" melden.
        if (affected.Any(g => g.Provider is CookiesProvider))
        {
            var locked = CookiesProvider.LockedBrowsers();
            if (locked.Count > 0)
                sb.Append(Loc.T("privacy.confirm.cookiesLocked", string.Join(", ", locked)));
        }

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

    /// <summary>Lädt die geschützten Domains neu aus dem Dienst (alphabetisch sortiert).</summary>
    private void ReloadWhitelist()
    {
        WhitelistDomains.Clear();
        foreach (var d in CookieWhitelistService.Instance.Domains)
            WhitelistDomains.Add(d);
    }

    /// <summary>
    /// Fügt die im Textfeld eingegebene Domain zur Cookie-Whitelist hinzu. Bei Erfolg wird
    /// das Feld geleert, bei ungültiger Eingabe erscheint ein Hinweis.
    /// </summary>
    private void AddWhitelist()
    {
        // Erst hinzufügen (mit dem noch gefüllten Feldwert), dann bei Erfolg leeren.
        if (AddDomainToWhitelist(NewWhitelistDomain))
            NewWhitelistDomain = "";
        else
            StatusText = Loc.T("privacy.whitelist.invalid");
    }

    /// <summary>
    /// Gemeinsame Logik zum Aufnehmen einer Domain in die Cookie-Whitelist. Aktualisiert bei
    /// Erfolg die Liste, entfernt geschützte Vorschau-Einträge und pflegt den Inventar-Status.
    /// </summary>
    /// <returns><c>true</c>, wenn die Domain neu aufgenommen wurde.</returns>
    private bool AddDomainToWhitelist(string? domain)
    {
        bool ok = CookieWhitelistService.Instance.Add(domain);
        if (ok)
        {
            ReloadWhitelist();
            PruneProtectedScannedItems();
            RefreshCookieWhitelistFlags();
            RefreshSelectionState();
        }
        return ok;
    }

    /// <summary>Nimmt bereits gescannte, nun geschützte Cookie-Einträge aus der Vorschau.</summary>
    private void PruneProtectedScannedItems()
    {
        foreach (var group in Groups.Where(g => g.RequiresConfirmation))
        {
            var protectedItems = group.Items
                .Where(i => CookieWhitelistService.Instance.Contains(i.Name))
                .ToList();
            foreach (var item in protectedItems)
                group.Items.Remove(item);
            group.RefreshTotals();
        }
    }

    /// <summary>Entfernt eine Domain aus der Whitelist und lädt die Liste neu.</summary>
    private void RemoveWhitelist(string? domain)
    {
        if (CookieWhitelistService.Instance.Remove(domain))
        {
            ReloadWhitelist();
            RefreshCookieWhitelistFlags();
        }
    }

    /// <summary>Gleicht den Whitelist-Status aller Inventar-Einträge mit dem Dienst ab.</summary>
    private void RefreshCookieWhitelistFlags()
    {
        foreach (var entry in BrowserCookies)
            entry.IsWhitelisted = CookieWhitelistService.Instance.Contains(entry.Domain);
        AddCookieToWhitelistCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Liest read-only das Cookie-Inventar aller Browser und füllt <see cref="BrowserCookies"/>.
    /// Fehler beim Scannen führen zu einer leeren Liste, nie zu einer Ausnahme.
    /// </summary>
    private async Task LoadCookiesAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("privacy.browserCookies.loading");

        CookieInventoryService.CookieScanResult result;
        try { result = await CookieInventoryService.Instance.ScanAsync(); }
        catch { result = CookieInventoryService.CookieScanResult.Empty; }

        _allCookies.Clear();
        _allCookies.AddRange(result.Entries);
        ApplyCookieFilter();
        RefreshCookieWhitelistFlags();

        // Gesperrte DBs (laufender Browser) sind der häufigste Grund für eine leere Liste –
        // das muss der Nutzer sehen, sonst wirkt der Button wie kaputt.
        CookieHint = result.LockedBrowsers.Count > 0
            ? Loc.T("privacy.browserCookies.locked", string.Join(", ", result.LockedBrowsers))
            : "";

        _cookiesLoaded = true;
        OnPropertyChanged(nameof(CookieEmptyText));

        IsBusy = false;
        StatusText = Loc.T("privacy.status.ready");
    }

    /// <summary>Nimmt die Domain eines Inventar-Eintrags in die Whitelist auf.</summary>
    private void AddCookieToWhitelist(CookieInventoryEntry? entry)
    {
        if (entry is null) return;
        AddDomainToWhitelist(entry.Domain);
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel alle berechneten Texte.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CookieEmptyText));
        foreach (var group in Groups)
            group.RefreshLabels();
        if (!IsBusy && !_hasScanned)
            StatusText = Loc.T("privacy.status.ready");
    }
}
