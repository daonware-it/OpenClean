using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Safety;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>
/// „Wiederherstellen"-Bereich: zeigt die vorhandenen Windows-Wiederherstellungspunkte sowie die
/// gesicherten Bereinigungs-Durchläufe aller Bereiche und erlaubt, einzelne Durchläufe rückgängig zu
/// machen oder ihre Sicherung zu verwerfen. Enthält außerdem die Schalter für die beiden
/// Sicherheitsnetze (Wiederherstellungspunkt &amp; Backup).
///
/// <para>Die beiden Sicherheitsnetze sind unabhängig voneinander: Wiederherstellungspunkte entstehen
/// auch bei abgeschaltetem Datei-Backup. Deshalb stehen sie in einem EIGENEN Abschnitt und nicht in
/// der Liste der Backup-Durchläufe – sonst wirkte ein Lauf ohne Datei-Backup so, als sei gar nichts
/// gesichert worden.</para>
/// </summary>
public sealed class BackupHistoryViewModel : ViewModelBase
{
    private bool _isBusy;
    private string _statusText = "";

    public ObservableCollection<BackupHistoryItemViewModel> Sessions { get; } = new();

    /// <summary>Vorhandene Windows-Wiederherstellungspunkte (jüngster zuerst).</summary>
    public ObservableCollection<RestorePointItemViewModel> RestorePoints { get; } = new();

    public RelayCommand RefreshCommand { get; }

    /// <summary>Öffnet die Windows-Systemwiederherstellung (rstrui.exe).</summary>
    public RelayCommand OpenSystemRestoreCommand { get; }

    /// <summary>Entfernt alle Wiederherstellungspunkte bis auf den jüngsten.</summary>
    public RelayCommand DeleteOldRestorePointsCommand { get; }

