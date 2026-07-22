using System.Windows;
using OpenClean.Contracts;
using OpenClean.Services;
using OpenClean.Services.Integrity;
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

        // Selbstprüfung (OPCL-20) als Allererstes: Ist die eigene EXE unverändert und von
        // DaonWare signiert? Muss VOR dem --auto-Zweig laufen (der löscht unbeaufsichtigt mit
        // Adminrechten) und vor dem Hauptfenster, weil die ViewModels den Zustand beim Erzeugen
        // auslesen – RelayCommand fragt CanExecute nicht selbsttätig erneut ab.
        var integrity = IntegrityGuard.Initialize();

        // Fremde oder nicht vertrauenswürdig signierte Binärdatei: nicht weiterlaufen.
        // Im unbeaufsichtigten Lauf ohne Dialog (der würde ewig hängen bleiben).
        if (integrity == IntegrityAction.Block)
        {
            LocalizationManager.Instance.InitializeStartupLanguage();
            if (!IsAutoRun(e.Args))
            {
                MessageBox.Show(
                    Loc.T("integrity.dialog.body"),
                    Loc.T("integrity.dialog.title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }

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

        // Einmaliger Grandfathering-Sunset: die geplante Reinigung erfordert jetzt durchgehend
        // eine gültige Lizenz. Eine noch aus v0.11 laufende Alt-Aufgabe wird ohne Lizenz sauber
        // entfernt (statt still ins Leere zu feuern) und der Nutzer einmalig informiert.
        SunsetLegacySchedule();

        // Beim Start: gespeicherte Palette + Modifier anwenden, bevor das Hauptfenster rendert.
        var settings = SettingsService.Instance.Current;
        ThemeService.ApplyAll(
            ThemeService.DetectStartupTheme(),
            settings.UseWindowsAccent,
            settings.UseMicaBackdrop,
            settings.ReduceMotion);

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
    /// Führt die geplante Reinigung aus – ausschließlich durch DELEGATION an das signierte
    /// Premium-Modul. Die eigentliche Ausführungslogik existiert nur im geschlossenen
    /// <c>OpenClean.Premium.dll</c>; die offene App kennt sie nicht. Ohne geladenes, gültig
    /// lizenziertes Modul passiert daher NICHTS – aber nie stillschweigend (Toast + Protokoll).
    /// Jeder Schritt ist gekapselt, damit ein Teilfehler den Lauf nicht abbricht.
    /// </summary>
    private static void RunAutomaticCleanup()
    {
        try
        {
            // Manipulierte Binärdatei: NICHT unbeaufsichtigt mit Adminrechten löschen.
            // Nie stillschweigend – Protokolleintrag und Benachrichtigung wie beim Lizenz-Stopp.
            if (IntegrityState.IsBlocked)
            {
                new AutoCleanReportStore().LogSkippedRun(Loc.T("integrity.auto.skipped"), DateTime.Now);
                new ToastService().Show(Loc.T("schedule.toast.title"), Loc.T("integrity.auto.skipped"));
                return;
            }

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

            // Delegation an das Modul: Es verifiziert Lizenz + Zeitplan-Feature selbst, scannt,
            // löscht, schreibt den Bericht und benachrichtigt. Kein Modul (keine gültige Lizenz)
            // ODER Ablehnung -> kein Lauf, aber Toast + Protokoll. Ein bloßer Patch der offenen
            // Quelle nützt nichts: Es gibt hier keinen Ausführungscode, den man freischalten könnte.
            if (PremiumService.Instance.Module is not IScheduledCleanupRunner runner ||
                !runner.RunScheduledCleanup())
            {
                new AutoCleanReportStore().LogSkippedRun(
                    Loc.T("schedule.auto.skippedNoLicense"), DateTime.Now);
                new ToastService().Show(
                    Loc.T("schedule.toast.title"),
                    Loc.T("schedule.auto.skippedNoLicense"));
            }
        }
        catch
        {
            // Ein unbeaufsichtigter Lauf darf niemals mit einem Fehlerdialog hängen bleiben.
        }
    }

    /// <summary>
    /// Einmaliger Grandfathering-Sunset (nach v1.x): Die geplante Reinigung erfordert jetzt
    /// durchgehend eine gültige Zeitplan-Lizenz. Läuft noch eine Alt-Aufgabe aus v0.11 ohne
    /// Lizenz, wird sie hier deregistriert (statt bei jedem Lauf still ins Leere zu feuern)
    /// und der Nutzer einmalig informiert. Lizenzierte Nutzer sind nicht betroffen.
    /// </summary>
    private static void SunsetLegacySchedule()
    {
        var settings = SettingsService.Instance.Current;
        if (settings.ScheduleSunsetDone == true) return;

        // Nur eingreifen, wenn KEINE gültige Zeitplan-Lizenz vorliegt.
        if (!LicenseService.Instance.HasFeature(PremiumContract.FeatureSchedule))
        {
            var taskService = new ScheduleTaskService();
            bool hadLegacy = settings.Schedule.Enabled || taskService.IsRegistered();
            if (hadLegacy)
            {
                taskService.Unregister();
                settings.Schedule.Enabled = false;
                new ToastService().Show(
                    Loc.T("schedule.sunset.title"),
                    Loc.T("schedule.sunset.body"));
            }
        }

        settings.ScheduleSunsetDone = true;
        SettingsService.Instance.Save();
    }
}
