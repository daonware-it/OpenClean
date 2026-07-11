using System.Windows.Media;
using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>
/// Wrapper um ein einzelnes <see cref="AppUpdate"/> mit Aktualisieren-Befehl für die UI.
/// Der eigentliche Update-Ablauf (Bestätigung, Fortschritt, Neu-Scan) liegt im
/// Eltern-ViewModel und wird über den <c>onUpdate</c>-Callback ausgelöst.
/// </summary>
public sealed class UpdaterItemViewModel : ViewModelBase
{
    private readonly Func<UpdaterItemViewModel, Task> _onUpdate;
    private bool _isUpdating;
    private ImageSource? _icon;

    public UpdaterItemViewModel(AppUpdate app, Func<UpdaterItemViewModel, Task> onUpdate)
    {
        Model = app;
        _onUpdate = onUpdate;
        UpdateCommand = new AsyncRelayCommand(_ => RunUpdateAsync(), _ => !IsUpdating);
    }

    /// <summary>Das zugrunde liegende Modell (für Batch-Updates im Eltern-ViewModel).</summary>
    public AppUpdate Model { get; }

    public string Name => Model.Name;
    public string Id => Model.Id;
    public string CurrentVersion => Model.CurrentVersion;
    public string AvailableVersion => Model.AvailableVersion;

    // ---- Icon / Buchstaben-Avatar ------------------------------------------

    /// <summary>Echtes Programm-Icon (aus dem passenden Registry-Eintrag extrahiert);
    /// <c>null</c>, solange keins gefunden/extrahiert wurde.</summary>
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
    public string Initial => AvatarPalette.InitialFor(Model.Name);

    /// <summary>Deterministische Avatar-Farbe aus dem Namen (stabil pro Programm).</summary>
    public Brush InitialBrush => AvatarPalette.BrushFor(Model.Name);

    /// <summary>Setzt das im Hintergrund extrahierte Icon (Aufruf auf dem UI-Thread).</summary>
    public void SetIcon(ImageSource icon) => Icon = icon;

    public AsyncRelayCommand UpdateCommand { get; }

    /// <summary>True während dieses App-Update läuft (deaktiviert den Button).</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (SetProperty(ref _isUpdating, value))
                UpdateCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunUpdateAsync()
    {
        IsUpdating = true;
        try
        {
            await _onUpdate(this);
        }
        finally
        {
            IsUpdating = false;
        }
    }
}