    public BackupHistoryViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh(), _ => !IsBusy);
        OpenSystemRestoreCommand = new RelayCommand(_ => OpenSystemRestore());
        DeleteOldRestorePointsCommand = new RelayCommand(
            _ => DeleteOldRestorePoints(), _ => !IsBusy && HasDeletableRestorePoints);
        Refresh();
    }

    /// <summary>Lädt die Liste der gesicherten Durchläufe neu und stößt das Laden der Punkte an.</summary>
    public void Refresh()
    {
        Sessions.Clear();
        foreach (var manifest in BackupService.Instance.ListSessions())
        {
            string id = manifest.Id;
            var restore = new RelayCommand(_ => RestoreSession(id), _ => !IsBusy);
            var delete = new RelayCommand(_ => DeleteSession(id), _ => !IsBusy);
            Sessions.Add(new BackupHistoryItemViewModel(manifest, restore, delete));
        }

        OnPropertyChanged(nameof(HasSessions));
        StatusText = Sessions.Count == 0
            ? Loc.T("safety.history.empty")
            : Loc.T("safety.history.count", Sessions.Count);

        _ = RefreshRestorePointsAsync();
    }

    /// <summary>
    /// Lädt die Wiederherstellungspunkte nach. Der WMI-Zugriff kann spürbar dauern, deshalb läuft er
    /// im Hintergrund – die Liste der Backup-Durchläufe steht sofort.
    /// </summary>
    private async Task RefreshRestorePointsAsync()
    {
        IReadOnlyList<RestorePointInfo> points =
            await Task.Run(() => RestorePointService.Instance.List());

        RestorePoints.Clear();
        foreach (var p in points)
        {
            uint seq = p.SequenceNumber;
            var delete = new RelayCommand(_ => DeleteRestorePoint(seq), _ => !IsBusy);
            RestorePoints.Add(new RestorePointItemViewModel(p, delete));
        }

        OnPropertyChanged(nameof(HasRestorePoints));
        OnPropertyChanged(nameof(HasDeletableRestorePoints));
        OnPropertyChanged(nameof(RestorePointsStatusText));
        DeleteOldRestorePointsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>True, wenn es außer dem jüngsten noch weitere Punkte gibt (nur dann lohnt die Sammelaktion).</summary>
    public bool HasDeletableRestorePoints => RestorePoints.Count > 1;

    /// <summary>True, wenn mindestens ein Wiederherstellungspunkt vorhanden ist.</summary>
    public bool HasRestorePoints => RestorePoints.Count > 0;

    /// <summary>
    /// Hinweistext des Punkte-Abschnitts. Ohne Punkte wird zwischen „Systemwiederherstellung ist
    /// abgeschaltet" (dann nützt auch der Schalter oben nichts) und „einfach noch keine da"
    /// unterschieden – sonst rätselt der Nutzer, warum die Liste leer bleibt.
    /// </summary>
    public string RestorePointsStatusText
    {
        get
        {
            if (HasRestorePoints) return Loc.T("safety.points.count", RestorePoints.Count);

            return RestorePointService.IsSystemRestoreEnabled()
                ? Loc.T("safety.points.empty")
                : Loc.T("safety.points.disabled");
        }
    }

    /// <summary>True, wenn mindestens ein gesicherter Durchlauf vorhanden ist.</summary>
    public bool HasSessions => Sessions.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                DeleteOldRestorePointsCommand.RaiseCanExecuteChanged();
                foreach (var s in Sessions)
                {
                    s.RestoreCommand.RaiseCanExecuteChanged();
                    s.DeleteCommand.RaiseCanExecuteChanged();
                }
                foreach (var p in RestorePoints)
                    p.DeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ---- Sicherheits-Einstellungen (Schalter) -------------------------------

    /// <summary>Vor jeder Bereinigung automatisch einen Windows-Wiederherstellungspunkt erstellen.</summary>
    public bool CreateRestorePoint
    {
        get => SettingsService.Instance.Current.Safety.CreateRestorePoint;
        set
        {
            if (SettingsService.Instance.Current.Safety.CreateRestorePoint == value) return;
            SettingsService.Instance.Current.Safety.CreateRestorePoint = value;
            SettingsService.Instance.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Zu löschende Dateien vor dem Löschen sichern (Undo möglich).</summary>
    public bool BackupBeforeDelete
    {
        get => SettingsService.Instance.Current.Safety.BackupBeforeDelete;
        set
        {
            if (SettingsService.Instance.Current.Safety.BackupBeforeDelete == value) return;
            SettingsService.Instance.Current.Safety.BackupBeforeDelete = value;
            SettingsService.Instance.Save();
            OnPropertyChanged();
        }
    }

    // ---- Aktionen -----------------------------------------------------------

    private async void RestoreSession(string id)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = Loc.T("safety.undo.running");

        UndoResult result = await Task.Run(() => BackupService.Instance.Restore(id));

        Refresh();
        IsBusy = false;
        StatusText = result.Failed > 0
            ? Loc.T("safety.undo.partial", result.Restored, result.Failed)
            : Loc.T("safety.undo.done", result.Restored, ByteFormatter.Format(result.RestoredBytes));
    }

    /// <summary>
    /// Startet die Windows-Systemwiederherstellung. Das Zurücksetzen des Systemzustands bleibt
    /// bewusst bei Windows: Es betrifft Registry, Treiber und Systemdateien und verlangt einen
    /// Neustart – das gehört nicht in ein Reinigungswerkzeug (vgl. das Datei-Undo, das nur die von
    /// OpenClean selbst gesicherten Objekte zurückholt).
    /// </summary>
    private void OpenSystemRestore()
    {
        try
        {
            // UseShellExecute: rstrui.exe verlangt Elevation; ohne die Shell schlüge der Start fehl.
            Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            StatusText = Loc.T("safety.points.open.failed");
        }
    }

    /// <summary>Entfernt einen einzelnen Wiederherstellungspunkt nach Rückfrage.</summary>
    private async void DeleteRestorePoint(uint sequenceNumber)
    {
        if (IsBusy) return;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("safety.points.delete.body"),
            Loc.T("safety.points.delete.title"),
            Loc.T("safety.points.delete.action"));
        if (!confirmed) return;

        IsBusy = true;
        bool ok = await Task.Run(() => RestorePointService.Instance.Remove(sequenceNumber));
        IsBusy = false;

        StatusText = ok ? Loc.T("safety.points.delete.done") : Loc.T("safety.points.delete.failed");
        await RefreshRestorePointsAsync();
    }

    /// <summary>Entfernt alle Punkte bis auf den jüngsten nach Rückfrage.</summary>
    private async void DeleteOldRestorePoints()
    {
        if (IsBusy || !HasDeletableRestorePoints) return;

        int count = RestorePoints.Count - 1;
        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("safety.points.deleteOld.body", count),
            Loc.T("safety.points.deleteOld.title"),
            Loc.T("safety.points.deleteOld.action"));
        if (!confirmed) return;

        IsBusy = true;
        (int removed, int failed) = await Task.Run(() => RestorePointService.Instance.RemoveAllButNewest());
        IsBusy = false;

        StatusText = failed > 0
            ? Loc.T("safety.points.deleteOld.partial", removed, failed)
            : Loc.T("safety.points.deleteOld.done", removed);
        await RefreshRestorePointsAsync();
    }

    private void DeleteSession(string id)
    {
        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("safety.history.delete.body"),
            Loc.T("safety.history.delete.title"),
            Loc.T("safety.history.delete.action"));
        if (!confirmed) return;

        BackupService.Instance.DeleteSession(id);
        Refresh();
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel alle berechneten Texte.</summary>
    public void Relocalize()
    {
        foreach (var s in Sessions) s.Relocalize();
        foreach (var p in RestorePoints) p.Relocalize();
        OnPropertyChanged(nameof(RestorePointsStatusText));

        if (!IsBusy)
        {
            StatusText = Sessions.Count == 0
                ? Loc.T("safety.history.empty")
                : Loc.T("safety.history.count", Sessions.Count);
        }
    }
}
