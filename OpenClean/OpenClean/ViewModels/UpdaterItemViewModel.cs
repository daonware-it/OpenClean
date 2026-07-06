using OpenClean.Models;

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
