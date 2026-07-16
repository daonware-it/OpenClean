using System.Windows.Media;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;

namespace OpenClean.ViewModels;

/// <summary>
/// Wrapper um <see cref="StartupEntry"/> mit Toggle-Befehl für die UI.
/// </summary>
public sealed class StartupItemViewModel : ViewModelBase
{
    private readonly StartupService _service;
    private readonly DelayedStartupService _delayService;
    private readonly Action<string> _reportError;
    private bool _isEnabled;
    private int _delaySeconds;
    private ImageSource? _icon;

    public StartupEntry Entry { get; }

    public StartupItemViewModel(StartupEntry entry, StartupService service,
        DelayedStartupService delayService, Action<string> reportError)
    {
        Entry = entry;
        _service = service;
        _delayService = delayService;
        _reportError = reportError;
        _delaySeconds = delayService.GetDelay(entry);
        // Ein verzögerter Eintrag ist in der Registry deaktiviert, startet aber weiterhin
        // (über die geplante Aufgabe) – für die UI gilt er daher als aktiviert.
        _isEnabled = entry.IsEnabled || _delaySeconds > 0;
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
    // Bei erkannter Manipulation (OPCL-20) ist der Schalter gesperrt. Bewusst über CanToggle:
    // der Setter von IsEnabled bricht dann ab, OHNE die Eigenschaft zu ändern – die Anzeige
    // zeigt also nie einen Zustand, der gar nicht in die Registry geschrieben wurde.
    public bool CanToggle => Entry.CanToggle && !IntegrityState.IsBlocked;

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
                if (!value && _delaySeconds > 0)
                {
                    // Verzögert und wird jetzt ausgeschaltet: Die Aufgabe muss weg, der
                    // Original-Eintrag bleibt deaktiviert – sonst bliebe ein Zombie-Task zurück.
                    _delayService.Remove(Entry, restoreOriginal: false);
                    _delaySeconds = 0;
                    OnPropertyChanged(nameof(DelaySeconds));
                    OnPropertyChanged(nameof(DelayDisplay));
                }
                else
                {
                    _service.SetEnabled(Entry, value);
                }

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

    // ---- Verzögerter Start --------------------------------------------------

    /// <summary>False, wenn der Eintrag nicht verzögert werden kann (RunOnce, Richtlinie,
    /// Store-App oder ohne auflösbare EXE).</summary>
    public bool CanDelay => _delayService.CanDelay(Entry);

    /// <summary>Verzögerung in Sekunden; 0 = startet unmittelbar mit der Anmeldung.</summary>
    public int DelaySeconds
    {
        get => _delaySeconds;
        set
        {
            if (_delaySeconds == value) return;
            if (!CanDelay) { OnPropertyChanged(); return; }
            try
            {
                _delayService.Apply(Entry, value);
                _delaySeconds = value;

                // Nach dem Setzen wie nach dem Aufheben startet das Programm wieder –
                // verzögert bzw. sofort. Der Schalter steht in beiden Fällen auf „an".
                if (!_isEnabled)
                {
                    _isEnabled = true;
                    OnPropertyChanged(nameof(IsEnabled));
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(DelayDisplay));
                OnPropertyChanged(nameof(StatusDisplay));
            }
            catch (Exception ex)
            {
                _reportError(Loc.T("startup.delay.error", Entry.Name, ex.Message));
                OnPropertyChanged(); // UI auf alten Wert zurücksetzen
            }
        }
    }

    /// <summary>Beschriftung der eingestellten Verzögerung („Keine", „2 Min." …).</summary>
    public string DelayDisplay => DelayedStartupService.DisplayFor(_delaySeconds);

    public string StatusDisplay =>
        _delaySeconds > 0 ? Loc.T("startup.state.delayed", DelayDisplay)
        : CanToggle ? (IsEnabled ? Loc.T("startup.state.enabled") : Loc.T("startup.state.disabled"))
        : Entry.Location is StartupLocation.HkcuRunOnce or StartupLocation.HklmRunOnce ? Loc.T("startup.state.once")
        : Loc.T("startup.state.policy");

    private void Toggle() => IsEnabled = !IsEnabled;

    /// <summary>Aktualisiert nach einem Sprachwechsel alle Anzeigetexte des Eintrags.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(LocationDisplay));
        OnPropertyChanged(nameof(PublisherDisplay));
        OnPropertyChanged(nameof(ImpactDisplay));
        OnPropertyChanged(nameof(DelayDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }
}
