using System.Windows.Media;
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
    private ImageSource? _icon;

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

    // ---- Icon / Buchstaben-Avatar ------------------------------------------

    /// <summary>Pfad zur Icon-Quelle (aufgelöste EXE), für das Hintergrund-Laden. Leer bei
    /// Store-Apps/nicht auflösbaren Kommandos (dann Buchstaben-Avatar).</summary>
    public string IconPath => Entry.ExecutablePath ?? "";

    /// <summary>Extrahiertes Programm-Icon; <c>null</c>, solange nicht/nicht extrahierbar.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    /// <summary>True, sobald ein echtes Icon vorliegt (sonst Buchstaben-Avatar anzeigen).</summary>
    public bool HasIcon => _icon is not null;

    /// <summary>Anfangsbuchstabe des Namens für den Fallback-Avatar.</summary>
    public string Initial => AvatarPalette.InitialFor(Entry.Name);

    /// <summary>Deterministische Avatar-Farbe aus dem Namen (stabil pro Eintrag).</summary>
    public Brush InitialBrush => AvatarPalette.BrushFor(Entry.Name);

    /// <summary>Setzt das im Hintergrund extrahierte Icon (Aufruf auf dem UI-Thread).</summary>
    public void SetIcon(ImageSource icon) => Icon = icon;

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
