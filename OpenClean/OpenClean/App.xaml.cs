using System.Windows;
using OpenClean.Services;
using OpenClean.Services.Localization;

namespace OpenClean;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Unbeaufsichtigter Lauf der geplanten Reinigung: erkennt den --auto-Schalter
        // (vom Windows-Aufgabenplaner gesetzt). Läuft KOMPLETT OHNE Fenster, reinigt
        // ohne Nachfrage, schreibt einen Bericht und beendet sich danach.
        if (IsAutoRun(e.Args))
        {
            RunAutomaticCleanup();
            Shutdown();
            return;
        }

        // Sprache festlegen (gespeicherte Auswahl → Windows-Sprache → Deutsch),
        // bevor das Hauptfenster gerendert wird.
        LocalizationManager.Instance.InitializeStartupLanguage();

        // Beim Start das gespeicherte Theme (settings.json) anwenden, sonst das
        // Windows-App-Modus-Theme (Hell/Dunkel) – bevor das Hauptfenster gerendert wird.
        ThemeService.ApplyTheme(ThemeService.DetectStartupTheme());

        // Hauptfenster explizit erzeugen (StartupUri wurde bewusst entfernt).
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>True, wenn ein Argument dem Auto-Schalter entspricht (case-insensitiv, auch /auto).</summary>
    private static bool IsAutoRun(IEnumerable<string> args)
        => args.Any(a =>
            a.Equals(ScheduleTaskService.AutoSwitch, StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Führt die geplante Reinigung anhand der gespeicherten Einstellungen aus, speichert
    /// den Bericht und zeigt – falls aktiviert – eine Windows-Benachrichtigung. Jeder
    /// Schritt ist gekapselt, damit ein Teilfehler den Lauf nicht abbricht.
    /// </summary>
    private static void RunAutomaticCleanup()
    {
        try
        {
            var schedule = SettingsService.Instance.Current.Schedule;

            var result = new AutoCleanService().Run(schedule);
            var report = new AutoCleanReportStore().Add(schedule.Profile, result, DateTime.Now);

            if (schedule.Notify)
            {
                string title = Loc.T("schedule.toast.title");
                string message = Loc.T("schedule.toast.body",
                    report.DeletedCount, ByteFormatter.Format(report.FreedBytes));
                new ToastService().Show(title, message);
            }
        }
        catch
        {
            // Ein unbeaufsichtigter Lauf darf niemals mit einem Fehlerdialog hängen bleiben.
        }
    }
}
