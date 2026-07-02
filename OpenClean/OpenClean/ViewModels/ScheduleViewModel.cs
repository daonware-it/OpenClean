using System.Collections.ObjectModel;
using System.Windows;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>Häufigkeit der geplanten Reinigung (bindet über EnumToBoolConverter an RadioButtons).</summary>
public enum ScheduleFrequency { Daily, Weekly, Monthly }

/// <summary>Reinigungsprofil der Automatik.</summary>
public enum CleanProfile { TempOnly, Full, Custom }

/// <summary>
/// Bereich „Zeitplan": konfiguriert die geplante automatische Reinigung (v0.8.0).
/// Schreibt beim Übernehmen die Windows-Aufgabenplaner-Aufgabe (nur wenn aktiviert),
/// zeigt die Berichte vergangener Läufe und warnt deutlich, dass die automatische
/// Reinigung OHNE Rückfrage löscht.
/// </summary>
public sealed class ScheduleViewModel : ViewModelBase
{
    private readonly ScheduleTaskService _taskService = new();
    private readonly AutoCleanReportStore _reportStore = new();

    private bool _isEnabled;
    private bool _notify = true;
    private ScheduleFrequency _frequency = ScheduleFrequency.Weekly;
    private CleanProfile _profile = CleanProfile.TempOnly;
    private int _hour = 20;
    private int _minute;
    private int _selectedWeekdayIndex; // 0 = Montag … 6 = Sonntag
    private int _dayOfMonth = 1;
    private bool _isBusy;
    private string _statusText = "";

    public ObservableCollection<ScheduleCategoryOption> Categories { get; } = new();
    public ObservableCollection<ScheduleReportItem> Reports { get; } = new();

    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 60).ToList();
    public IReadOnlyList<int> DaysOfMonth { get; } = Enumerable.Range(1, 28).ToList();

    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RunNowCommand { get; }

    public ScheduleViewModel()
    {
        // Kategorien (für „Benutzerdefiniert") aus demselben Scanner wie die Bereinigung.
        foreach (var category in new TempScannerService().CreateCategories())
            Categories.Add(new ScheduleCategoryOption(category.Key));

        LoadFromSettings();

        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => !IsBusy);
        RunNowCommand = new AsyncRelayCommand(_ => RunNowAsync(), _ => !IsBusy);

        LoadReports();
        UpdateInitialStatus();
    }

    // ---- Zustand ------------------------------------------------------------

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool Notify
    {
        get => _notify;
        set => SetProperty(ref _notify, value);
    }

    public ScheduleFrequency Frequency
    {
        get => _frequency;
        set
        {
            if (SetProperty(ref _frequency, value))
            {
                OnPropertyChanged(nameof(ShowWeekly));
                OnPropertyChanged(nameof(ShowMonthly));
            }
        }
    }

    public CleanProfile Profile
    {
        get => _profile;
        set
        {
            if (SetProperty(ref _profile, value))
                OnPropertyChanged(nameof(ShowCustom));
        }
    }

    public int Hour { get => _hour; set => SetProperty(ref _hour, value); }
    public int Minute { get => _minute; set => SetProperty(ref _minute, value); }
    public int DayOfMonth { get => _dayOfMonth; set => SetProperty(ref _dayOfMonth, value); }

    public int SelectedWeekdayIndex
    {
        get => _selectedWeekdayIndex;
        set => SetProperty(ref _selectedWeekdayIndex, value);
    }

    /// <summary>Wochentagsnamen in Anzeigereihenfolge (Montag zuerst), lokalisiert.</summary>
    public IReadOnlyList<string> WeekdayNames => new[]
    {
        Loc.T("schedule.weekday.mon"), Loc.T("schedule.weekday.tue"), Loc.T("schedule.weekday.wed"),
        Loc.T("schedule.weekday.thu"), Loc.T("schedule.weekday.fri"), Loc.T("schedule.weekday.sat"),
        Loc.T("schedule.weekday.sun")
    };

    public bool ShowWeekly => Frequency == ScheduleFrequency.Weekly;
    public bool ShowMonthly => Frequency == ScheduleFrequency.Monthly;
    public bool ShowCustom => Profile == CleanProfile.Custom;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
                RunNowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasReports => Reports.Count > 0;

    // ---- Übernehmen / Ausführen --------------------------------------------

    private async Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("schedule.status.applying");

        var schedule = BuildSettings();
        SettingsService.Instance.Current.Schedule = schedule;
        SettingsService.Instance.Save();

        // schtasks im Hintergrund ausführen (Prozessstart blockiert kurz).
        bool ok = await Task.Run(() => _taskService.Apply(schedule));

        StatusText = ok
            ? (schedule.Enabled ? Loc.T("schedule.status.registered") : Loc.T("schedule.status.disabled"))
            : Loc.T("schedule.status.error");

        IsBusy = false;
    }

    /// <summary>
    /// Führt die Reinigung sofort einmal aus (manueller Test). Anders als der geplante
    /// Lauf fragt dieser manuell ausgelöste Test SICHERHEITSHALBER nach – der geplante
    /// Lauf selbst löscht ohne Rückfrage.
    /// </summary>
    private async Task RunNowAsync()
    {
        var schedule = BuildSettings();

        bool confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            Loc.T("schedule.runNow.confirm"),
            Loc.T("schedule.runNow.title"),
            Loc.T("schedule.runNow.action"));
        if (!confirmed) return;

        IsBusy = true;
        StatusText = Loc.T("schedule.status.running");

        var report = await Task.Run(() =>
        {
            var result = new AutoCleanService().Run(schedule);
            return _reportStore.Add(schedule.Profile, result, DateTime.Now);
        });

        LoadReports();
        StatusText = Loc.T("schedule.status.ranNow",
            report.DeletedCount, ByteFormatter.Format(report.FreedBytes));
        IsBusy = false;
    }

    // ---- Laden / Speichern --------------------------------------------------

    private void LoadFromSettings()
    {
        var s = SettingsService.Instance.Current.Schedule;
        _isEnabled = s.Enabled;
        _notify = s.Notify;
        _frequency = Enum.TryParse(s.Frequency, out ScheduleFrequency f) ? f : ScheduleFrequency.Weekly;
        _profile = Enum.TryParse(s.Profile, out CleanProfile p) ? p : CleanProfile.TempOnly;
        _hour = Math.Clamp(s.Hour, 0, 23);
        _minute = Math.Clamp(s.Minute, 0, 59);
        _dayOfMonth = Math.Clamp(s.DayOfMonth, 1, 28);
        _selectedWeekdayIndex = (Math.Clamp(s.DayOfWeek, 0, 6) + 6) % 7; // DayOfWeek(0=So) -> Index(0=Mo)

        var selected = new HashSet<string>(s.CustomCategoryKeys, StringComparer.Ordinal);
        foreach (var option in Categories)
            option.IsSelected = selected.Contains(option.Key);
    }

    private ScheduleSettings BuildSettings() => new()
    {
        Enabled = IsEnabled,
        Notify = Notify,
        Frequency = Frequency.ToString(),
        Profile = Profile.ToString(),
        Hour = Hour,
        Minute = Minute,
        DayOfMonth = DayOfMonth,
        DayOfWeek = (SelectedWeekdayIndex + 1) % 7, // Index(0=Mo) -> DayOfWeek(0=So)
        CustomCategoryKeys = Categories.Where(c => c.IsSelected).Select(c => c.Key).ToList()
    };

    private void LoadReports()
    {
        Reports.Clear();
        foreach (var report in _reportStore.Load())
            Reports.Add(new ScheduleReportItem(report));
        OnPropertyChanged(nameof(HasReports));
    }

    private void UpdateInitialStatus()
    {
        bool registered = _taskService.IsRegistered();
        StatusText = registered ? Loc.T("schedule.status.registered") : Loc.T("schedule.status.inactive");
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel alle berechneten Texte.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(WeekdayNames));
        foreach (var option in Categories)
            option.RefreshName();
        LoadReports();
        UpdateInitialStatus();
    }
}

