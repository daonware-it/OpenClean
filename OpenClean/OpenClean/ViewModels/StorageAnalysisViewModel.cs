using System.Collections.ObjectModel;
using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>
/// Steuert die Belegungsanalyse (Sunburst): Laufwerk wählen → scannen → per Klick in
/// Unterordner zoomen.
///
/// <para>Der Scan läuft nie automatisch beim Öffnen des Bereichs – er kann Minuten dauern.
/// Der Baum wird nur bis <see cref="Controls.SunburstChart.RingCount"/> Ebenen tief gehalten; taucht
/// man in einen abgeschnittenen Ordner ein (<see cref="FolderNode.IsPartial"/>), wird
/// dessen Teilbaum nachgeladen.</para>
/// </summary>
public sealed class StorageAnalysisViewModel : ViewModelBase
{
    private readonly DiskScannerService _scanner = new();
    private readonly SystemInfoService _systemInfo = new();

    private CancellationTokenSource? _cts;

    private DriveUsage? _selectedDrive;
    private FolderNode? _rootNode;      // Wurzel des gescannten Laufwerks
    private FolderNode? _currentNode;   // aktuell angezeigte Ebene
    private bool _isBusy;
    private bool _hasResult;
    private double _scanProgressPercent;
    private string _scanProgressText = "";
    private string _statusText = Loc.T("storage.empty");

    public ObservableCollection<DriveUsage> Drives { get; } = new();

    /// <summary>Pfad von der Wurzel bis zum aktuellen Knoten (für die Breadcrumb-Leiste).</summary>
    public ObservableCollection<FolderNode> Breadcrumb { get; } = new();

    /// <summary>Die fünf größten Unterordner der aktuellen Ebene.</summary>
    public ObservableCollection<FolderNode> TopFolders { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand DrillDownCommand { get; }
    public RelayCommand NavigateUpCommand { get; }
    public AsyncRelayCommand NavigateToCommand { get; }

    public StorageAnalysisViewModel()
    {
        foreach (var drive in _systemInfo.GetFixedDrives())
            Drives.Add(drive);
        _selectedDrive = Drives.FirstOrDefault();

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy && SelectedDrive is not null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        DrillDownCommand = new AsyncRelayCommand(
            node => DrillDownAsync(node as FolderNode), _ => !IsBusy);
        NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => !IsBusy && CurrentNode?.Parent is not null);
        NavigateToCommand = new AsyncRelayCommand(
            node => NavigateToAsync(node as FolderNode), _ => !IsBusy);
    }

    public DriveUsage? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
                ScanCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Der Knoten, der gerade im Zentrum des Sunbursts steht.</summary>
    public FolderNode? CurrentNode
    {
        get => _currentNode;
        private set
        {
            if (!SetProperty(ref _currentNode, value)) return;
            RebuildBreadcrumb();
            RebuildTopFolders();
            NavigateUpCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            DrillDownCommand.RaiseCanExecuteChanged();
            NavigateUpCommand.RaiseCanExecuteChanged();
            NavigateToCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>True, sobald ein Scan Daten geliefert hat (steuert Diagramm vs. Leertext).</summary>
    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    public double ScanProgressPercent
    {
        get => _scanProgressPercent;
        private set => SetProperty(ref _scanProgressPercent, value);
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private async Task ScanAsync()
    {
        var drive = SelectedDrive;
        if (drive is null) return;

        var result = await RunScanAsync(drive.Name, drive.TotalBytes);
        if (result is null) return;

        _rootNode = result.Root;
        CurrentNode = result.Root;
        HasResult = true;

        StatusText = result.SkippedFolders > 0
            ? Loc.T("storage.doneSkipped", ByteFormatter.Format(result.Root.TotalBytes), result.SkippedFolders)
            : Loc.T("storage.done", ByteFormatter.Format(result.Root.TotalBytes));
    }

    /// <summary>Klick auf ein Segment: hineinzoomen, Teilbaum bei Bedarf nachladen.</summary>
    private async Task DrillDownAsync(FolderNode? node)
    {
        if (node is null) return;
        await NavigateToAsync(node);
    }

    /// <summary>Klick auf die Mitte oder auf die Breadcrumb: eine Ebene zurück.</summary>
    private void NavigateUp()
    {
        if (CurrentNode?.Parent is { } parent)
            CurrentNode = parent;
    }

    /// <summary>
    /// Wechselt zu einem Knoten. Ist er abgeschnitten (<see cref="FolderNode.IsPartial"/>),
    /// wird sein Teilbaum vorher nachgeladen – die Bytes stimmen schon, nur die Kinder fehlen.
    /// </summary>
    private async Task NavigateToAsync(FolderNode? node)
    {
        if (node is null) return;

        if (node.IsPartial)
        {
            var result = await RunScanAsync(node.FullPath, node.TotalBytes);
            if (result is null) return;

            node.ReplaceChildren(result.Root.Children);
            StatusText = Loc.T("storage.done", ByteFormatter.Format(node.TotalBytes));
        }

        CurrentNode = node;
    }

    /// <summary>
    /// Gemeinsamer Scan-Ablauf für Erstscan und Nachladen. Liefert null, wenn der Nutzer
    /// abgebrochen hat.
    /// </summary>
    private async Task<DiskScanResult?> RunScanAsync(string rootPath, long expectedBytes)
    {
        IsBusy = true;
        ScanProgressPercent = 0;
        ScanProgressText = Loc.T("storage.scanningPrepare");

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // Der Fortschritt schätzt anhand der bekannten Gesamtgröße – exakter geht es nicht,
        // ohne die Platte vorher ein zweites Mal zu lesen.
        var progress = new Progress<DiskScanProgress>(p =>
        {
            ScanProgressPercent = expectedBytes > 0
                ? Math.Clamp(100.0 * p.ScannedBytes / expectedBytes, 0, 100)
                : 0;
            ScanProgressText = Loc.T("storage.scanning", p.CurrentPath);
        });

        try
        {
            var options = new DiskScanOptions
            {
                RootPath = rootPath,
                MaxDepth = Controls.SunburstChart.RingCount,
                MaxFiles = 0,                     // der Sunburst braucht keine Dateiliste
                ExcludeSystemFolders = false      // die Anzeige zeigt die Platte ungefiltert
            };

            return await _scanner.ScanAsync(options, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.T("storage.canceled");
            return null;
        }
        finally
        {
            IsBusy = false;
            ScanProgressPercent = 0;
            ScanProgressText = "";
        }
    }

    private void RebuildBreadcrumb()
    {
        Breadcrumb.Clear();
        var chain = new List<FolderNode>();
        for (var node = CurrentNode; node is not null; node = node.Parent)
            chain.Add(node);
        chain.Reverse();
        foreach (var node in chain)
            Breadcrumb.Add(node);
    }

    private void RebuildTopFolders()
    {
        TopFolders.Clear();
        if (CurrentNode is null) return;
        foreach (var child in CurrentNode.Children.OrderByDescending(c => c.TotalBytes).Take(5))
            TopFolders.Add(child);
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel die berechneten Texte.</summary>
    public void Relocalize()
    {
        if (!IsBusy && !HasResult)
            StatusText = Loc.T("storage.empty");
    }
}
