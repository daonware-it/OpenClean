using System.Diagnostics;
using OpenClean.Models;

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
    /// </summary>
    public bool Register(ScheduleSettings schedule)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        var args = new List<string>
        {
            "/Create",
            "/TN", TaskName,
            // Anführungszeichen um den EXE-Pfad, damit Leerzeichen im Pfad korrekt sind.
            "/TR", $"\"{exe}\" {AutoSwitch}",
            "/RL", "HIGHEST",
            "/F",
            "/ST", $"{Clamp(schedule.Hour, 0, 23):D2}:{Clamp(schedule.Minute, 0, 59):D2}"
        };

        switch (schedule.Frequency)
        {
            case "Daily":
                args.Add("/SC"); args.Add("DAILY");
                break;
            case "Monthly":
                args.Add("/SC"); args.Add("MONTHLY");
                args.Add("/D"); args.Add(Clamp(schedule.DayOfMonth, 1, 28).ToString());
                break;
            default: // "Weekly"
                args.Add("/SC"); args.Add("WEEKLY");
                args.Add("/D"); args.Add(WeekdayCode(schedule.DayOfWeek));
                break;
        }

        var (exit, _) = RunSchtasks(args);
        return exit == 0;
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

    private static string WeekdayCode(int dayOfWeek) => (dayOfWeek % 7) switch
    {
        0 => "SUN",
        1 => "MON",
        2 => "TUE",
        3 => "WED",
        4 => "THU",
        5 => "FRI",
        _ => "SAT"
    };

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;

    private static (int exitCode, string output) RunSchtasks(IEnumerable<string> args)
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
