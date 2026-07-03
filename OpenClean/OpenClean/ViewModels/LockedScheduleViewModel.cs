using System.Windows;
using OpenClean.Services;
using OpenClean.Services.Licensing;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>
/// ViewModel des gesperrten Zeitplan-Bereichs (Premium nicht freigeschaltet bzw.
/// Modul fehlt). Bietet Kauf/Aktivierung an und – für Bestandsnutzer aus v0.11
/// (Grandfathering) – die Verwaltung des weiterlaufenden Alt-Zeitplans.
/// </summary>
public sealed class LockedScheduleViewModel : ViewModelBase
{
    private readonly ScheduleTaskService _taskService = new();
    private string _statusText = "";
    private bool _isBusy;

    public RelayCommand BuyCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public AsyncRelayCommand DisableLegacyCommand { get; }
    public AsyncRelayCommand RetryModuleCommand { get; }

    public LockedScheduleViewModel()
    {
        BuyCommand = new RelayCommand(_ => LicenseViewModel.OpenBuyPage());
        ActivateCommand = new RelayCommand(_ => ActivationDialog.Show(Application.Current?.MainWindow));
        DisableLegacyCommand = new AsyncRelayCommand(_ => DisableLegacyAsync(), _ => !IsBusy);
        RetryModuleCommand = new AsyncRelayCommand(_ => RetryModuleAsync(), _ => !IsBusy);
    }

    /// <summary>
    /// True für Bestandsnutzer, deren geplante Reinigung aus v0.11 weiterläuft
    /// (Grandfathering): Info-Box mit Deaktivieren-Option anzeigen.
    /// </summary>
    public bool IsGrandfathered =>
        SettingsService.Instance.Current.ScheduleGrandfathered == true &&
        SettingsService.Instance.Current.Schedule.Enabled;

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
            {
                DisableLegacyCommand.RaiseCanExecuteChanged();
                RetryModuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Deaktiviert den weiterlaufenden Alt-Zeitplan (Task + Einstellung).</summary>
    private async Task DisableLegacyAsync()
    {
        IsBusy = true;

        await Task.Run(() => _taskService.Unregister());
        SettingsService.Instance.Current.Schedule.Enabled = false;
        SettingsService.Instance.Save();

        IsBusy = false;
        StatusText = Loc.T("premium.locked.scheduleDisabled");
        OnPropertyChanged(nameof(IsGrandfathered));
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
