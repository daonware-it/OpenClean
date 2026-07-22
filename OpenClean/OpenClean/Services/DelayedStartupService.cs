using System.IO;
using System.Text;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Startet Autostart-Programme verzögert statt sofort mit der Anmeldung: Der Original-Eintrag
/// wird über den StartupApproved-Mechanismus (<see cref="StartupService.SetEnabled"/>) still
/// gelegt, und an seiner Stelle legt OpenClean eine geplante Aufgabe mit Anmelde-Trigger und
/// <c>&lt;Delay&gt;</c> an. Das Programm startet also weiterhin – nur eben später, wenn der
/// Desktop bereits steht.
///
/// Technik wie bei <see cref="ScheduleTaskService"/>: über das mitgelieferte <c>schtasks.exe</c>
/// mit XML-Definition (kein NuGet-Paket, kein COM-Interop). Die Aufgaben laufen bewusst mit
/// NORMALEN Rechten (LeastPrivilege) – ein Fremdprogramm soll genauso starten wie zuvor aus dem
/// Run-Key, nicht plötzlich erhöht.
///
/// Persistenz: <see cref="AppSettings.DelayedStartup"/> (settings.json). Die Aufgabe ist die
/// Wahrheit, die Settings-Liste nur der Index dazu – <see cref="Reconcile"/> gleicht beides ab.
/// </summary>
public sealed class DelayedStartupService
{
    /// <summary>Aufgabenplaner-Ordner, in dem alle Verzögerungs-Aufgaben liegen.</summary>
    public const string TaskFolder = "OpenClean Delayed";

    /// <summary>
    /// Wählbare Verzögerungsstufen in Sekunden (0 = keine Verzögerung). Dieselben Stufen dienen
    /// als Leiter für die automatische Staffelung – deshalb keine krummen Zwischenwerte, die in
    /// der Auswahlliste fehlen würden.
    /// </summary>
    public static readonly int[] DelayOptions = { 0, 30, 60, 120, 180, 300, 600 };

    private readonly StartupService _startupService;

    public DelayedStartupService(StartupService startupService) => _startupService = startupService;

    private static List<DelayedStartupItem> Items => SettingsService.Instance.Current.DelayedStartup;

    // ---- Abfragen -----------------------------------------------------------

