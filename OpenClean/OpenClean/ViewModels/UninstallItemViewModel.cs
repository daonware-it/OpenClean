using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>
/// Wrapper um ein <see cref="InstalledApp"/> für die Deinstallations-Liste: Auswahl
/// (für Batch-Deinstallation) und Einzel-Deinstallations-Befehl. Der eigentliche Ablauf
/// (Bestätigung, Fortschritt, Reste-Suche) liegt im Eltern-ViewModel.
/// </summary>
public sealed class UninstallItemViewModel : ViewModelBase
{
    private readonly Func<UninstallItemViewModel, Task> _onUninstall;
    private readonly Action _onSelectionChanged;
    private bool _isSelected;
    private bool _isBusy;

    public UninstallItemViewModel(InstalledApp app, Func<UninstallItemViewModel, Task> onUninstall,
        Action onSelectionChanged)
    {
        Model = app;
        _onUninstall = onUninstall;
        _onSelectionChanged = onSelectionChanged;
        UninstallCommand = new AsyncRelayCommand(_ => RunAsync(), _ => CanUninstall && !IsBusy);
    }

    public InstalledApp Model { get; }

    public string Name => Model.Name;
    public string Version => Model.Version;
    public bool CanUninstall => Model.CanUninstall;

    public string PublisherDisplay =>
        string.IsNullOrWhiteSpace(Model.Publisher) ? Loc.T("uninstall.publisher.unknown") : Model.Publisher;

    public string SizeDisplay =>
        Model.EstimatedBytes > 0 ? ByteFormatter.Format(Model.EstimatedBytes) : "—";

    public string InstallDateDisplay => Model.InstallDate?.ToString("dd.MM.yyyy") ?? "—";

    public long SizeBytes => Model.EstimatedBytes;

    public AsyncRelayCommand UninstallCommand { get; }

    /// <summary>Ob dieses Programm für die Batch-Deinstallation ausgewählt ist.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _onSelectionChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                UninstallCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        try { await _onUninstall(this); }
        finally { IsBusy = false; }
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel die berechneten Texte.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(PublisherDisplay));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(InstallDateDisplay));
    }
}
