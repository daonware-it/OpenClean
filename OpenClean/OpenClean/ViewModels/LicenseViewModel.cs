using System.Diagnostics;
using OpenClean.Services.Licensing;

namespace OpenClean.ViewModels;

/// <summary>
/// ViewModel des Aktivierungsdialogs: nimmt den Lizenzschlüssel entgegen, führt die
/// Aktivierung (inkl. Modul-Download) über <see cref="PremiumService"/> aus und meldet
/// Erfolg/Fehler lokalisiert zurück.
/// </summary>
public sealed class LicenseViewModel : ViewModelBase
{
    private string _licenseKeyInput = "";
    private string _statusText = "";
    private bool _isBusy;
    private bool _succeeded;

    public AsyncRelayCommand ActivateCommand { get; }
    public RelayCommand BuyCommand { get; }

    /// <summary>Wird bei erfolgreicher Aktivierung ausgelöst (Dialog schließt sich).</summary>
    public event EventHandler? Succeeded;

    public LicenseViewModel()
    {
        ActivateCommand = new AsyncRelayCommand(_ => ActivateAsync(),
            _ => !IsBusy && !string.IsNullOrWhiteSpace(LicenseKeyInput));
        BuyCommand = new RelayCommand(_ => OpenBuyPage());
    }

    public string LicenseKeyInput
    {
        get => _licenseKeyInput;
        set
        {
            if (SetProperty(ref _licenseKeyInput, value))
                ActivateCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                ActivateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Für IsEnabled-Bindings (Eingabefeld während der Aktivierung sperren).</summary>
    public bool IsNotBusy => !_isBusy;

    /// <summary>True, sobald die Aktivierung erfolgreich abgeschlossen wurde.</summary>
    public bool ActivationSucceeded => _succeeded;

    private async Task ActivateAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("premium.activate.working");

        var error = await PremiumService.Instance.ActivateAsync(LicenseKeyInput);

        IsBusy = false;

        if (error == LicenseApiError.None)
        {
            _succeeded = true;
            StatusText = Loc.T("premium.activate.success");
            Succeeded?.Invoke(this, EventArgs.Empty);
            return;
        }

        StatusText = error switch
        {
            LicenseApiError.InvalidKey => Loc.T("premium.error.invalidKey"),
            LicenseApiError.DeviceLimit => Loc.T("premium.error.deviceLimit"),
            LicenseApiError.RateLimited => Loc.T("premium.error.rateLimited"),
            _ => Loc.T("premium.error.network")
        };
    }

    /// <summary>Öffnet die Kaufseite im Standardbrowser.</summary>
    public static void OpenBuyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(LicenseApiClient.BuyUrl) { UseShellExecute = true });
        }
        catch
        {
            // Kein Browser verfügbar -> still ignorieren.
        }
    }
}