    /// <summary>
    /// True, wenn der Eintrag verzögert werden kann. Voraussetzung: schaltbar, aus einem
    /// Run-Key oder Autostart-Ordner (RunOnce/Richtlinie/Store-App scheiden aus) und mit
    /// einer real aufrufbaren EXE.
    /// </summary>
    public bool CanDelay(StartupEntry entry)
    {
        if (!entry.CanToggle) return false;

        bool supportedLocation = entry.Location
            is StartupLocation.HkcuRun
            or StartupLocation.HklmRun
            or StartupLocation.HklmRunWow6432
            or StartupLocation.StartupFolderUser
            or StartupLocation.StartupFolderCommon;
        if (!supportedLocation) return false;

        // Ohne auflösbare EXE gibt es nichts, was die Aufgabe starten könnte. Eine nicht
        // aufgelöste Verknüpfung (.lnk als "Ziel") taugt nicht als Exec-Command.
        string? exe = entry.ExecutablePath;
        return !string.IsNullOrWhiteSpace(exe)
               && !exe.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Eingestellte Verzögerung in Sekunden; 0, wenn der Eintrag nicht verzögert wird.</summary>
    public int GetDelay(StartupEntry entry)
        => Find(entry)?.DelaySeconds ?? 0;

    /// <summary>True, wenn aktuell mindestens ein Eintrag verzögert gestartet wird.</summary>
    public bool HasAny() => Items.Count > 0;

    // ---- Anwenden -----------------------------------------------------------

    /// <summary>
    /// Setzt, ändert oder entfernt die Verzögerung eines Eintrags. <paramref name="delaySeconds"/>
    /// = 0 hebt die Verzögerung auf und aktiviert den Original-Eintrag wieder.
    /// Wirft bei Misserfolg – der Aufrufer meldet den Fehler an den Nutzer.
    /// </summary>
    public void Apply(StartupEntry entry, int delaySeconds)
    {
        if (delaySeconds <= 0)
        {
            Remove(entry, restoreOriginal: true);
            return;
        }

        if (!CanDelay(entry))
            throw new InvalidOperationException(Loc.T("startup.delay.notSupported"));

        string taskName = TaskNameFor(entry);
        string xml = BuildTaskXml(entry, delaySeconds);

        // Erst die Aufgabe anlegen – nur wenn sie wirklich steht, wird der Original-Eintrag
        // deaktiviert. Andernfalls würde das Programm gar nicht mehr starten.
        if (!RegisterTask(taskName, xml))
            throw new InvalidOperationException(Loc.T("startup.delay.taskFailed"));

        _startupService.SetEnabled(entry, false);

        var existing = Find(entry);
        if (existing is null)
        {
            Items.Add(new DelayedStartupItem
            {
                Key = KeyFor(entry),
                Name = entry.Name,
                Command = entry.Command,
                Location = entry.Location.ToString(),
                DelaySeconds = delaySeconds,
                TaskName = taskName
            });
        }
        else
        {
            existing.DelaySeconds = delaySeconds;
            existing.TaskName = taskName;
            existing.Command = entry.Command;
        }

        SettingsService.Instance.Save();
    }

    /// <summary>
    /// Entfernt die Verzögerung: löscht die geplante Aufgabe und den Settings-Datensatz.
    /// <paramref name="restoreOriginal"/> steuert, ob der Original-Autostart-Eintrag wieder
    /// aktiviert wird (false z. B., wenn der Nutzer den Eintrag ohnehin ausschaltet).
    /// </summary>
    public void Remove(StartupEntry entry, bool restoreOriginal)
    {
        var item = Find(entry);
        if (item is null) return;

        DeleteTask(item.TaskName);
        Items.Remove(item);
        SettingsService.Instance.Save();

        if (restoreOriginal)
            _startupService.SetEnabled(entry, true);
    }

    /// <summary>
    /// Hebt alle Verzögerungen auf. Die Original-Einträge werden anhand der übergebenen,
    /// frisch gelesenen Einträge wieder aktiviert; für Datensätze ohne passenden Eintrag
    /// wird nur die Aufgabe entfernt.
    /// </summary>
    public void RemoveAll(IEnumerable<StartupEntry> knownEntries)
    {
        var byKey = IndexByKey(knownEntries);

        foreach (var item in Items.ToList())
        {
            DeleteTask(item.TaskName);
            if (byKey.TryGetValue(item.Key, out var entry))
            {
                try { _startupService.SetEnabled(entry, true); }
                catch { /* einzelner Eintrag nicht schaltbar -> Rest trotzdem aufräumen */ }
            }
        }

        Items.Clear();
        SettingsService.Instance.Save();
    }

    /// <summary>
    /// Gleicht Settings und Aufgabenplaner ab: Datensätze, deren Aufgabe der Nutzer außerhalb
    /// von OpenClean gelöscht hat, werden verworfen und der Original-Eintrag reaktiviert –
    /// sonst würde die Liste eine Verzögerung anzeigen, die es nicht mehr gibt (und das
    /// Programm startete gar nicht mehr).
    /// </summary>
    public void Reconcile(IEnumerable<StartupEntry> knownEntries)
    {
        if (Items.Count == 0) return;

        var byKey = IndexByKey(knownEntries);
        bool changed = false;

        foreach (var item in Items.ToList())
        {
            if (TaskExists(item.TaskName)) continue;

            Items.Remove(item);
            changed = true;

            if (byKey.TryGetValue(item.Key, out var entry) && !entry.IsEnabled)
            {
                try { _startupService.SetEnabled(entry, true); }
                catch { /* nicht schaltbar -> Datensatz ist trotzdem weg */ }
            }
        }

        if (changed)
            SettingsService.Instance.Save();
    }

    // ---- Aufgabenplaner -----------------------------------------------------

    private static bool RegisterTask(string taskName, string xml)
    {
        // schtasks /XML erwartet die Datei in UTF-16 (mit BOM) – wie in ScheduleTaskService.
        string tempFile = Path.Combine(Path.GetTempPath(), $"openclean-delay-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempFile, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            var (exit, _) = ScheduleTaskService.RunSchtasks(
                new[] { "/Create", "/TN", taskName, "/XML", tempFile, "/F" });
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

    private static void DeleteTask(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName)) return;
        ScheduleTaskService.RunSchtasks(new[] { "/Delete", "/TN", taskName, "/F" });
    }

