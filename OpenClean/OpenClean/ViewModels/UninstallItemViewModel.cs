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
    private long _sizeBytes;

    public UninstallItemViewModel(InstalledApp app, Func<UninstallItemViewModel, Task> onUninstall,
        Action onSelectionChanged)
    {
        Model = app;
        _onUninstall = onUninstall;
        _onSelectionChanged = onSelectionChanged;
        // Größe aus der Registry; fehlt sie, eine bereits berechnete (gecachte) Ordnergröße nutzen.
        _sizeBytes = app.EstimatedBytes > 0
            ? app.EstimatedBytes
            : InstalledAppsService.GetCachedFolderSize(app.SizeFolder);
        UninstallCommand = new AsyncRelayCommand(_ => RunAsync(), _ => CanUninstall && !IsBusy);
    }

    public InstalledApp Model { get; }

    public string Name => Model.Name;
    public string Version => Model.Version;
    public bool CanUninstall => Model.CanUninstall;

    public string PublisherDisplay =>
        string.IsNullOrWhiteSpace(Model.Publisher) ? Loc.T("uninstall.publisher.unknown") : Model.Publisher;

    public string SizeDisplay => _sizeBytes > 0 ? ByteFormatter.Format(_sizeBytes) : "—";

    public string InstallDateDisplay => Model.InstallDate?.ToString("dd.MM.yyyy") ?? "—";

    public long SizeBytes => _sizeBytes;

    /// <summary>
    /// True, wenn noch keine Größe vorliegt (weder Registry noch Cache), aber ein
    /// Größen-Ordner bekannt ist – dann lohnt die Hintergrund-Berechnung.
    /// </summary>
    public bool NeedsSizeCalculation =>
        _sizeBytes <= 0 && !string.IsNullOrWhiteSpace(Model.SizeFolder);

    /// <summary>Ordner für die nachträgliche Größenberechnung.</summary>
    public string SizeFolder => Model.SizeFolder;

    /// <summary>Setzt die nachträglich (aus dem Ordner) berechnete Größe und aktualisiert die Anzeige.</summary>
    public void SetComputedSize(long bytes)
    {
        if (bytes <= 0 || bytes == _sizeBytes) return;
        _sizeBytes = bytes;
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(SizeDisplay));
    }

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
