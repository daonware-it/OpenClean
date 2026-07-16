using System.Diagnostics;
using System.IO;
using System.Text;
using OpenClean.Models;
using OpenClean.Services.Integrity;

namespace OpenClean.Services;

/// <summary>
/// Agent (Planer): registriert/entfernt die geplante Aufgabe im Windows-Aufgabenplaner,
/// die OpenClean zur eingestellten Zeit mit <c>--auto</c> startet. Bewusst über das
/// mitgelieferte <c>schtasks.exe</c> (kein zusätzliches NuGet-Paket, kein COM-Interop).
///
/// Die Aufgabe läuft mit HÖCHSTEN Rechten (/RL HIGHEST) – nur so startet die per
/// Manifest als Administrator markierte EXE beim geplanten Lauf LAUTLOS, ohne dass bei
/// jeder Ausführung eine UAC-Abfrage erscheint. Das Erstellen/Löschen selbst benötigt
/// Adminrechte, die OpenClean ohnehin besitzt.
/// </summary>
public sealed class ScheduleTaskService
{
    /// <summary>Fester Name der geplanten Aufgabe (im Aufgabenplaner sichtbar).</summary>
    public const string TaskName = "OpenClean Automatische Reinigung";

    /// <summary>Kommandozeilen-Schalter, der den unbeaufsichtigten Reinigungslauf auslöst.</summary>
    public const string AutoSwitch = "--auto";

    /// <summary>True, wenn die geplante Aufgabe aktuell registriert ist.</summary>
    public bool IsRegistered()
    {
        var (exit, _) = RunSchtasks(new[] { "/Query", "/TN", TaskName });
        return exit == 0;
    }

    /// <summary>
    /// Legt die geplante Aufgabe anhand der Einstellungen an (überschreibt eine
    /// vorhandene). Gibt true bei Erfolg zurück.
    ///
    /// Registriert über eine XML-Definition (<c>schtasks /Create /XML</c>) statt über
    /// Einzel-Flags, da nur so die für Laptops entscheidenden Einstellungen setzbar sind:
    /// auch im Akkubetrieb starten und einen verpassten Lauf (PC aus/Standby) nachholen.
    /// </summary>
    public bool Register(ScheduleSettings schedule)
    {
        // Sperre bei erkannter Manipulation (OPCL-20): keine neue unbeaufsichtigte Aufgabe
        // anlegen. Unregister bleibt bewusst OHNE Sperre – eine bestehende Aufgabe zu
        // entfernen ist sicherheitstechnisch immer erwünscht.
        if (IntegrityState.IsBlocked) return false;

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        string xml = BuildTaskXml(schedule, exe);

        // schtasks /XML erwartet die Datei in UTF-16 (mit BOM); die XML-Deklaration
        // nennt entsprechend UTF-16.
        string tempFile = Path.Combine(Path.GetTempPath(), $"openclean-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempFile, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var (exit, _) = RunSchtasks(new[] { "/Create", "/TN", TaskName, "/XML", tempFile, "/F" });
            return exit == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { /* Aufräumen ist optional. */ }
        }
    }

    /// <summary>
    /// Baut die Aufgabenplaner-XML (Schema 1.2) für den gewünschten Zeitplan: Trigger je
    /// nach Frequenz, höchste Rechte, Start auch im Akkubetrieb und Nachholen verpasster Läufe.
    /// </summary>
    private static string BuildTaskXml(ScheduleSettings schedule, string exe)
    {
        int hour = Clamp(schedule.Hour, 0, 23);
        int minute = Clamp(schedule.Minute, 0, 59);
        // Task Scheduler verlangt ein StartBoundary-Datum; das konkrete Datum ist bei
        // einem wiederkehrenden Kalender-Trigger unerheblich – nur die Uhrzeit zählt.
        string startBoundary = $"2020-01-01T{hour:D2}:{minute:D2}:00";
        string user = X($"{Environment.UserDomainName}\\{Environment.UserName}");
        string command = X(exe);

        string trigger = schedule.Frequency switch
        {
            "Daily" =>
                $"      <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>",
            "Monthly" =>
                "      <ScheduleByMonth>" +
                $"<DaysOfMonth><Day>{Clamp(schedule.DayOfMonth, 1, 28)}</Day></DaysOfMonth>" +
                "<Months><January/><February/><March/><April/><May/><June/><July/>" +
                "<August/><September/><October/><November/><December/></Months>" +
                "</ScheduleByMonth>",
            _ => // "Weekly"
                "      <ScheduleByWeek><WeeksInterval>1</WeeksInterval>" +
                $"<DaysOfWeek><{WeekdayElement(schedule.DayOfWeek)}/></DaysOfWeek>" +
                "</ScheduleByWeek>"
        };

        return
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>OpenClean</Author>
    <Description>{X("Automatische Reinigung durch OpenClean.")}</Description>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>{startBoundary}</StartBoundary>
      <Enabled>true</Enabled>
{trigger}
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{command}</Command>
      <Arguments>{AutoSwitch}</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    /// <summary>Entfernt die geplante Aufgabe. True, wenn sie danach nicht mehr existiert.</summary>
    public bool Unregister()
    {
        var (exit, _) = RunSchtasks(new[] { "/Delete", "/TN", TaskName, "/F" });
        // Exit 0 = gelöscht; ist sie ohnehin nicht vorhanden, gilt das ebenfalls als Erfolg.
        return exit == 0 || !IsRegistered();
    }

    /// <summary>Wendet den Wunschzustand an: registrieren wenn aktiviert, sonst entfernen.</summary>
    public bool Apply(ScheduleSettings schedule)
        => schedule.Enabled ? Register(schedule) : Unregister();

    // ---- Hilfsfunktionen ----------------------------------------------------

    /// <summary>Wochentag als Task-Scheduler-XML-Elementname (z. B. <c>Monday</c>).</summary>
    private static string WeekdayElement(int dayOfWeek) => (dayOfWeek % 7) switch
    {
        0 => "Sunday",
        1 => "Monday",
        2 => "Tuesday",
        3 => "Wednesday",
        4 => "Thursday",
        5 => "Friday",
        _ => "Saturday"
    };

    /// <summary>XML-escaped einen Textwert (Pfade, Benutzernamen) für die Task-Definition.</summary>
    internal static string X(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;

    /// <summary>Ruft <c>schtasks.exe</c> auf; wirft nie (Fehler => Exit-Code -1).</summary>
    internal static (int exitCode, string output) RunSchtasks(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return (-1, "");

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
    }
}
