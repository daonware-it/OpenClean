using OpenClean.Contracts;
using OpenClean.Services.Licensing;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>
/// ViewModel der Lizenz-Seite: zeigt den Lizenzzustand (Free / Pro / abgelaufen), die
/// Lizenzdetails und die freigeschalteten Pro-Funktionen und startet Aktivierung, Kauf
/// und Geräte-Freigabe. Prüfung und Persistenz der Lizenz bleiben im
/// <see cref="LicenseService"/> – hier wird nur angezeigt und angestoßen.
/// </summary>
public sealed class LicensePageViewModel : ViewModelBase
{
    private bool _isBusy;
    private string? _statusKey;

    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;

    public RelayCommand ActivateCommand { get; }
    public RelayCommand BuyCommand { get; }
    public AsyncRelayCommand DeactivateCommand { get; }

    public LicensePageViewModel(IDialogService? dialogs = null, IUiDispatcher? ui = null)
    {
        _dialogs = dialogs ?? DialogService.Default;
        _ui = ui ?? UiDispatcher.Default;

        ActivateCommand = new RelayCommand(_ => Activate(), _ => !IsBusy);
        BuyCommand = new RelayCommand(_ => LicenseViewModel.OpenBuyPage());
        DeactivateCommand = new AsyncRelayCommand(_ => DeactivateAsync(), _ => !IsBusy && HasLicense);

        // Lizenzwechsel (Aktivierung, Hintergrund-Refresh, Freigabe) -> Anzeige neu berechnen.
        PremiumService.Instance.Changed += (_, _) =>
        {
            if (_ui.CheckAccess()) Refresh();
            else _ui.Post(Refresh);
        };
    }

    private static LicenseService Lic => LicenseService.Instance;

    /// <summary>True bei gültiger Lizenz (Pro freigeschaltet).</summary>
    public bool IsPremium => Lic.State == LicenseState.Valid;

    /// <summary>True, wenn ein Schlüssel gespeichert ist (auch abgelaufen) – steuert Details/Freigabe.</summary>
    public bool HasLicense => !string.IsNullOrWhiteSpace(Lic.LicenseKey);

    /// <summary>True, solange Pro nicht aktiv ist – zeigt „Aktivieren"/„Kaufen".</summary>
    public bool ShowPurchase => !IsPremium;

    public string StateTitle => Lic.State switch
    {
        LicenseState.Valid => Loc.T("license.state.active"),
        LicenseState.Expired => Loc.T("license.state.expired"),
        _ => Loc.T("license.state.free")
    };

    public string StateBody => Lic.State switch
    {
        LicenseState.Valid => Loc.T("license.state.active.body"),
        LicenseState.Expired => Loc.T("license.state.expired.body"),
        _ => Loc.T("license.state.free.body")
    };

    /// <summary>Maskierter Schlüssel – nur die letzte Gruppe im Klartext.</summary>
    public string MaskedKey => Mask(Lic.LicenseKey);

    public string Edition => Lic.License?.Edition ?? "—";

    public string ExpiresText => Lic.License is { } info
        ? info.ExpiresUtc.ToLocalTime().ToString("dd.MM.yyyy")
        : "—";

    public string ModuleVersion => PremiumService.Instance.Module?.ModuleVersion ?? "—";

    public bool HasSchedule => Lic.HasFeature(PremiumContract.FeatureSchedule);
    public bool HasBatchUninstall => Lic.HasFeature(PremiumContract.FeatureBatchUninstall);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ActivateCommand.RaiseCanExecuteChanged();
            DeactivateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Zuletzt gesetzter Status-Text, abgeleitet vom gemerkten Loc-Schlüssel (sprachwechsel-fest).</summary>
    public string StatusText => _statusKey is null ? "" : Loc.T(_statusKey);

    /// <summary>Setzt den Status anhand eines Loc-Schlüssels und meldet die Änderung.</summary>
    private void SetStatus(string? locKey)
    {
        _statusKey = locKey;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Öffnet den bestehenden Aktivierungsdialog (Schlüssel-Eingabe + Modul-Download).</summary>
    private void Activate()
    {
        _dialogs.ActivateLicense();
        // Erfolgsfall deckt bereits das Changed-Ereignis ab; das hier fängt den Abbruch ab.
        Refresh();
    }

    /// <summary>
    /// Gibt das Gerät frei: Bestätigung -> Server-Abmeldung -> lokales Entfernen.
    /// Ist der Server nicht erreichbar, wird ausdrücklich nachgefragt, ob die Lizenz
    /// trotzdem nur lokal entfernt werden soll (Geräte-Slot bleibt dann belegt).
    /// </summary>
    private async Task DeactivateAsync()
    {
        bool confirmed = _dialogs.ConfirmThemed(
            Loc.T("license.deactivate.confirm.body"),
            Loc.T("license.deactivate.confirm.title"),
            Loc.T("license.deactivate.confirm.action"));
        if (!confirmed) return;

        IsBusy = true;
        SetStatus("license.deactivate.working");

        var error = await PremiumService.Instance.DeactivateAsync();

        IsBusy = false;

        if (error == LicenseApiError.None)
        {
            SetStatus("license.deactivate.success");
            Refresh();
            return;
        }

        if (error is LicenseApiError.Network or LicenseApiError.RateLimited)
        {
            bool localOnly = _dialogs.ConfirmThemed(
                Loc.T("license.deactivate.offline.body"),
                Loc.T("license.deactivate.offline.title"),
                Loc.T("license.deactivate.confirm.action"));

            if (localOnly)
            {
                PremiumService.Instance.RemoveLocally();
                SetStatus("license.deactivate.success");
                Refresh();
                return;
            }
        }

        SetStatus("license.deactivate.error");
    }

    /// <summary>Zeigt nur die letzte Schlüsselgruppe; der Rest wird maskiert.</summary>
    private static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "—";

        int dash = key.LastIndexOf('-');
        // Regulärer Schlüssel (OPENCLEAN-XXXX-XXXX-XXXX-XXXX): letzte Gruppe im Klartext.
        // Sonderfall ohne (nutzbaren) Bindestrich: nie den ganzen Schlüssel zeigen,
        // sondern höchstens die letzten 4 Zeichen als Klartext-Rest.
        string tail = dash >= 0 && dash < key.Length - 1
            ? key[(dash + 1)..]
            : key.Length > 4 ? key[^4..] : key;
        return $"OPENCLEAN-••••-••••-••••-{tail}";
    }

    /// <summary>Berechnet alle abgeleiteten Anzeigen neu (Lizenz- oder Sprachwechsel).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsPremium));
        OnPropertyChanged(nameof(HasLicense));
        OnPropertyChanged(nameof(ShowPurchase));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateBody));
        OnPropertyChanged(nameof(MaskedKey));
        OnPropertyChanged(nameof(Edition));
        OnPropertyChanged(nameof(ExpiresText));
        OnPropertyChanged(nameof(ModuleVersion));
        OnPropertyChanged(nameof(HasSchedule));
        OnPropertyChanged(nameof(HasBatchUninstall));
        DeactivateCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Aktualisiert nach einem Sprachwechsel alle berechneten Texte. StatusText ist kein
    /// berechneter, sondern ein gespeicherter Text – daher zusätzlich explizit neu melden,
    /// damit er anhand des gemerkten Loc-Schlüssels in der neuen Sprache neu übersetzt wird.
    /// </summary>
    public void Relocalize()
    {
        Refresh();
        OnPropertyChanged(nameof(StatusText));
    }
}
