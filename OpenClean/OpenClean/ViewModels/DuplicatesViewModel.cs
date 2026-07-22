using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using OpenClean.Contracts;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Duplicates;
using OpenClean.Services.Integrity;
using OpenClean.Services.Licensing;
using OpenClean.Services.Safety;
using OpenClean.Services.SecureDelete;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>Strategie der automatischen Auswahl: welche Kopie je Gruppe BEHALTEN wird.</summary>
public enum DuplicateKeepStrategy { Newest, Oldest, Smallest }

/// <summary>Suchmodus: exakte (SHA-256, kostenlos) oder visuell ähnliche Bilder (Perceptual-Hash, Pro).</summary>
public enum DuplicateScanMode { Exact, Similar }

/// <summary>
/// Bereich „Duplikate" (v0.10.0): findet exakte Duplikate (SHA-256) über einen oder
/// mehrere Ordner hinweg, zeigt die Kopien einer Gruppe nebeneinander zum Vergleich
/// und löscht die manuell oder automatisch markierten Kopien – wobei pro Gruppe
/// immer mindestens eine Kopie erhalten bleibt.
/// </summary>
public sealed class DuplicatesViewModel : ScanViewModelBase
{
    private readonly DuplicateScannerService _scanner = new();
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;

    private string _statusText = "";
    private DuplicateGroupViewModel? _selectedGroup;