/// <summary>Auswählbare Kategorie im „Benutzerdefiniert"-Profil (Schlüssel + lokalisierter Name).</summary>
public sealed class ScheduleCategoryOption : ViewModelBase
{
    public string Key { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ScheduleCategoryOption(string key) => Key = key;

    public string Name => Loc.T($"{Key}.name");

    public void RefreshName() => OnPropertyChanged(nameof(Name));
}

/// <summary>Anzeige-Wrapper eines gespeicherten automatischen Reinigungsberichts.</summary>
public sealed class ScheduleReportItem
{
    private readonly AutoCleanReport _report;

    public ScheduleReportItem(AutoCleanReport report) => _report = report;

    public string When => _report.Timestamp.ToString("dd.MM.yyyy HH:mm");

    public string ProfileName => _report.Profile switch
    {
        "Full" => Loc.T("schedule.profile.full"),
        "Custom" => Loc.T("schedule.profile.custom"),
        _ => Loc.T("schedule.profile.tempOnly")
    };

    public string Summary => Loc.T("schedule.report.summary",
        _report.DeletedCount, ByteFormatter.Format(_report.FreedBytes)) +
        (_report.SkippedCount > 0 ? Loc.T("schedule.report.skipped", _report.SkippedCount) : "");

    public IReadOnlyList<string> DeletedPaths => _report.DeletedPaths;

    public bool HasPaths => _report.DeletedPaths.Count > 0;

    public bool PathsTruncated => _report.PathsTruncated;

    public string TruncatedNote => Loc.T("schedule.report.truncated");
}