    private static bool TaskExists(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName)) return false;
        var (exit, _) = ScheduleTaskService.RunSchtasks(new[] { "/Query", "/TN", taskName });
        return exit == 0;
    }

    /// <summary>
    /// Baut die Aufgabenplaner-XML (Schema 1.2): Anmelde-Trigger mit Verzögerung, normale
    /// Rechte, kein Zeitlimit (das gestartete Programm läuft dauerhaft weiter).
    /// </summary>
    private static string BuildTaskXml(StartupEntry entry, int delaySeconds)
    {
        var (exe, arguments) = CommandLine.Split(entry.Command);
        // Store-App/nicht auflösbar ist über CanDelay bereits ausgeschlossen; für Einträge aus
        // dem Autostart-Ordner steht in Command das aufgelöste Verknüpfungsziel.
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            exe = entry.ExecutablePath ?? exe;

        string workingDir = Path.GetDirectoryName(exe) ?? "";
        string user = ScheduleTaskService.X($"{Environment.UserDomainName}\\{Environment.UserName}");

        string argumentsElement = string.IsNullOrWhiteSpace(arguments)
            ? ""
            : $"\n      <Arguments>{ScheduleTaskService.X(arguments)}</Arguments>";
        string workingDirElement = string.IsNullOrWhiteSpace(workingDir)
            ? ""
            : $"\n      <WorkingDirectory>{ScheduleTaskService.X(workingDir)}</WorkingDirectory>";

        return
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>OpenClean</Author>
    <Description>{ScheduleTaskService.X(Loc.T("startup.delay.taskDescription", entry.Name))}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
      <Delay>{IsoDuration(delaySeconds)}</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{ScheduleTaskService.X(exe)}</Command>{argumentsElement}{workingDirElement}
    </Exec>
  </Actions>
</Task>";
    }

    // ---- Hilfsfunktionen ----------------------------------------------------

    /// <summary>Verzögerung als ISO-8601-Dauer, z. B. 120 → <c>PT2M</c>, 30 → <c>PT30S</c>.</summary>
    internal static string IsoDuration(int seconds)
        => seconds % 60 == 0 ? $"PT{seconds / 60}M" : $"PT{seconds}S";

    /// <summary>Lokalisierte Beschriftung einer Verzögerungsstufe („Keine", „30 Sek.", „2 Min.").</summary>
    public static string DisplayFor(int seconds) => seconds switch
    {
        <= 0 => Loc.T("startup.delay.none"),
        < 60 => Loc.T("startup.delay.sec", seconds),
        _ => Loc.T("startup.delay.min", seconds / 60)
    };

    private static string KeyFor(StartupEntry entry)
        => $"{entry.Location}|{entry.Name.ToLowerInvariant()}";

    /// <summary>
    /// Index Schlüssel → Eintrag. Namensdubletten innerhalb derselben Quelle sind möglich
    /// (z. B. zwei Store-Apps gleichen Namens), daher gewinnt der erste Eintrag, statt zu werfen.
    /// </summary>
    private static Dictionary<string, StartupEntry> IndexByKey(IEnumerable<StartupEntry> entries)
    {
        var map = new Dictionary<string, StartupEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            map.TryAdd(KeyFor(entry), entry);
        return map;
    }

    private static DelayedStartupItem? Find(StartupEntry entry)
    {
        string key = KeyFor(entry);
        return Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Voller Aufgabenname. Der Location-Präfix trennt gleichnamige Einträge aus verschiedenen
    /// Quellen; im Namen unzulässige Zeichen (u. a. der Ordnertrenner) werden ersetzt.
    /// </summary>
    private static string TaskNameFor(StartupEntry entry)
    {
        var sb = new StringBuilder(entry.Name.Length);
        foreach (char c in entry.Name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_');

        string safe = sb.ToString().Trim();
        if (safe.Length == 0) safe = "Eintrag";
        if (safe.Length > 80) safe = safe[..80];

        return $@"{TaskFolder}\{entry.Location}_{safe}";
    }
}