    private DuplicateScanMode _scanMode = DuplicateScanMode.Exact;
    private int _threshold;
    private bool _useSecureDelete;
    private int _passes;

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand AutoSelectCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }

    public DuplicatesViewModel(IDialogService? dialogs = null, IUiDispatcher? ui = null)
    {
        _dialogs = dialogs ?? DialogService.Default;
        _ui = ui ?? UiDispatcher.Default;

        AddFolderCommand = new RelayCommand(_ => AddFolders(), _ => !IsBusy);
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy && Folders.Count > 0);
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is string folder) Folders.Remove(folder);
            OnPropertyChanged(nameof(HasFolders));
            ScanCommand.RaiseCanExecuteChanged();
        }, _ => !IsBusy);
        AutoSelectCommand = new RelayCommand(p => AutoSelect(ParseStrategy(p)), _ => !IsBusy && Groups.Count > 0);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => !IsBusy && Groups.Count > 0);
        // !IntegrityState.IsBlocked: bei erkannter Manipulation (OPCL-20) gesperrt. Wichtig,
        // weil hier direkt File.Delete aufgerufen wird – ohne einen der abgesicherten Services.
        DeleteSelectedCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync(),
            _ => !IsBusy && SelectedCount > 0 && !IntegrityState.IsBlocked);

        // Alle busy-abhängigen Commands zentral neu bewerten lassen (CancelCommand ist in der Basis
        // bereits registriert).
        RegisterBusyCommands(AddFolderCommand, RemoveFolderCommand, ScanCommand,
            AutoSelectCommand, ClearSelectionCommand, DeleteSelectedCommand);

        // Aus den Einstellungen übernommene Pro-Optionen (auf gültige Werte normalisiert).
        _threshold = FuzzyScanOptions.NormalizeThreshold(
            SettingsService.Instance.Current.FuzzyDuplicates.SimilarityThreshold);
        _passes = SecureDeleteOptions.NormalizePasses(
            SettingsService.Instance.Current.SecureDelete.Passes);

        // Schloss-Badges der Pro-Features aktuell halten: nach Aktivierung/Widerruf neu bewerten.
        PremiumService.Instance.Changed += (_, _) =>
        {
            void Update()
            {
                OnPropertyChanged(nameof(IsSimilarLocked));
                OnPropertyChanged(nameof(IsSimilarUnlocked));
                OnPropertyChanged(nameof(IsSecureDeleteLocked));
            }
            if (_ui.CheckAccess()) Update();
            else _ui.Post(Update);
        };

        StatusText = Loc.T("duplicates.status.idle");
    }

    // ---- Eigenschaften ------------------------------------------------------

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasFolders => Folders.Count > 0;
    public bool HasGroups => Groups.Count > 0;

    /// <summary>Die im Master-Bereich gewählte Gruppe (rechts nebeneinander verglichen).</summary>
    public DuplicateGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
                OnPropertyChanged(nameof(HasSelectedGroup));
        }
    }

    public bool HasSelectedGroup => SelectedGroup is not null;

    public int SelectedCount => Groups.Sum(g => g.Files.Count(f => f.IsSelected));
    public long SelectedBytes => Groups.Sum(g => g.Files.Where(f => f.IsSelected).Sum(f => f.SizeBytes));

    public string SelectionSummary =>
        Loc.T("duplicates.selection.summary", SelectedCount, ByteFormatter.Format(SelectedBytes));

    // ---- Pro: Ähnliche Bilder (Fuzzy) --------------------------------------

    /// <summary>Aktiver Suchmodus (exakt oder ähnlich).</summary>
    public DuplicateScanMode ScanMode
    {
        get => _scanMode;
        set
        {
            if (SetProperty(ref _scanMode, value))
            {
                OnPropertyChanged(nameof(IsExactMode));
                OnPropertyChanged(nameof(IsSimilarMode));
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True, wenn der exakte Modus aktiv ist (für die TwoWay-Modusauswahl in der View).</summary>
    public bool IsExactMode
    {
        get => ScanMode == DuplicateScanMode.Exact;
        set { if (value) ScanMode = DuplicateScanMode.Exact; }
    }

    /// <summary>True, wenn der Ähnlichkeits-Modus aktiv ist.</summary>
    public bool IsSimilarMode
    {
        get => ScanMode == DuplicateScanMode.Similar;
        set { if (value) ScanMode = DuplicateScanMode.Similar; }
    }

    /// <summary>Ob die Ähnlichkeitssuche mangels Lizenz gesperrt ist (Pro-Schloss).</summary>
    public bool IsSimilarLocked =>
        !PremiumService.Instance.HasFeature(PremiumContract.FeatureFuzzyDuplicates);

    /// <summary>Umkehrung von <see cref="IsSimilarLocked"/> (für IsEnabled-Bindungen in der View).</summary>
    public bool IsSimilarUnlocked => !IsSimilarLocked;

    /// <summary>Ähnlichkeitsschwelle (50–100); wird in den Einstellungen persistiert.</summary>
    public int SimilarityThreshold
    {
        get => _threshold;
        set
        {
            if (SetProperty(ref _threshold, value))
            {
                SettingsService.Instance.Current.FuzzyDuplicates.SimilarityThreshold = value;
                SettingsService.Instance.Save();
            }
        }
    }

    // ---- Pro: Sicheres Löschen ---------------------------------------------

    /// <summary>Ob sicheres Löschen mangels Lizenz gesperrt ist (Pro-Schloss).</summary>
    public bool IsSecureDeleteLocked =>
        !PremiumService.Instance.HasFeature(PremiumContract.FeatureSecureDelete);

    /// <summary>Ob der nächste Löschvorgang sicher (mehrfaches Überschreiben) erfolgen soll.</summary>
    public bool UseSecureDelete
    {
        get => _useSecureDelete;
        set => SetProperty(ref _useSecureDelete, value);
    }

    /// <summary>Anzahl der Überschreib-Durchläufe {1,3,7}; wird in den Einstellungen persistiert.</summary>
    public int SecureDeletePasses
    {
        get => _passes;
        set
        {
            if (SetProperty(ref _passes, SecureDeleteOptions.NormalizePasses(value)))
            {
                SettingsService.Instance.Current.SecureDelete.Passes = _passes;
                SettingsService.Instance.Save();
                OnPropertyChanged(nameof(IsPasses1));
                OnPropertyChanged(nameof(IsPasses3));
                OnPropertyChanged(nameof(IsPasses7));
            }
        }
    }

    // Durchlauf-Auswahl als drei einander ausschließende Schalter (ohne Extra-Konverter).
    public bool IsPasses1 { get => SecureDeletePasses == 1; set { if (value) SecureDeletePasses = 1; } }
    public bool IsPasses3 { get => SecureDeletePasses == 3; set { if (value) SecureDeletePasses = 3; } }
    public bool IsPasses7 { get => SecureDeletePasses == 7; set { if (value) SecureDeletePasses = 7; } }

    // ---- Ordnerauswahl ------------------------------------------------------

    /// <summary>Fügt per Systemdialog einen oder mehrere Ordner hinzu (Mehrfachauswahl).</summary>
    private void AddFolders()
    {
        var folders = _dialogs.PickFolders(Loc.T("duplicates.action.addFolder"));

        foreach (var folder in folders)
        {
            if (!Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                Folders.Add(folder);
        }
        OnPropertyChanged(nameof(HasFolders));
        ScanCommand.RaiseCanExecuteChanged();
    }

    // ---- Scan ---------------------------------------------------------------

    private async Task ScanAsync()
    {
        // Pro-Gate: Der Ähnlich-Modus ist lizenzpflichtig. Vor jeglicher Arbeit freischalten (Upsell).
        // Bleibt es nach dem Upsell gesperrt, ohne frisch aktivierte Lizenz abbrechen.
        if (ScanMode == DuplicateScanMode.Similar &&
            PremiumGate.StillLockedAfterUpsell(_dialogs, () => IsSimilarLocked, "premium.duplicates.locked"))
            return;

        var progress = new Progress<DuplicateScanProgress>(p =>
        {
            StatusText = p.Phase == "collect"
                ? Loc.T("duplicates.status.collecting", p.Done)
                : Loc.T("duplicates.status.hashing", p.Done, p.Total);
        });

        var folders = Folders.ToList();

        await RunCancellableAsync(async ct =>
        {
            Groups.Clear();
            SelectedGroup = null;
            OnPropertyChanged(nameof(HasGroups));
            RefreshSelection();

            if (ScanMode == DuplicateScanMode.Similar)
            {
                // Ausführung ausschließlich im signierten Modul – die offene App delegiert nur.
                if (PremiumService.Instance.Module is not IFuzzyDuplicateRunner runner)
                {
                    StatusText = Loc.T("premium.error.moduleLoad");
                    return;
                }

                var options = new FuzzyScanOptions
                {
                    SimilarityThreshold = FuzzyScanOptions.NormalizeThreshold(SimilarityThreshold)
                };
                var found = await runner.ScanAsync(folders, options, progress, ct);

                foreach (var group in found)
                    Groups.Add(BuildFromFuzzy(group));

                StatusText = found.Count == 0
                    ? Loc.T("duplicates.status.none")
                    : Loc.T("duplicates.status.similarSummary",
                        found.Count, found.Sum(g => g.Files.Count));
                SelectedGroup = Groups.FirstOrDefault();
            }
            else
            {
                var found = await Task.Run(() => _scanner.Scan(folders, progress, ct));

                foreach (var group in found)
                    Groups.Add(new DuplicateGroupViewModel(group, RefreshSelection));

                long wasted = found.Sum(g => g.WastedBytes);
                StatusText = found.Count == 0
                    ? Loc.T("duplicates.status.none")
                    : Loc.T("duplicates.status.summary",
                        found.Count, found.Sum(g => g.Files.Count), ByteFormatter.Format(wasted));
                SelectedGroup = Groups.FirstOrDefault();
            }
        }, onCancelled: () => StatusText = Loc.T("duplicates.status.cancelled"));

        OnPropertyChanged(nameof(HasGroups));
        RefreshSelection();
    }

    /// <summary>
    /// Adapter: bildet eine Ähnlichkeits-Gruppe des Moduls auf die bestehende exakte
    /// Präsentation (<see cref="DuplicateGroup"/>/<see cref="DuplicateFile"/>) ab, damit der
    /// Vergleichs- und Löschcode unverändert bleibt. Die niedrigste Ähnlichkeit der Gruppe
    /// wird zur Info in den Gruppen-Summary übernommen; als Gruppengröße dient die größte Kopie
    /// (jene, die per Standard behalten wird).
    /// </summary>
    private DuplicateGroupViewModel BuildFromFuzzy(FuzzyDuplicateGroup fuzzy)
    {
        var files = fuzzy.Files
            .Select(f => new DuplicateFile
            {
                Path = f.Path,
                SizeBytes = f.SizeBytes,
                Created = f.Created,
                Modified = f.Modified
            })
            .ToList();

        long largest = fuzzy.Files.Count > 0 ? fuzzy.Files.Max(f => f.SizeBytes) : 0;
        int similarity = fuzzy.Files.Count > 0 ? fuzzy.Files.Min(f => f.SimilarityScore) : 0;

        var group = new DuplicateGroup
        {
            Hash = "",               // kein Inhalts-Hash bei Ähnlichkeit
            SizeBytes = largest,
            Files = files
        };
        return new DuplicateGroupViewModel(group, RefreshSelection, similarity);
    }

    // ---- Auswahl ------------------------------------------------------------

    private static DuplicateKeepStrategy ParseStrategy(object? parameter) => parameter switch
    {
        string s when Enum.TryParse(s, out DuplicateKeepStrategy strategy) => strategy,
        _ => DuplicateKeepStrategy.Newest
    };

    /// <summary>
    /// Automatische Auswahl: markiert je Gruppe alle Kopien zum Löschen AUSSER der
    /// zu behaltenden (neueste/älteste Änderung bzw. kürzester Pfad bei „Smallest").
    /// </summary>
    private void AutoSelect(DuplicateKeepStrategy strategy)
    {
        foreach (var group in Groups)
        {
            var keep = strategy switch
            {
                DuplicateKeepStrategy.Newest => group.Files.OrderByDescending(f => f.Modified).First(),
                DuplicateKeepStrategy.Oldest => group.Files.OrderBy(f => f.Modified).First(),
                // Inhaltsgleiche Dateien sind gleich groß – „kleinste Kopie" wird als
                // die mit dem kürzesten (am wenigsten verschachtelten) Pfad interpretiert.
                _ => group.Files.OrderBy(f => f.FilePath.Length).ThenBy(f => f.FilePath).First()
            };
            foreach (var file in group.Files)
                file.SetSelected(file != keep);
        }
        RefreshSelection();
    }

    private void ClearSelection()
    {
        foreach (var group in Groups)
            foreach (var file in group.Files)
                file.SetSelected(false);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectionSummary));
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        foreach (var group in Groups)
            group.RefreshSummary();
    }

    // ---- Löschen ------------------------------------------------------------

    private async Task DeleteSelectedAsync()
    {
        // Zweite Verteidigungslinie neben CanExecute: hier wird direkt gelöscht (File.Delete),
        // es gibt also keinen Service, der die Sperre sonst durchsetzen würde.
        if (IntegrityState.IsBlocked)
        {
            _dialogs.ShowWarning(Loc.T("integrity.blocked.action"));
            return;
        }

        int count = SelectedCount;
        long bytes = SelectedBytes;
        if (count == 0) return;

        // Pro-Gate für sicheres Löschen: Bei fehlender Lizenz Upsell; bleibt es gesperrt,
        // fällt der Ablauf auf den normalen (kostenlosen) Löschpfad zurück – nie blockieren.
        if (UseSecureDelete &&
            PremiumGate.StillLockedAfterUpsell(_dialogs, () => IsSecureDeleteLocked, "premium.secureDelete.locked"))
            UseSecureDelete = false;

        bool confirmed = _dialogs.ConfirmThemed(
            Loc.T("duplicates.confirm.body", count, ByteFormatter.Format(bytes)),
            Loc.T("duplicates.confirm.title"),
            Loc.T("duplicates.action.delete"));
        if (!confirmed) return;

        // Secure-Delete-Zweig: bewusst unwiederbringlich, kein Sicherheitsnetz/Backup.
        if (UseSecureDelete)
        {
            await SecureDeleteSelectedAsync(count);
            return;
        }

        // Sicherheitsnetz vorbereiten (Wiederherstellungspunkt + Datei-Backup/Undo).
        // Läuft VOR dem IsBusy-Wechsel, damit ein Abbruch den Zustand unberührt lässt.
        SafetyPreparation prep = await SafetyPrompt.PrepareAsync(_dialogs, "duplicates");
        if (!prep.Proceed)
        {
            StatusText = Loc.T("safety.aborted");
            return;
        }

        IsBusy = true;
        StatusText = Loc.T("duplicates.status.deleting");

        var session = prep.Session;
        var groups = Groups.ToList();
        try
        {
            var (deleted, freed, skipped) = await Task.Run(() => DeleteGroups(groups, session));

            PruneEmptiedGroups(groups);
            StatusText = Loc.T("duplicates.status.deleted", deleted, ByteFormatter.Format(freed)) +
                         (skipped > 0 ? Loc.T("duplicates.status.skipped", skipped) : "");
        }
        finally
        {
            // Manifest IMMER schreiben (auch bei unerwartetem Fehler) – sonst wären bereits gelöschte
            // Dateien ohne Undo und der Ordner würde nie aufgeräumt. Commit ist idempotent.
            prep.Session?.Commit();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Löscht die markierten Kopien aller Gruppen (Hintergrund-Arbeit). Pro Gruppe bleibt über
    /// <see cref="DuplicateDeletionGuard"/> immer mindestens eine Kopie erhalten. Löscht über die
    /// Backup-Sitzung (Undo bevorzugt) oder – ohne Sicherheitsnetz – direkt. Gibt gelöschte Anzahl,
    /// freigegebene Bytes und übersprungene Kopien zurück.
    /// </summary>
    private static (int deleted, long freed, int skipped) DeleteGroups(
        IReadOnlyList<DuplicateGroupViewModel> groups, BackupSession? session)
    {
        int deletedCount = 0, skippedCount = 0;
        long freedBytes = 0;

        foreach (var group in groups)
        {
            var toDelete = DuplicateDeletionGuard.SelectDeletable(
                group.Files, f => f.IsSelected, f => f.Modified, out bool sparedOne);
            if (sparedOne) skippedCount++;

            foreach (var file in toDelete)
            {
                try
                {
                    if (session != null)
                    {
                        // Über das Sicherheitsnetz löschen: Nutzerdaten bevorzugt sichern (Undo).
                        var outcome = session.TryDelete(file.FilePath, false, file.SizeBytes, SafeDeleteStrategy.PreferBackup);
                        if (outcome == SafeDeleteOutcome.Deleted)
                        {
                            deletedCount++;
                            freedBytes += file.SizeBytes;
                            file.MarkDeleted();
                        }
                        else
                        {
                            skippedCount++;
                        }
                    }
                    else
                    {
                        // Fallback ohne Backup (Sicherheitsnetz abgeschaltet): direkt löschen.
                        File.Delete(file.FilePath);
                        deletedCount++;
                        freedBytes += file.SizeBytes;
                        file.MarkDeleted();
                    }
                }
                catch
                {
                    skippedCount++; // gesperrt/kein Zugriff -> überspringen
                }
            }
        }

        return (deletedCount, freedBytes, skippedCount);
    }

    /// <summary>
    /// Entfernt gelöschte Dateien aus den Gruppen; Gruppen mit nur noch einer verbliebenen Kopie
    /// sind keine Duplikate mehr und verschwinden. Aktualisiert danach Auswahl und Zähler.
    /// </summary>
    private void PruneEmptiedGroups(IReadOnlyList<DuplicateGroupViewModel> groups)
    {
        foreach (var group in groups)
        {
            group.RemoveDeleted();
            if (group.Files.Count < 2)
            {
                Groups.Remove(group);
                if (SelectedGroup == group) SelectedGroup = null;
            }
        }
        SelectedGroup ??= Groups.FirstOrDefault();

        OnPropertyChanged(nameof(HasGroups));
        RefreshSelection();
    }

    /// <summary>
    /// Pro-Pfad „Sicheres Löschen": überschreibt die markierten Kopien mehrfach und entfernt sie
    /// unwiderruflich. Kein Sicherheitsnetz (Undo/Backup) – deshalb ein harter Extra-Warndialog.
    /// Das Überschreiben selbst läuft ausschließlich im signierten Modul.
    /// </summary>
    private async Task SecureDeleteSelectedAsync(int count)
    {
        // Harter Extra-Warndialog ZUSÄTZLICH zur normalen Bestätigung.
        bool sure = _dialogs.ConfirmThemed(
            Loc.T("duplicates.secureDelete.warnBody", count),
            Loc.T("duplicates.secureDelete.warnTitle"),
            Loc.T("duplicates.secureDelete.confirm"));
        if (!sure) return;

        // Ausführung ausschließlich im signierten Modul – die offene App delegiert nur.
        if (PremiumService.Instance.Module is not ISecureDeleteRunner sdRunner)
        {
            StatusText = Loc.T("premium.error.moduleLoad");
            return;
        }

        // Zu löschende Kopien sammeln: „mind. eine Kopie behalten"-Guard je Gruppe,
        // danach defensiv über PathSafety filtern (nicht löschbare überspringen).
        var groups = Groups.ToList();
        var targets = new List<DuplicateFileViewModel>();
        foreach (var group in groups)
        {
            var toDelete = DuplicateDeletionGuard.SelectDeletable(
                group.Files, f => f.IsSelected, f => f.Modified, out _);
            foreach (var file in toDelete)
                if (PathSafety.IsDeletable(file.FilePath))
                    targets.Add(file);
        }

        var paths = targets.Select(f => f.FilePath).ToList();

        await RunCancellableAsync(async ct =>
        {
            StatusText = Loc.T("duplicates.status.deleting");

            var progress = new Progress<SecureDeleteProgress>(p =>
                StatusText = Loc.T("duplicates.secureDelete.status",
                    p.FileIndex, p.FileCount, p.CurrentPass, p.PassCount));

            var result = await sdRunner.SecureDeleteAsync(
                paths, new SecureDeleteOptions { Passes = SecureDeletePasses }, progress, ct);

            // Erfolgreich gelöschte Kopien aus den Gruppen entfernen (FailedPaths ausnehmen).
            var failedSet = new HashSet<string>(result.FailedPaths, StringComparer.OrdinalIgnoreCase);
            long freed = 0;
            foreach (var file in targets)
            {
                if (failedSet.Contains(file.FilePath)) continue;
                file.MarkDeleted();
                freed += file.SizeBytes;
            }

            PruneEmptiedGroups(groups);
            StatusText = Loc.T("duplicates.status.deleted", result.DeletedCount, ByteFormatter.Format(freed)) +
                         (result.FailedCount > 0 ? Loc.T("duplicates.status.skipped", result.FailedCount) : "");
        }, onCancelled: () => StatusText = Loc.T("duplicates.status.cancelled"));
    }

    // ---- Lokalisierung ------------------------------------------------------

    public void Relocalize()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        foreach (var group in Groups)
            group.Relocalize();
        if (!IsBusy)
            StatusText = Groups.Count > 0
                ? Loc.T("duplicates.status.summary",
                    Groups.Count, Groups.Sum(g => g.Files.Count),
                    ByteFormatter.Format(Groups.Sum(g => g.Model.WastedBytes)))
                : Loc.T("duplicates.status.idle");
    }
}

/// <summary>Eine Duplikat-Gruppe in der Liste (links) inkl. Auswahlzustand ihrer Dateien.</summary>
public sealed class DuplicateGroupViewModel : ViewModelBase
{
    /// <summary>Ähnlichkeit der lockersten Kopie (0–100) im Ähnlich-Modus; null im exakten Modus.</summary>
    private readonly int? _similarityScore;

    public DuplicateGroup Model { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; } = new();

    public DuplicateGroupViewModel(DuplicateGroup model, Action onSelectionChanged, int? similarityScore = null)
    {
        Model = model;
        _similarityScore = similarityScore;
        foreach (var file in model.Files)
            Files.Add(new DuplicateFileViewModel(file, onSelectionChanged));
    }

    /// <summary>Anzeigename der Gruppe: Dateiname der ersten Kopie.</summary>
    public string Name => Path.GetFileName(Files.Count > 0 ? Files[0].FilePath : Model.Files[0].Path);

    public string SizeDisplay => ByteFormatter.Format(Model.SizeBytes);

    public string Summary => _similarityScore is int score
        ? Loc.T("duplicates.group.summarySimilar",
            Files.Count, ByteFormatter.Format(Model.SizeBytes), score)
        : Loc.T("duplicates.group.summary",
            Files.Count, ByteFormatter.Format(Model.SizeBytes));

    public int SelectedCount => Files.Count(f => f.IsSelected);

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
    }

    public void RemoveDeleted()
    {
        foreach (var file in Files.Where(f => f.IsDeleted).ToList())
            Files.Remove(file);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Summary));
        foreach (var file in Files)
            file.Relocalize();
    }
}

