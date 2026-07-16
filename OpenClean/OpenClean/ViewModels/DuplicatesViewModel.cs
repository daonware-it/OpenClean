using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;
using OpenClean.Services.Safety;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>Strategie der automatischen Auswahl: welche Kopie je Gruppe BEHALTEN wird.</summary>
public enum DuplicateKeepStrategy { Newest, Oldest, Smallest }

/// <summary>
/// Bereich „Duplikate" (v0.10.0): findet exakte Duplikate (SHA-256) über einen oder
/// mehrere Ordner hinweg, zeigt die Kopien einer Gruppe nebeneinander zum Vergleich
/// und löscht die manuell oder automatisch markierten Kopien – wobei pro Gruppe
/// immer mindestens eine Kopie erhalten bleibt.
/// </summary>
public sealed class DuplicatesViewModel : ViewModelBase
{
    private readonly DuplicateScannerService _scanner = new();

    private bool _isBusy;
    private string _statusText = "";
    private DuplicateGroupViewModel? _selectedGroup;
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AutoSelectCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }

    public DuplicatesViewModel()
    {
        AddFolderCommand = new RelayCommand(_ => AddFolders(), _ => !IsBusy);
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy && Folders.Count > 0);
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is string folder) Folders.Remove(folder);
            OnPropertyChanged(nameof(HasFolders));
            ScanCommand.RaiseCanExecuteChanged();
        }, _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        AutoSelectCommand = new RelayCommand(p => AutoSelect(ParseStrategy(p)), _ => !IsBusy && Groups.Count > 0);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => !IsBusy && Groups.Count > 0);
        // !IntegrityState.IsBlocked: bei erkannter Manipulation (OPCL-20) gesperrt. Wichtig,
        // weil hier direkt File.Delete aufgerufen wird – ohne einen der abgesicherten Services.
        DeleteSelectedCommand = new AsyncRelayCommand(_ => DeleteSelectedAsync(),
            _ => !IsBusy && SelectedCount > 0 && !IntegrityState.IsBlocked);

        StatusText = Loc.T("duplicates.status.idle");
    }

    // ---- Eigenschaften ------------------------------------------------------

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AddFolderCommand.RaiseCanExecuteChanged();
                RemoveFolderCommand.RaiseCanExecuteChanged();
                ScanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                AutoSelectCommand.RaiseCanExecuteChanged();
                ClearSelectionCommand.RaiseCanExecuteChanged();
                DeleteSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

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

    // ---- Ordnerauswahl ------------------------------------------------------

    /// <summary>Fügt per Systemdialog einen oder mehrere Ordner hinzu (Mehrfachauswahl).</summary>
    private void AddFolders()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Multiselect = true,
            Title = Loc.T("duplicates.action.addFolder")
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;

        foreach (var folder in dialog.FolderNames)
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
        IsBusy = true;
        Groups.Clear();
        SelectedGroup = null;
        OnPropertyChanged(nameof(HasGroups));
        RefreshSelection();

        _cts = new CancellationTokenSource();
        var progress = new Progress<DuplicateScanProgress>(p =>
        {
            StatusText = p.Phase == "collect"
                ? Loc.T("duplicates.status.collecting", p.Done)
                : Loc.T("duplicates.status.hashing", p.Done, p.Total);
        });

        var folders = Folders.ToList();
        try
        {
            var found = await Task.Run(() => _scanner.Scan(folders, progress, _cts.Token));

            foreach (var group in found)
                Groups.Add(new DuplicateGroupViewModel(group, RefreshSelection));

            long wasted = found.Sum(g => g.WastedBytes);
            StatusText = found.Count == 0
                ? Loc.T("duplicates.status.none")
                : Loc.T("duplicates.status.summary",
                    found.Count, found.Sum(g => g.Files.Count), ByteFormatter.Format(wasted));
            SelectedGroup = Groups.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.T("duplicates.status.cancelled");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            OnPropertyChanged(nameof(HasGroups));
            RefreshSelection();
        }
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
            MessageBox.Show(Loc.T("integrity.blocked.action"), "OpenClean",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int count = SelectedCount;
        long bytes = SelectedBytes;
        if (count == 0) return;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("duplicates.confirm.body", count, ByteFormatter.Format(bytes)),
            Loc.T("duplicates.confirm.title"),
            Loc.T("duplicates.action.delete"));
        if (!confirmed) return;

        // Sicherheitsnetz vorbereiten (Wiederherstellungspunkt + Datei-Backup/Undo).
        // Läuft VOR dem IsBusy-Wechsel, damit ein Abbruch den Zustand unberührt lässt.
        SafetyPreparation prep = await SafetyPrompt.PrepareAsync(Application.Current?.MainWindow, "duplicates");
        if (!prep.Proceed)
        {
            StatusText = Loc.T("safety.aborted");
            return;
        }

        IsBusy = true;
        StatusText = Loc.T("duplicates.status.deleting");

        var session = prep.Session;
        var groups = Groups.ToList();
        int deleted = 0, skipped = 0;
        long freed = 0;
        try
        {
        (deleted, freed, skipped) = await Task.Run(() =>
        {
            int deletedCount = 0, skippedCount = 0;
            long freedBytes = 0;
            foreach (var group in groups)
            {
                // Sicherheits-Guard: pro Gruppe bleibt immer mindestens eine Kopie
                // erhalten – auch wenn manuell alle markiert wurden.
                var selected = group.Files.Where(f => f.IsSelected).ToList();
                if (selected.Count == group.Files.Count)
                {
                    var spared = selected.OrderByDescending(f => f.Modified).First();
                    selected.Remove(spared);
                    skippedCount++;
                }

                foreach (var file in selected)
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
        });

        // Gelöschte Dateien aus den Gruppen entfernen; Gruppen mit nur noch einer
        // verbliebenen Kopie sind keine Duplikate mehr.
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
    public DuplicateGroup Model { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; } = new();

    public DuplicateGroupViewModel(DuplicateGroup model, Action onSelectionChanged)
    {
        Model = model;
        foreach (var file in model.Files)
            Files.Add(new DuplicateFileViewModel(file, onSelectionChanged));
    }

    /// <summary>Anzeigename der Gruppe: Dateiname der ersten Kopie.</summary>
    public string Name => Path.GetFileName(Files.Count > 0 ? Files[0].FilePath : Model.Files[0].Path);

    public string SizeDisplay => ByteFormatter.Format(Model.SizeBytes);

    public string Summary => Loc.T("duplicates.group.summary",
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

    /// <summary>Bild-Vorschau (verkleinert) für die Vergleichsansicht; null bei Nicht-Bildern.</summary>
    public BitmapImage? PreviewSource
    {
        get
        {
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
                return image;
            }
            catch { return null; }
        }
    }

    public bool HasPreview => ImageExtensions.Contains(Path.GetExtension(_model.Path));

    public void Relocalize()
    {
        OnPropertyChanged(nameof(SizeDisplay));
    }
}
