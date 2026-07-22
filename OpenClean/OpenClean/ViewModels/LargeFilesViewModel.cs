using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;
using OpenClean.Services.Safety;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>
/// Der Große-Dateien-Finder: Laufwerk + Mindestgröße wählen → suchen → markierte Dateien in
/// den Papierkorb verschieben.
///
/// <para>Anders als die Bereinigung fasst dieser Bereich echte Nutzerdaten an. Deshalb:
/// Systemordner werden schon beim Scan ausgeschlossen, vor jedem Löschen prüft
/// <see cref="PathSafety"/> noch einmal, gelöscht wird ausschließlich in den Papierkorb, und
/// der Vorgang ist bei erkannter Manipulation (OPCL-20) komplett gesperrt.</para>
/// </summary>
public sealed class LargeFilesViewModel : ViewModelBase
{
    private readonly DiskScannerService _scanner = new();
    private readonly SystemInfoService _systemInfo = new();
    private readonly IDialogService _dialogs;

    /// <summary>Deckelung der Trefferliste – bewusst als Konstante statt Magic Number in
    /// <see cref="DiskScanOptions.MaxFiles"/>, damit <see cref="ScanAsync"/> erkennen kann,
    /// ob die Liste tatsächlich an der Grenze gekappt wurde.</summary>
    private const int MaxFiles = 500;

    private CancellationTokenSource? _cts;
    private bool _suppressSelectionCallback;

    /// <summary>True während <see cref="DeleteSelectedAsync"/> läuft. Das Löschen selbst ist
    /// bewusst nicht abbrechbar (läuft schon in Windows) – <see cref="CancelCommand"/> darf in
    /// dieser Zeit trotz <see cref="IsBusy"/> nicht aktiv erscheinen.</summary>
    private bool _isDeleting;

    private DriveUsage? _selectedDrive;
    private int _minSizeMb = 100;
    private bool _isBusy;
    private bool _hasScanned;
    private double _scanProgressPercent;
    private string _scanProgressText = "";
    private string _statusText = Loc.T("largefiles.empty");

    public ObservableCollection<LargeFileViewModel> Files { get; } = new();

    /// <summary>Sortierbare Sicht auf <see cref="Files"/> – die Spaltenköpfe setzen hier die Sortierung.</summary>
    public ICollectionView FilesView { get; }

    public ObservableCollection<DriveUsage> Drives { get; } = new();

    /// <summary>Auswählbare Mindestgrößen in MB.</summary>
    public IReadOnlyList<int> MinSizeOptions { get; } = new[] { 50, 100, 500, 1024 };

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public RelayCommand SortCommand { get; }

