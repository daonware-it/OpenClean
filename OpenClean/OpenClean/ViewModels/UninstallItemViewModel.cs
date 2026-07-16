using System;
using System.Windows.Media;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;

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
    private long _maxBytes = 1;
    private ImageSource? _icon;

    // Schwelle für das „GROSS“-Badge: > 100 GB.
    private const long BigThreshold = 100L * 1024 * 1024 * 1024;

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
    // Bei erkannter Manipulation (OPCL-20) gesperrt (UninstallerService sperrt zusätzlich selbst).
    public bool CanUninstall => Model.CanUninstall && !IntegrityState.IsBlocked;

    public string PublisherDisplay =>
        string.IsNullOrWhiteSpace(Model.Publisher) ? Loc.T("uninstall.publisher.unknown") : Model.Publisher;

    public string SizeDisplay => _sizeBytes > 0 ? ByteFormatter.Format(_sizeBytes) : "—";

    public string InstallDateDisplay => Model.InstallDate?.ToString("dd.MM.yyyy") ?? "—";

    public long SizeBytes => _sizeBytes;

    /// <summary>True bei besonders großen Programmen (&gt; 100 GB) – zeigt ein „GROSS“-Badge.</summary>
    public bool IsBig => _sizeBytes > BigThreshold;

    /// <summary>Balkenlänge relativ zum größten Programm (0–100).</summary>
    public double SizeBarPercent =>
        _maxBytes > 0 ? System.Math.Min((double)_sizeBytes / _maxBytes * 100.0, 100.0) : 0;

    /// <summary>Setzt den Bezugswert (größtes Programm) für den Größen-Balken.</summary>
    public void SetMaxBytes(long max)
    {
        _maxBytes = max > 0 ? max : 1;
        OnPropertyChanged(nameof(SizeBarPercent));
    }

    // ---- Icon / Buchstaben-Avatar ------------------------------------------

    /// <summary>Pfad zur Icon-Quelle (aus der Registry), für das Hintergrund-Laden.</summary>
    public string IconPath => Model.IconPath;

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
    public string Initial => AvatarPalette.InitialFor(Model.Name);

    /// <summary>Deterministische Avatar-Farbe aus dem Namen (stabil pro Programm).</summary>
    public Brush InitialBrush => AvatarPalette.BrushFor(Model.Name);

    /// <summary>Setzt das im Hintergrund extrahierte Icon (Aufruf auf dem UI-Thread).</summary>
    public void SetIcon(ImageSource icon) => Icon = icon;

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
        OnPropertyChanged(nameof(IsBig));
        OnPropertyChanged(nameof(SizeBarPercent));
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
