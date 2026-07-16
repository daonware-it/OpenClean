using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace OpenClean.Services.Safety;

/// <summary>Ergebnis eines Versuchs, einen Windows-Wiederherstellungspunkt zu erstellen.</summary>
public enum RestorePointStatus
{
    /// <summary>Ein neuer Wiederherstellungspunkt wurde tatsächlich erstellt.</summary>
    Created,

    /// <summary>
    /// Windows hat den Aufruf akzeptiert, wegen der 24-Stunden-Drosselung aber KEINEN neuen
    /// Punkt angelegt (in den letzten 24 h existiert bereits einer). Kein Fehler.
    /// </summary>
    Throttled,

    /// <summary>Systemwiederherstellung/VSS ist deaktiviert (kein Punkt möglich).</summary>
    Disabled,

    /// <summary>Systemwiederherstellung wird nicht unterstützt (z. B. Windows Server) – Feature ausblenden.</summary>
    NotSupported,

    /// <summary>Keine Administratorrechte (Punkt kann nicht erstellt werden).</summary>
    AccessDenied,

    /// <summary>Sonstiger Fehler (VSS, Speicherplatz, Timeout …).</summary>
    Failed
}

/// <summary>Ein vorhandener Windows-Wiederherstellungspunkt (aus der WMI-Klasse <c>SystemRestore</c>).</summary>
/// <param name="SequenceNumber">Laufende Nummer, mit der Windows den Punkt führt.</param>
/// <param name="Description">Vom Ersteller vergebene Beschreibung.</param>
/// <param name="CreatedUtc">Erstellzeitpunkt (UTC).</param>
public sealed record RestorePointInfo(uint SequenceNumber, string Description, DateTime CreatedUtc);

/// <summary>Ergebnis inkl. lokalisierbarer Detailinformation.</summary>
public sealed record RestorePointResult(RestorePointStatus Status, string Message)
{
    /// <summary>True, wenn danach gefahrlos weitergemacht werden kann (Punkt da ODER Drosselung).</summary>
    public bool IsSafeToContinue => Status is RestorePointStatus.Created or RestorePointStatus.Throttled;
}

