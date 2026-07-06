using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>
/// Wrapper um <see cref="StartupEntry"/> mit Toggle-Befehl für die UI.
/// </summary>
public sealed class StartupItemViewModel : ViewModelBase
{
    private readonly StartupService _service;
    private readonly Action<string> _reportError;
    private bool _isEnabled;

    public StartupEntry Entry { get; }

    public StartupItemViewModel(StartupEntry entry, StartupService service, Action<string> reportError)
    {
        Entry = entry;
        _service = service;
        _reportError = reportError;
        _isEnabled = entry.IsEnabled;
        ToggleCommand = new RelayCommand(_ => Toggle());
    }

    public RelayCommand ToggleCommand { get; }

    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string LocationDisplay => Entry.LocationDisplay;
    public string PublisherDisplay => Entry.PublisherDisplay;
    public string ImpactDisplay => Entry.ImpactDisplay;
    public StartupImpact Impact => Entry.Impact;

    /// <summary>False bei per Gruppenrichtlinie gesetzten Einträgen (nicht schaltbar).</summary>
    public bool CanToggle => Entry.CanToggle;

    /// <summary>Zweizeilig gebunden vom Toggle-Switch/Checkbox.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!CanToggle) { OnPropertyChanged(); return; }
            if (_isEnabled == value) return;
            try
            {
                _service.SetEnabled(Entry, value);
                _isEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
            catch (Exception ex)
            {
                _reportError(Loc.T("startup.error.changeFailed", Entry.Name, ex.Message));
                OnPropertyChanged(); // UI auf alten Wert zurücksetzen
            }
        }
    }

    public string StatusDisplay =>
        CanToggle ? (IsEnabled ? Loc.T("startup.state.enabled") : Loc.T("startup.state.disabled"))
        : Entry.Location is StartupLocation.HkcuRunOnce or StartupLocation.HklmRunOnce ? Loc.T("startup.state.once")
        : Loc.T("startup.state.policy");

    private void Toggle() => IsEnabled = !IsEnabled;

    /// <summary>Aktualisiert nach einem Sprachwechsel alle Anzeigetexte des Eintrags.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(LocationDisplay));
        OnPropertyChanged(nameof(PublisherDisplay));
        OnPropertyChanged(nameof(ImpactDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }
}
