using System.Windows;
using OpenClean.Services;
using OpenClean.Services.Licensing;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>
/// ViewModel des gesperrten Zeitplan-Bereichs (Premium nicht freigeschaltet bzw.
/// Modul fehlt). Bietet Kauf/Aktivierung an sowie – bei vorhandener Lizenz, aber
/// fehlendem Modul – den erneuten Modul-Download.
/// </summary>
public sealed class LockedScheduleViewModel : ViewModelBase
{
    private string _statusText = "";
    private bool _isBusy;

    public RelayCommand BuyCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public AsyncRelayCommand RetryModuleCommand { get; }

    public LockedScheduleViewModel()
    {
        BuyCommand = new RelayCommand(_ => LicenseViewModel.OpenBuyPage());
        ActivateCommand = new RelayCommand(_ => ActivationDialog.Show(Application.Current?.MainWindow));
        RetryModuleCommand = new AsyncRelayCommand(_ => RetryModuleAsync(), _ => !IsBusy);
    }

    /// <summary>
    /// True, wenn zwar eine gültige Lizenz vorliegt, das Premium-Modul aber fehlt oder
    /// nicht geladen werden konnte (dann Reparatur/Neu-Download anbieten).
    /// </summary>
    public bool IsModuleMissing =>
        PremiumService.Instance.HasFeature(Contracts.PremiumContract.FeatureSchedule) &&
        PremiumService.Instance.GetSection(nameof(AppSection.Zeitplan)) is null;

    /// <summary>True, wenn Kauf/Aktivierung angeboten werden (keine gültige Lizenz).</summary>
    public bool ShowPurchase => !IsModuleMissing;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RetryModuleCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Lädt das fehlende Premium-Modul erneut vom Server.</summary>
    private async Task RetryModuleAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("premium.activate.downloadingModule");

        bool ok = await PremiumService.Instance.TryFetchModuleAsync();

        IsBusy = false;
        StatusText = ok ? "" : Loc.T("premium.error.moduleLoad");
        OnPropertyChanged(nameof(IsModuleMissing));
        OnPropertyChanged(nameof(ShowPurchase));
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel die berechneten Texte.</summary>
    public void Relocalize()
    {
        if (!IsBusy) StatusText = "";
    }
}