/// <summary>
/// Erstellt Windows-Systemwiederherstellungspunkte über die WMI-Klasse
/// <c>SystemRestore</c> (Namespace <c>root\default</c>). Bewusst als schlanker, prozessweiter
/// Dienst mit sauberer Fehlerklassifizierung: der Aufrufer entscheidet anhand von
/// <see cref="RestorePointResult"/>, ob eine Bereinigung fortgesetzt oder abgebrochen wird.
///
/// <para><b>Rechte:</b> <c>CreateRestorePoint</c> verlangt erhöhte Rechte. OpenClean läuft laut
/// app.manifest ohnehin als Administrator; der Elevation-Check bleibt als Sicherheitsnetz.</para>
///
/// <para><b>Drosselung:</b> Auf Consumer-Windows legt Windows höchstens EINEN Punkt pro 24 h an
/// und meldet im Drosselungsfall trotzdem Erfolg (<c>ReturnValue == 0</c>), ohne einen neuen Punkt
/// zu erzeugen. Deshalb wird der jüngste Punkt vorher/nachher verglichen und ehrlich als
/// <see cref="RestorePointStatus.Throttled"/> gemeldet – die Registry wird NICHT heimlich verändert.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RestorePointService
{
    /// <summary>Prozessweiter Singleton.</summary>
    public static RestorePointService Instance { get; } = new();

    private RestorePointService() { }

    // WMI-Konstanten (siehe SystemRestore.CreateRestorePoint):
    private const uint RptModifySettings = 12;    // RestorePointType: Systemeinstellungen ändern
    private const uint EvtBeginSystemChange = 100; // EventType: eigenständigen Punkt erzeugen

    // Ein WMI-Aufruf kann bei belegtem VSS blockieren -> hartes Zeitlimit für den Arbeitsthread.
    private const int CreateTimeoutMs = 90_000;

    /// <summary>True, wenn der aktuelle Prozess mit Administratorrechten läuft.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prüft grob, ob die Systemwiederherstellung aktiviert ist (Registry
    /// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore</c>). Nur ein Vorab-Hinweis;
    /// maßgeblich bleibt der Rückgabewert von <see cref="TryCreate"/>.
    /// </summary>
    public static bool IsSystemRestoreEnabled()
    {
        try
        {
            using RegistryKey? sr = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            if (sr is null) return false;

            if (sr.GetValue("DisableSR") is int disable && disable == 1) return false;
            if (sr.GetValue("RPSessionInterval") is int rp) return rp == 1;

            // Wert nicht vorhanden -> unbestimmt; nicht als sicher aktiviert werten.
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Versucht, einen Wiederherstellungspunkt mit der angegebenen Beschreibung zu erstellen.
    /// Blockiert bis zum Ergebnis (auf eigenem Thread mit Zeitlimit); vom Aufrufer per
    /// <c>Task.Run</c> außerhalb des UI-Threads aufrufen.
    /// </summary>
    public RestorePointResult TryCreate(string description)
    {
        if (!IsElevated())
            return new RestorePointResult(RestorePointStatus.AccessDenied,
                Loc.T("safety.restore.error.accessDenied"));

        RestorePointResult result = new(RestorePointStatus.Failed, Loc.T("safety.restore.error.generic"));
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try { result = CreateCore(description); }
            catch (Exception ex) { captured = ex; }
        })
        {
            IsBackground = true,
            Name = "OpenCleanRestorePoint"
        };
        // MTA reicht für WMI; kein STA nötig.
        thread.Start();

        if (!thread.Join(CreateTimeoutMs))
            return new RestorePointResult(RestorePointStatus.Failed, Loc.T("safety.restore.error.timeout"));

        if (captured is not null)
            return Classify(captured);

        return result;
    }

    private RestorePointResult CreateCore(string description)
    {
        DateTime? before = GetLatestRestorePointTimeUtc();

        var scope = new ManagementScope(@"\\.\root\default");
        using var mc = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);

        using ManagementBaseObject inParams = mc.GetMethodParameters("CreateRestorePoint");
        inParams["Description"] = description;
        inParams["RestorePointType"] = RptModifySettings;
        inParams["EventType"] = EvtBeginSystemChange;

        using ManagementBaseObject outParams = mc.InvokeMethod("CreateRestorePoint", inParams, null);
        uint ret = Convert.ToUInt32(outParams["ReturnValue"], CultureInfo.InvariantCulture);

        if (ret != 0)
        {
            return ret switch
            {
                1058 => new RestorePointResult(RestorePointStatus.Disabled, Loc.T("safety.restore.error.disabled")),
                5 => new RestorePointResult(RestorePointStatus.AccessDenied, Loc.T("safety.restore.error.accessDenied")),
                _ => new RestorePointResult(RestorePointStatus.Failed,
                        Loc.T("safety.restore.error.code", ret))
            };
        }

        // ReturnValue==0 heißt NICHT garantiert "neuer Punkt": Drosselung erkennen.
        DateTime? after = GetLatestRestorePointTimeUtc();
        bool createdNew = after.HasValue && (!before.HasValue || after > before);

        return createdNew
            ? new RestorePointResult(RestorePointStatus.Created, Loc.T("safety.restore.created"))
            : new RestorePointResult(RestorePointStatus.Throttled, Loc.T("safety.restore.throttled"));
    }

    private static RestorePointResult Classify(Exception ex)
    {
        if (ex is ManagementException me)
        {
            return me.ErrorCode switch
            {
                ManagementStatus.AccessDenied =>
                    new RestorePointResult(RestorePointStatus.AccessDenied, Loc.T("safety.restore.error.accessDenied")),
                ManagementStatus.NotFound or ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace =>
                    new RestorePointResult(RestorePointStatus.NotSupported, Loc.T("safety.restore.error.notSupported")),
                _ => new RestorePointResult(RestorePointStatus.Failed, Loc.T("safety.restore.error.generic"))
            };
        }

        if (ex is UnauthorizedAccessException)
            return new RestorePointResult(RestorePointStatus.AccessDenied, Loc.T("safety.restore.error.accessDenied"));

        return new RestorePointResult(RestorePointStatus.Failed, Loc.T("safety.restore.error.generic"));
    }

    // Entfernt EINEN Wiederherstellungspunkt anhand seiner Sequenznummer. Die WMI-Klasse
    // SystemRestore kann das nicht (sie kennt nur Create/Enable/Disable/Restore) – srclient.dll ist
    // der einzige Weg dafür. Rückgabe: ERROR_SUCCESS (0) bei Erfolg.
    [DllImport("srclient.dll", SetLastError = true)]
    private static extern int SRRemoveRestorePoint(int dwRPNum);

    private const int ErrorSuccess = 0;

    /// <summary>
    /// Entfernt den Wiederherstellungspunkt mit der angegebenen Sequenznummer. Gibt <c>true</c> bei
    /// Erfolg zurück. Verlangt Administratorrechte. Blockierender Aufruf – per <c>Task.Run</c> nutzen.
    /// </summary>
    public bool Remove(uint sequenceNumber)
    {
        if (!IsElevated()) return false;

        try
        {
            return SRRemoveRestorePoint((int)sequenceNumber) == ErrorSuccess;
        }
        catch
        {
            // srclient.dll fehlt/nicht unterstützt (z. B. Windows Server).
            return false;
        }
    }

    /// <summary>
    /// Entfernt alle Wiederherstellungspunkte bis auf den jüngsten und meldet, wie viele entfernt
    /// wurden und wie viele scheiterten. Der jüngste bleibt bewusst stehen: Er ist in aller Regel
    /// das Netz der letzten Bereinigung – ihn mitzulöschen würde genau die Absicherung entfernen,
    /// für die OpenClean ihn angelegt hat.
    /// </summary>
    public (int Removed, int Failed) RemoveAllButNewest()
    {
        var points = List();
        if (points.Count <= 1) return (0, 0);

        int removed = 0, failed = 0;
        // List() liefert jüngster zuerst -> ab Index 1 alles entfernen.
        for (int i = 1; i < points.Count; i++)
        {
            if (Remove(points[i].SequenceNumber)) removed++;
            else failed++;
        }

        return (removed, failed);
    }

    /// <summary>
    /// Listet die vorhandenen Wiederherstellungspunkte auf (jüngster zuerst). Best effort: ist die
    /// Systemwiederherstellung abgeschaltet oder nicht verfügbar, kommt eine leere Liste zurück.
    /// Blockierender WMI-Zugriff – vom Aufrufer per <c>Task.Run</c> außerhalb des UI-Threads nutzen.
    /// </summary>
    public IReadOnlyList<RestorePointInfo> List()
    {
        var points = new List<RestorePointInfo>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT SequenceNumber, Description, CreationTime FROM SystemRestore"));

            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (!TryParseWmiDate(mo["CreationTime"]?.ToString(), out var utc)) continue;

                    uint seq;
                    try { seq = Convert.ToUInt32(mo["SequenceNumber"], CultureInfo.InvariantCulture); }
                    catch { continue; }

                    points.Add(new RestorePointInfo(seq, mo["Description"]?.ToString() ?? "", utc));
                }
            }
        }
        catch
        {
            // Deaktiviert/nicht unterstützt/keine Rechte -> nichts anzuzeigen, kein Fehlerfall.
        }

        points.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return points;
    }

    /// <summary>Erstellzeit (UTC) des jüngsten vorhandenen Wiederherstellungspunkts oder null.</summary>
    private DateTime? GetLatestRestorePointTimeUtc()
    {
        // Prüfung ist optional; bei Fehler liefert List() eine leere Liste und blockiert nicht.
        var points = List();
        return points.Count > 0 ? points[0].CreatedUtc : null;
    }

    /// <summary>
    /// Wandelt ein WMI-DATETIME ("yyyyMMddHHmmss.ffffff±zzz") in UTC.
    ///
    /// <para>Der Offset am Ende gehört zwingend ausgewertet und darf NICHT abgeschnitten werden:
    /// Der SystemRestore-Provider liefert die Zeit mit Offset <c>-000</c>, also in UTC. Nur die
    /// ersten 14 Zeichen zu nehmen und sie als Ortszeit zu deuten, verschiebt jeden Punkt um den
    /// UTC-Abstand der Zeitzone (hier: 2 Stunden). <see cref="ManagementDateTimeConverter"/> ist der
    /// dafür vorgesehene Parser und rechnet den Offset korrekt heraus.</para>
    /// </summary>
    private static bool TryParseWmiDate(string? raw, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(raw)) return false;

        try
        {
            utc = ManagementDateTimeConverter.ToDateTime(raw).ToUniversalTime();
            return true;
        }
        catch
        {
            // Unerwartetes Format -> Punkt überspringen statt mit falscher Zeit anzeigen.
            return false;
        }
    }
}