    public LargeFilesViewModel(IDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? DialogService.Default;

        foreach (var drive in _systemInfo.GetFixedDrives())
            Drives.Add(drive);
        _selectedDrive = Drives.FirstOrDefault();

        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.SortDescriptions.Add(new SortDescription(nameof(LargeFileViewModel.SizeBytes),
            ListSortDirection.Descending));

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy && SelectedDrive is not null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy && !_isDeleting);
        DeleteSelectedCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync(), _ => CanDelete);
        SelectAllCommand = new RelayCommand(_ => SetAllSelection(true), _ => Files.Count > 0 && !IsBusy);
        DeselectAllCommand = new RelayCommand(_ => SetAllSelection(false), _ => Files.Count > 0 && !IsBusy);
        OpenInExplorerCommand = new RelayCommand(file => OpenInExplorer(file as LargeFileViewModel));
        SortCommand = new RelayCommand(column => Sort(column as string));
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

    /// <summary>Mindestgröße in MB; Dateien darunter tauchen gar nicht erst auf.</summary>
    public int MinSizeMb
    {
        get => _minSizeMb;
        set => SetProperty(ref _minSizeMb, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            SelectAllCommand.RaiseCanExecuteChanged();
            DeselectAllCommand.RaiseCanExecuteChanged();
            RefreshSelectionState();
        }
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

    public bool HasResults => Files.Count > 0;
    public bool ShowEmptyHint => !IsBusy && Files.Count == 0;

    public int SelectedCount => Files.Count(f => f.IsSelected);
    public long SelectedBytes => Files.Where(f => f.IsSelected).Sum(f => f.SizeBytes);

    public string SelectionSummary => _hasScanned
        ? Loc.T("largefiles.selection", SelectedCount, ByteFormatter.Format(SelectedBytes))
        : Loc.T("common.notAnalyzed");

    // IntegrityState.IsBlocked: bei erkannter Manipulation bleibt die Suche nutzbar,
    // das Löschen aber gesperrt – genau wie CleanerViewModel.CanClean es handhabt.
    public bool CanDelete => _hasScanned && !IsBusy && SelectedCount > 0 && !IntegrityState.IsBlocked;

    private async Task ScanAsync()
    {
        var drive = SelectedDrive;
        if (drive is null) return;

        IsBusy = true;
        _hasScanned = false;
        Files.Clear();
        RaiseListState();

        ScanProgressPercent = 0;
        ScanProgressText = Loc.T("storage.scanningPrepare");
        StatusText = "";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // Belegung, nicht Kapazität: Der Scanner summiert nur tatsächlich gefundene Bytes
        // (siehe StorageAnalysisViewModel, dort ist derselbe Fehler bereits behoben).
        long expectedBytes = drive.UsedBytes;
        var progress = new Progress<DiskScanProgress>(p =>
        {
            ScanProgressPercent = expectedBytes > 0
                ? Math.Clamp(100.0 * p.ScannedBytes / expectedBytes, 0, 100)
                : 0;
            ScanProgressText = Loc.T("largefiles.scanning", p.CurrentPath);
        });

        DiskScanResult result;
        try
        {
            var options = new DiskScanOptions
            {
                RootPath = drive.Name,
                MaxDepth = 0,                       // der Finder braucht keinen Baum
                MinFileBytes = MinSizeMb * 1024L * 1024L,
                MaxFiles = MaxFiles,
                ExcludeSystemFolders = true         // Nutzerdaten-Löschpfad: Systemordner raus
            };

            result = await _scanner.ScanAsync(options, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsBusy = false;
            ScanProgressText = "";
            StatusText = Loc.T("storage.canceled");
            RaiseListState();
            RefreshSelectionState();
            return;
        }

        foreach (var file in result.LargestFiles)
        {
            var vm = new LargeFileViewModel(file)
            {
                SelectionChanged = () =>
                {
                    if (_suppressSelectionCallback) return;
                    RefreshSelectionState();
                }
            };
            Files.Add(vm);
        }

        _hasScanned = true;
        IsBusy = false;
        ScanProgressPercent = 0;
        ScanProgressText = "";

        long totalBytes = result.LargestFiles.Sum(f => f.SizeBytes);
        string baseStatus = Files.Count == 0
            ? Loc.T("largefiles.noResults")
            : result.SkippedFolders > 0
                ? Loc.T("largefiles.foundSkipped", Files.Count, ByteFormatter.Format(totalBytes), result.SkippedFolders)
                : Loc.T("largefiles.found", Files.Count, ByteFormatter.Format(totalBytes));

        // Deckelung sichtbar machen: Bei exakt MaxFiles Treffern gibt es womöglich mehr, die
        // Liste sagt das aber sonst nicht – der Nutzer würde Vollständigkeit annehmen.
        StatusText = Files.Count == MaxFiles
            ? baseStatus + " " + Loc.T("largefiles.capped", MaxFiles)
            : baseStatus;

        RaiseListState();
        RefreshSelectionState();
    }

    private async Task DeleteSelectedAsync()
    {
        // Zweite Verteidigungslinie neben CanExecute – hier wird wirklich gelöscht.
        if (IntegrityState.IsBlocked) return;

        var selected = Files.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) return;

        if (!_dialogs.ConfirmThemed(BuildDeleteConfirmMessage(selected))) return;

        // Löschen selbst ist bewusst nicht abbrechbar (läuft schon in Windows) – CancelCommand
        // darf trotz IsBusy nicht aktiv wirken, siehe dessen CanExecute im Konstruktor
        // (IsBusy && !_isDeleting).
        _isDeleting = true;
        IsBusy = true;
        StatusText = "";

        try
        {
            // Dritte Verteidigungslinie: PathSafety entscheidet endgültig, was angefasst wird.
            var deletable = selected.Where(f => PathSafety.IsDeletable(f.FullPath)).ToList();
            var blocked = selected.Except(deletable).ToList();
            foreach (var file in blocked)
                file.MarkProtected();

            var requests = deletable
                .Select(f => new SafeDeleteRequest(f.FullPath, false, f.SizeBytes))
                .ToList();

            IReadOnlyList<SafeDeleteOutcome> outcomes = await RunDeleteAsync(requests);

            ApplyDeleteOutcomes(deletable, outcomes, blocked.Count);
        }
        catch (Exception ex)
        {
            // Ohne diese Meldung sähe der Nutzer bei einer unerwarteten Ausnahme gar keine
            // Rückmeldung – markierte Dateien blieben liegen und niemand wüsste, warum. Die
            // Ausnahme wird nicht weitergeworfen: AsyncRelayCommand würde zwar zusätzlich einen
            // Fehlerdialog zeigen, aber die Statuszeile soll unabhängig davon stimmen.
            StatusText = Loc.T("largefiles.deleteError", ex.Message);
        }
        finally
        {
            // Auch im Fehlerfall muss der Listenzustand aktualisiert werden: Wurden vor der
            // Ausnahme bereits Dateien aus "Files" entfernt, wären "HasResults"/"ShowEmptyHint"
            // sonst veraltet.
            RaiseListState();
            RefreshSelectionState();

            _isDeleting = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Baut den Bestätigungstext und benennt dabei ehrlich, was NICHT im Papierkorb landet: Bei
    /// aktivem „Dateien sofort löschen“, auf Laufwerken ohne Papierkorb oder oberhalb der Quote
    /// löscht Windows endgültig. Das gehört VOR die Entscheidung – nicht als Windows-Rückfrage
    /// mitten in den Lauf.
    /// </summary>
    private static string BuildDeleteConfirmMessage(IReadOnlyList<LargeFileViewModel> selected)
    {
        long expectedBytes = selected.Sum(f => f.SizeBytes);
        var permanent = selected.Where(f => !RecycleBin.WillGoToRecycleBin(f.FullPath, f.SizeBytes)).ToList();

        string message = Loc.T("largefiles.confirm", selected.Count, ByteFormatter.Format(expectedBytes));
        if (permanent.Count > 0)
        {
            message += Loc.T("largefiles.confirm.permanent",
                permanent.Count, ByteFormatter.Format(permanent.Sum(f => f.SizeBytes)));
        }

        return message;
    }

    /// <summary>
    /// Führt das eigentliche Löschen aus. Löscht – sofern das Sicherheitsnetz aktiv ist – über eine
    /// Backup-Sitzung, damit der Lauf im Wiederherstellen-Bereich auftaucht und sich rückgängig
    /// machen lässt; sonst direkt in den Papierkorb (wie bisher).
    ///
    /// <para>Kein Wiederherstellungspunkt (anders als <see cref="SafetyPrompt.PrepareAsync"/>): Die
    /// Systemwiederherstellung sichert keine Nutzerdaten, für eine gelöschte Videodatei wäre sie
    /// wirkungslos. RecycleOnly: NIEMALS eine Backup-Kopie anlegen – eine mehrere GB große Datei
    /// erst zu kopieren würde den Platzbedarf verdoppeln und den Zweck des Bereichs zerstören.</para>
    /// </summary>
    private async Task<IReadOnlyList<SafeDeleteOutcome>> RunDeleteAsync(IReadOnlyList<SafeDeleteRequest> requests)
    {
        // Fensterhandle MUSS auf dem UI-Thread beschafft werden (vor dem Task.Run) – ein eventueller
        // "zu groß für den Papierkorb"-Dialog soll modal zum Hauptfenster erscheinen. Der eigentliche
        // Aufruf läuft im Hintergrund, weil er ohne Zeitlimit auf eine mögliche Nutzerbestätigung
        // warten kann und den UI-Thread sonst einfrieren würde.
        IntPtr ownerHandle = _dialogs.OwnerHandle;

        BackupSession? session = SettingsService.Instance.Current.Safety.BackupBeforeDelete
            ? BackupService.Instance.BeginSession("largefiles", ownerHandle)
            : null;

        IReadOnlyList<SafeDeleteOutcome> outcomes = await Task.Run(() =>
        {
            if (session is not null)
                return session.TryDeleteMany(requests, SafeDeleteStrategy.RecycleOnly);

            // Sicherheitsnetz abgeschaltet -> direkt in den Papierkorb wie bisher.
            var failedPaths = RecycleBin.MoveToRecycleBin(
                requests.Select(r => r.Path).ToList(), ownerHandle);
            var failedSet = new HashSet<string>(failedPaths, StringComparer.OrdinalIgnoreCase);
            return requests
                .Select(r => failedSet.Contains(r.Path)
                    ? SafeDeleteOutcome.Skipped
                    : SafeDeleteOutcome.Deleted)
                .ToList();
        });

        session?.Commit();
        return outcomes;
    }

    /// <summary>
    /// Wertet die Löschergebnisse aus: entfernt gelöschte Dateien aus der Liste, markiert
    /// fehlgeschlagene und setzt die Statuszeile. Geschützte (blockierte) Dateien werden nie zum
    /// Löschen versucht – sie zählen deshalb nicht als „fehlgeschlagen“, sondern werden getrennt
    /// ausgewiesen.
    /// </summary>
    private void ApplyDeleteOutcomes(
        IReadOnlyList<LargeFileViewModel> deletable, IReadOnlyList<SafeDeleteOutcome> outcomes, int blockedCount)
    {
        long freedBytes = 0;
        int deletedCount = 0;
        int failedCount = 0;

        for (int i = 0; i < deletable.Count; i++)
        {
            var file = deletable[i];
            if (outcomes[i] != SafeDeleteOutcome.Deleted)
            {
                file.MarkFailed();
                failedCount++;
                continue;
            }

            freedBytes += file.SizeBytes;
            deletedCount++;
            Files.Remove(file);
        }

        StatusText = (failedCount, blockedCount) switch
        {
            (0, 0) => Loc.T("largefiles.deleted", deletedCount, ByteFormatter.Format(freedBytes)),
            (> 0, 0) => Loc.T("largefiles.deletePartial", deletedCount, failedCount),
            (0, > 0) => Loc.T("largefiles.deletePartialProtected", deletedCount, blockedCount),
            _ => Loc.T("largefiles.deletePartialBoth", deletedCount, failedCount, blockedCount)
        };
    }

    private void SetAllSelection(bool selected)
    {
        // Bulk-Operation: pro-Item-Callback unterdrücken (sonst O(n²) Benachrichtigungen),
        // danach genau eine Gesamt-Aktualisierung – wie in CleanerViewModel.SetAllSelection.
        _suppressSelectionCallback = true;
        try
        {
            foreach (var file in Files)
                file.IsSelected = selected;
        }
        finally
        {
            _suppressSelectionCallback = false;
        }

        RefreshSelectionState();
    }

    /// <summary>Klick auf einen Spaltenkopf: nach dieser Property sortieren, Richtung umkehren,
    /// wenn dieselbe Spalte erneut geklickt wird.</summary>
    private void Sort(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return;

        var current = FilesView.SortDescriptions.FirstOrDefault();
        var direction = current.PropertyName == propertyName && current.Direction == ListSortDirection.Descending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        FilesView.SortDescriptions.Clear();
        FilesView.SortDescriptions.Add(new SortDescription(propertyName, direction));
    }

    /// <summary>Doppelklick auf eine Zeile: Ordner im Explorer öffnen, Datei markiert.</summary>
    private static void OpenInExplorer(LargeFileViewModel? file)
    {
        if (file is null) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch { /* Explorer nicht startbar – kein Grund, die App zu stören */ }
    }

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanDelete));
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        DeselectAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel die berechneten Texte.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        foreach (var file in Files)
            file.Relocalize();
        if (!IsBusy && !_hasScanned)
            StatusText = Loc.T("largefiles.empty");
    }
}
