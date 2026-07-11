using System.Windows;
using OpenClean.Contracts;
using OpenClean.Services;
using OpenClean.Services.Licensing;
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
            // Auch ohne UI die gespeicherte Sprache laden – sonst erscheint die
            // Toast-Benachrichtigung immer in der Rückfallsprache (Englisch).
            LocalizationManager.Instance.InitializeStartupLanguage();
            RunAutomaticCleanup();
            Shutdown();
            return;
        }

        // Sprache festlegen (gespeicherte Auswahl → Windows-Sprache → Deutsch),
        // bevor das Hauptfenster gerendert wird.
        LocalizationManager.Instance.InitializeStartupLanguage();

        // Einmalige Grandfathering-Migration (v0.12.0): Installationen, die die geplante
        // Reinigung schon vor der Premium-Einführung nutzten, behalten ihren Zeitplan.
        MigrateScheduleGrandfathering();

        // Beim Start das gespeicherte Theme (settings.json) anwenden, sonst das
        // Windows-App-Modus-Theme (Hell/Dunkel) – bevor das Hauptfenster gerendert wird.
        ThemeService.ApplyTheme(ThemeService.DetectStartupTheme());

        // Hauptfenster explizit erzeugen (StartupUri wurde bewusst entfernt).
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Online-Lizenzprüfung im Hintergrund anstoßen: erneuert das Token (frische
        // 30-Tage-Offline-Frist) bzw. entfernt eine serverseitig widerrufene Lizenz.
        // Ohne vorhandene Lizenz passiert nichts -> Free-Nutzer lösen KEIN Netzwerk aus.
        PremiumService.Instance.EnforceLicenseInBackground();
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
            var settings = SettingsService.Instance.Current;
            var schedule = settings.Schedule;

            // Online-Lizenzprüfung auch im unbeaufsichtigten Lauf: erkennt einen serverseitigen
            // Widerruf, bevor ohne gültige Lizenz gereinigt wird, und erneuert die Offline-Frist.
            // Blockierend (durch das HTTP-Timeout des Clients begrenzt) und nur bei vorhandener
            // Lizenz aktiv; ein Netzwerkfehler bricht den Lauf nicht ab (dann greift die 30-Tage-Frist).
            // WICHTIG: über Task.Run auf einen Hintergrund-Thread legen. Ein direktes
            // GetAwaiter().GetResult() auf dem UI-Thread in OnStartup führt zum Deadlock –
            // die await-Fortsetzung des HTTP-Aufrufs will zurück auf den UI-Thread, dessen
            // Dispatcher-Schleife im --auto-Modus aber noch nicht pumpt (der Lauf blockiert
            // dann unbegrenzt, das HTTP-Timeout greift nicht). Auf dem Threadpool gibt es
            // keinen UI-SynchronizationContext -> die Fortsetzung läuft frei weiter.
            try { Task.Run(() => PremiumService.Instance.EnforceLicenseOnlineAsync()).GetAwaiter().GetResult(); }
            catch { /* Netzwerk-/Serverfehler dürfen den geplanten Lauf nie verhindern. */ }

            // Premium-Prüfung (rein offline über das signierte Lizenz-Token): ohne Lizenz
            // und ohne Grandfathering wird NICHT gereinigt – aber nie stillschweigend:
            // Toast + Protokolleintrag, die geplante Aufgabe bleibt bestehen (der Nutzer
            // entfernt sie bewusst über den gesperrten Zeitplan-Bereich).
            if (!IsScheduledCleanupAllowed(settings))
            {
                new AutoCleanReportStore().LogSkippedRun(
                    Loc.T("schedule.auto.skippedNoLicense"), DateTime.Now);
                new ToastService().Show(
                    Loc.T("schedule.toast.title"),
                    Loc.T("schedule.auto.skippedNoLicense"));
                return;
            }

            var result = new AutoCleanService().Run(schedule);
            var report = new AutoCleanReportStore().Add(schedule, result, DateTime.Now);

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

    /// <summary>
    /// True, wenn der geplante Lauf ausgeführt werden darf: gültige Premium-Lizenz mit
    /// Zeitplan-Feature ODER Grandfathering (Bestandsnutzer aus v0.11; solange die
    /// Migration noch nicht lief, zählt ein aktivierter Alt-Zeitplan ebenfalls).
    /// </summary>
    private static bool IsScheduledCleanupAllowed(AppSettings settings)
        => LicenseService.Instance.HasFeature(PremiumContract.FeatureSchedule) ||
           settings.ScheduleGrandfathered == true ||
           (settings.ScheduleGrandfathered is null && settings.Schedule.Enabled);

    /// <summary>
    /// Setzt das Grandfathering-Flag einmalig: true, wenn die geplante Reinigung vor dem
    /// Update auf v0.12 bereits genutzt wurde (Einstellung aktiv oder Aufgabe registriert).
    /// </summary>
    private static void MigrateScheduleGrandfathering()
    {
        var settings = SettingsService.Instance.Current;
        if (settings.ScheduleGrandfathered is not null) return;

        bool legacy = settings.Schedule.Enabled || new ScheduleTaskService().IsRegistered();
        settings.ScheduleGrandfathered = legacy;
        SettingsService.Instance.Save();
    }
}
