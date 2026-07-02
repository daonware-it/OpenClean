using System.Collections.ObjectModel;
using System.Windows;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>
/// Agent 2: Lädt verfügbare App-Updates über winget und erlaubt einzelnes bzw. gebündeltes Aktualisieren.
/// </summary>
public sealed class UpdaterViewModel : ViewModelBase
{
    private readonly WingetService _service = new();

    private bool _isBusy;
    private bool _wingetAvailable = true;
    private string _statusText = Loc.T("updater.status.loading");
    private double _progressPercent;
    private bool _progressIsIndeterminate = true;
    private string _progressText = "";

    public ObservableCollection<UpdaterItemViewModel> Updates { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand UpdateAllCommand { get; }

    public UpdaterViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        UpdateAllCommand = new AsyncRelayCommand(_ => UpdateAllAsync(), _ => !IsBusy && Updates.Count > 0);

        _ = LoadAsync();
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                UpdateAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>False, wenn winget auf dem System nicht verfügbar ist (Hinweis in der View).</summary>
    public bool WingetAvailable
    {
        get => _wingetAvailable;
        private set => SetProperty(ref _wingetAvailable, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Fortschritt des laufenden Sammel-Updates in Prozent (0–100) für die ProgressBar.</summary>
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    /// <summary>
    /// True, wenn der Fortschritt nicht in Prozent bezifferbar ist (Scan bzw. Einzel-Update →
    /// laufende Marquee-Animation); false beim Sammel-Update (determinierter Balken pro App).
    /// </summary>
    public bool ProgressIsIndeterminate
    {
        get => _progressIsIndeterminate;
        private set => SetProperty(ref _progressIsIndeterminate, value);
    }

    /// <summary>Text unter der ProgressBar, z. B. „Aktualisiere 2/5: App“.</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        ProgressIsIndeterminate = true; // Scan hat keinen bezifferbaren Fortschritt → Marquee.
        StatusText = Loc.T("updater.status.loading");
        ProgressText = "";
        ProgressPercent = 0;

        WingetAvailable = await Task.Run(() => _service.IsAvailable());
        if (!WingetAvailable)
        {
            Updates.Clear();
            StatusText = Loc.T("updater.status.wingetMissing");
            IsBusy = false;
            UpdateAllCommand.RaiseCanExecuteChanged();
            return;
        }

        var apps = await Task.Run(() => _service.GetUpgradable());

        // ObservableCollection nur auf dem UI-Thread anfassen.
        Updates.Clear();
        foreach (var app in apps.OrderBy(a => a.Name))
            Updates.Add(new UpdaterItemViewModel(app, UpdateOneAsync));

        IsBusy = false;
        StatusText = Updates.Count == 0
            ? Loc.T("updater.status.none")
            : Loc.T("updater.status.summary", Updates.Count);
        UpdateAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Aktualisiert genau ein Programm: Bestätigung, laufender Fortschrittsbalken (Marquee, da
    /// winget beim Einzel-Update keinen Prozentwert liefert) und anschließend ein Neu-Scan,
    /// damit die Liste den echten Zustand widerspiegelt.
    /// </summary>
    private async Task UpdateOneAsync(UpdaterItemViewModel item)
    {
        // Schutz vor parallelen Updates (ein anderer Vorgang läuft bereits).
        if (IsBusy) return;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("updater.confirm.single", item.Name, item.CurrentVersion, item.AvailableVersion),
            Loc.T("updater.confirm.title"),
            Loc.T("updater.action.update"));

        if (!confirmed) return;

        IsBusy = true;
        ProgressIsIndeterminate = true;
        ProgressPercent = 0;
        StatusText = Loc.T("updater.status.updating", item.Name);
        ProgressText = "";

        bool ok = await _service.UpgradeAsync(item.Model);

        // Danach frisch scannen, damit Versionen/verbleibende Updates korrekt sind.
        await LoadAsync();

        ProgressText = "";
        ProgressPercent = 0;

        if (!ok)
            ReportError(Loc.T("updater.error.failed", item.Name, ""));
    }

    private async Task UpdateAllAsync()
    {
        if (Updates.Count == 0) return;

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("updater.confirm.all", Updates.Count),
            Loc.T("updater.confirm.title"),
            Loc.T("updater.action.updateAll"));

        if (!confirmed) return;

        IsBusy = true;
        ProgressIsIndeterminate = false; // Sammel-Update: determinierter Balken (Fortschritt pro App).
        ProgressPercent = 0;

        var snapshot = Updates.Select(u => u.Model).ToList();

        // IProgress auf dem UI-Thread erzeugt → Callbacks marshallen automatisch zurück.
        var progress = new Progress<UpdateProgress>(p =>
        {
            ProgressPercent = p.Percent;
            ProgressText = Loc.T("updater.status.updatingAll", p.Done, p.Total, p.CurrentApp);
        });

        await _service.UpgradeAllAsync(snapshot, progress);

        // Frisch laden, damit die Liste den echten verbleibenden Zustand zeigt.
        await LoadAsync();

        ProgressText = "";
        ProgressPercent = 0;
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel die Statuszeile.</summary>
    public void Relocalize()
    {
        if (!IsBusy)
        {
            StatusText = !WingetAvailable
                ? Loc.T("updater.status.wingetMissing")
                : Updates.Count == 0
                    ? Loc.T("updater.status.none")
                    : Loc.T("updater.status.summary", Updates.Count);
        }
    }

    private static void ReportError(string message)
        => MessageBox.Show(message, "OpenClean", MessageBoxButton.OK, MessageBoxImage.Warning);
}
