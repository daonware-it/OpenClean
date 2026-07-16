using System.Windows.Media;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;

namespace OpenClean.ViewModels;

/// <summary>
/// Wrapper um <see cref="ContextMenuEntry"/> mit Umschalter für die UI (Muster:
/// <see cref="StartupItemViewModel"/>).
/// </summary>
public sealed class ContextMenuItemViewModel : ViewModelBase
{
    private readonly ContextMenuService _service;
    private readonly Action<string> _reportError;
    private bool _isEnabled;
    private ImageSource? _icon;

    public ContextMenuEntry Entry { get; }

    public ContextMenuItemViewModel(ContextMenuEntry entry, ContextMenuService service, Action<string> reportError)
    {
        Entry = entry;
        _service = service;
        _reportError = reportError;
        _isEnabled = entry.IsEnabled;
        ToggleCommand = new RelayCommand(_ => IsEnabled = !IsEnabled);
    }

    public RelayCommand ToggleCommand { get; }

    public string Name => Entry.Name;
    public string RegistryPathDisplay => Entry.RegistryPathDisplay;
    public string PublisherDisplay => Entry.PublisherDisplay;
    public string LocationDisplay => Entry.LocationDisplay;
    public string KindDisplay => Entry.KindDisplay;

    /// <summary>Alle Orte des Eintrags (der Ort-Filter prüft gegen diese Liste).</summary>
    public IReadOnlyList<ContextMenuLocation> Locations => Entry.Locations;

    /// <summary>False bei Windows-Kern-Verben (Öffnen, Kopieren …) – nicht abschaltbar.</summary>
    // Bei erkannter Manipulation (OPCL-20) gesperrt – siehe StartupItemViewModel.CanToggle.
    public bool CanToggle => Entry.CanToggle && !IntegrityState.IsBlocked;

    // ---- Icon / Buchstaben-Avatar ------------------------------------------

    /// <summary>Pfad zur Icon-Quelle (Handler-DLL/EXE); leer, wenn nicht auflösbar.</summary>
    public string IconPath => Entry.HandlerPath ?? "";

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public string Initial => AvatarPalette.InitialFor(Entry.Name);

    public Brush InitialBrush => AvatarPalette.BrushFor(Entry.Name);

    /// <summary>Setzt das im Hintergrund extrahierte Icon (Aufruf auf dem UI-Thread).</summary>
    public void SetIcon(ImageSource icon) => Icon = icon;

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
                _reportError(Loc.T("contextmenu.error.changeFailed", Entry.Name, ex.Message));
                OnPropertyChanged(); // UI auf alten Wert zurücksetzen
            }
        }
    }

    public string StatusDisplay => Entry.StatusDisplay;

    /// <summary>Aktualisiert nach einem Sprachwechsel alle Anzeigetexte des Eintrags.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(PublisherDisplay));
        OnPropertyChanged(nameof(LocationDisplay));
        OnPropertyChanged(nameof(KindDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }
}
