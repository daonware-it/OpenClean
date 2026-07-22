using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClean.Services.Network;

/// <summary>Name und Pfad eines Prozesses; leere Felder bedeuten „nicht ermittelbar".</summary>
public sealed class ProcessIdentity
{
    public static readonly ProcessIdentity Unknown = new("", "");

    public ProcessIdentity(string name, string path)
    {
        Name = name;
        Path = path;
    }

    /// <summary>Dateiname ohne Endung (z. B. „chrome").</summary>
    public string Name { get; }

    /// <summary>Vollständiger Pfad der EXE, leer wenn nicht lesbar.</summary>
    public string Path { get; }
}

/// <summary>
/// Cache PID → Prozessname/-pfad. Der Pfad wird über
/// <c>QueryFullProcessImageName</c> mit PROCESS_QUERY_LIMITED_INFORMATION gelesen – das
/// funktioniert auch für Prozesse anderer Benutzer, scheitert aber bei geschützten
/// Prozessen (PPL) und den Kernel-Pseudo-PIDs 0/4. In diesem Fall bleibt der Pfad leer;
/// es wird nie geworfen.
///
/// PIDs werden von Windows wiederverwendet. Deshalb muss der Aufrufer nach jeder Abfrage
/// <see cref="Trim"/> mit den aktuell noch vorhandenen PIDs aufrufen: verschwindet eine PID
/// aus der Tabelle, fliegt ihr Eintrag raus und eine später gleich nummerierte, neue
/// Anwendung wird frisch aufgelöst.
/// </summary>
public sealed class ProcessPathCache
{
    private readonly ConcurrentDictionary<int, ProcessIdentity> _cache = new();

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION – reicht zum Lesen des Abbildpfads.</summary>
    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags,
        StringBuilder exeName, ref int size);

    /// <summary>Identität der PID – aus dem Cache oder frisch ermittelt.</summary>
    public ProcessIdentity Get(int processId) => _cache.GetOrAdd(processId, Resolve);

    /// <summary>Entfernt alle Einträge, deren PID nicht mehr in <paramref name="livePids"/> steht.</summary>
    public void Trim(IReadOnlySet<int> livePids)
    {
        foreach (int pid in _cache.Keys)
            if (!livePids.Contains(pid))
                _cache.TryRemove(pid, out _);
    }

    /// <summary>Verwirft den gesamten Cache (z. B. beim erneuten Öffnen der Ansicht).</summary>
    public void Clear() => _cache.Clear();

    private static ProcessIdentity Resolve(int processId)
    {
        // PID 0/4 sind Kernel-Pseudoprozesse ohne Abbilddatei.
        if (processId <= 0) return new ProcessIdentity("System Idle Process", "");
        if (processId == 4) return new ProcessIdentity("System", "");

        string path = TryGetImagePath(processId);
        if (!string.IsNullOrEmpty(path))
        {
            string name;
            try { name = System.IO.Path.GetFileNameWithoutExtension(path); }
            catch { name = ""; }
            return new ProcessIdentity(name, path);
        }

        // Kein Pfad (geschützter Prozess): wenigstens den Namen versuchen.
        try
        {
            using var p = Process.GetProcessById(processId);
            return new ProcessIdentity(p.ProcessName, "");
        }
        catch
        {
            return ProcessIdentity.Unknown; // Prozess bereits beendet o. Ä.
        }
    }

    private static string TryGetImagePath(int processId)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return "";

            int size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, sb, ref size)
                ? sb.ToString(0, Math.Min(size, sb.Length))
                : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }
}