/// <summary>Eine einzelne Kopie innerhalb einer Gruppe (Vergleichskarte rechts).</summary>
public sealed class DuplicateFileViewModel : ViewModelBase
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"
    };

    private readonly DuplicateFile _model;
    private readonly Action _onSelectionChanged;
    private bool _isSelected;

    public DuplicateFileViewModel(DuplicateFile model, Action onSelectionChanged)
    {
        _model = model;
        _onSelectionChanged = onSelectionChanged;
    }

    public string FilePath => _model.Path;
    public string FileName => Path.GetFileName(_model.Path);
    public string Directory => Path.GetDirectoryName(_model.Path) ?? "";
    public long SizeBytes => _model.SizeBytes;
    public DateTime Modified => _model.Modified;

    public string SizeDisplay => ByteFormatter.Format(_model.SizeBytes);
    public string ModifiedDisplay => _model.Modified.ToString("dd.MM.yyyy HH:mm");
    public string CreatedDisplay => _model.Created.ToString("dd.MM.yyyy HH:mm");

    /// <summary>Ob diese Kopie zum LÖSCHEN markiert ist.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _onSelectionChanged();
        }
    }

    /// <summary>Setzt die Auswahl programmatisch (Auto-Auswahl) ohne Callback-Kaskade.</summary>
    public void SetSelected(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }

    public bool IsDeleted { get; private set; }
    public void MarkDeleted() => IsDeleted = true;

    private BitmapImage? _previewSource;
    private bool _previewLoaded;

    /// <summary>
    /// Bild-Vorschau (verkleinert) für die Vergleichsansicht; null bei Nicht-Bildern. Das Ergebnis
    /// wird gecacht (eingefrorene <see cref="BitmapImage"/>), damit WPF bei jedem erneuten
    /// Getter-Zugriff (Re-Measure, Container-Recycling) nicht die Datei neu von der Platte dekodiert.
    /// </summary>
    public BitmapImage? PreviewSource
    {
        get
        {
            if (_previewLoaded) return _previewSource;
            _previewLoaded = true;

            if (!ImageExtensions.Contains(Path.GetExtension(_model.Path))) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(_model.Path);
                image.DecodePixelWidth = 260;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                _previewSource = image;
            }
            catch { _previewSource = null; }

            return _previewSource;
        }
    }

    public bool HasPreview => ImageExtensions.Contains(Path.GetExtension(_model.Path));

    public void Relocalize()
    {
        OnPropertyChanged(nameof(SizeDisplay));
    }
}
