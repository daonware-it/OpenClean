using System.IO;
using System.Runtime.Versioning;
using OpenClean.Services.Safety;

namespace OpenClean.Services.DeepClean;

/// <summary>
/// Alles, was <see cref="DeepCleanService"/> außer Prozessaufrufen noch vom echten System
/// braucht: Pfade, Rechte, Ordnergrößen und die Wiederherstellungspunkte. Als Schnittstelle,
/// damit der Dienst in Tests ohne Administratorrechte und ohne echte Systemänderung läuft.
/// </summary>
internal interface IDeepCleanEnvironment
{
    /// <summary>True, wenn der Prozess erhöht läuft. Ohne das geht keiner der fünf Bereiche.</summary>
    bool IsElevated { get; }

    /// <summary>Vollpfad der alten Windows-Installation (<c>C:\Windows.old</c>).</summary>
    string WindowsOldPath { get; }

    /// <summary>Vollpfad des Windows-Update-Zwischenspeichers.</summary>
    string UpdateCachePath { get; }

    /// <summary>Vollpfad des Caches der Übermittlungsoptimierung.</summary>
    string DeliveryOptimizationPath { get; }

    bool DirectoryExists(string path);

    /// <summary>Rekursiv belegte Bytes eines Ordners. Unlesbare Zweige zählen als 0, nie eine Ausnahme.</summary>
    long GetDirectorySize(string path, CancellationToken ct);

    /// <summary>Leert einen Ordner (Inhalt, nicht den Ordner selbst) und meldet die Zahl der Fehlschläge.</summary>
    int EmptyDirectory(string path, CancellationToken ct);

    /// <summary>Anzahl vorhandener Wiederherstellungspunkte.</summary>
    int CountRestorePoints();

    /// <summary>Entfernt alle Wiederherstellungspunkte bis auf den jüngsten.</summary>
    (int Removed, int Failed) RemoveOldRestorePoints();
}

/// <summary>Die echte, auf Windows aufsetzende Implementierung.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDeepCleanEnvironment : IDeepCleanEnvironment
{
    public bool IsElevated => RestorePointService.IsElevated();

    public string WindowsOldPath => Path.Combine(SystemDriveRoot(), "Windows.old");

    public string UpdateCachePath
        => Path.Combine(WindowsDirectory(), "SoftwareDistribution", "Download");

    /// <summary>
    /// Cache der Übermittlungsoptimierung. Der Ort hat sich mit Windows 10 1709 verschoben:
    /// seither liegt er im Profil des Dienstkontos „NetworkService", davor unter
    /// <c>SoftwareDistribution</c>. Entschieden wird über den tatsächlich vorhandenen Ordner
    /// und nicht über die Build-Nummer – der Ordner ist die verlässlichere Auskunft.
    /// Existiert keiner von beiden, bleibt es beim heutigen Pfad; der Bereich meldet dann
    /// „nicht verfügbar“, statt einen erfundenen Ort anzugeben.
    ///
    /// <para><b>Der Pfad zeigt bewusst auf das Unterverzeichnis <c>Cache</c>, nicht auf den
    /// DO-Wurzelordner.</b> Der Wurzelordner enthält daneben <c>State</c> (die DO-Datenbank) und
    /// <c>Logs</c>. Auf den Wurzelordner gerichtet würde die Bereinigung diese mitlöschen –
    /// gemessen auf einem echten System: Cache 11,7 GB, Logs 31,6 MB, State 1,0 MB. Zu holen ist
    /// also praktisch alles im Cache; die Datenbank mitzunehmen bringt nichts und beschädigt den
    /// Dienst.</para>
    /// </summary>
    public string DeliveryOptimizationPath
    {
        get
        {
            foreach (var candidate in DeliveryOptimizationCandidates())
                if (Directory.Exists(candidate)) return candidate;

            return DeliveryOptimizationCandidates()[0];
        }
    }

    /// <summary>
    /// Die bekannten Orte des DO-Caches, moderner Pfad zuerst. Beide enden auf <c>Cache</c>.
    ///
    /// <para>Für den modernen Pfad ist die <c>Cache</c>-Ebene auf einem echten System belegt. Für
    /// den alten Pfad (vor 1709) ließ sie sich hier nicht nachmessen – der Ordner existiert auf
    /// keinem verfügbaren System mehr. Im Zweifel wird deshalb die Variante gewählt, die WENIGER
    /// löscht: Gibt es dort kein <c>Cache</c>-Unterverzeichnis, meldet der Bereich „nicht
    /// verfügbar“ und rührt nichts an, statt ersatzweise den Wurzelordner samt Datenbank zu
    /// leeren.</para>
    /// </summary>
    private static string[] DeliveryOptimizationCandidates() =>
    [
        Path.Combine(WindowsDirectory(), "ServiceProfiles", "NetworkService", "AppData",
                     "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
        Path.Combine(WindowsDirectory(), "SoftwareDistribution", "DeliveryOptimization", "Cache"),
    ];

    private static string WindowsDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Wurzel des Systemlaufwerks („C:\") – abgeleitet aus dem Windows-Ordner, nicht geraten.</summary>
    private static string SystemDriveRoot()
    {
        string windows = WindowsDirectory();
        string? root = Path.GetPathRoot(windows);
        return string.IsNullOrEmpty(root) ? @"C:\" : root;
    }

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    public long GetDirectorySize(string path, CancellationToken ct)
    {
        if (!DirectoryExists(path)) return 0;

        long total = 0;
        // Iterativ statt rekursiv: Windows.old und WinSxS sind tief verschachtelt, ein
        // rekursiver Abstieg riskiert einen Stapelüberlauf.
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = pending.Pop();

            try
            {
                foreach (string file in Directory.EnumerateFiles(current))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        // Verknüpfungen (Hardlinks/Junctions) nicht mitzählen – WinSxS und
                        // Windows.old sind voll davon; sonst käme ein Vielfaches heraus.
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        total += info.Length;
                    }
                    catch { /* Datei verschwunden oder gesperrt -> als 0 zählen */ }
                }

                foreach (string dir in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }

                    pending.Push(dir);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Zugriff verweigert -> Zweig überspringen */ }
        }

        return total;
    }

    public int EmptyDirectory(string path, CancellationToken ct)
    {
        if (!DirectoryExists(path)) return 0;

        int failures = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file) { Attributes = FileAttributes.Normal };
                    info.Delete();
                }
                catch { failures++; }
            }

            foreach (string dir in Directory.EnumerateDirectories(path))
            {
                ct.ThrowIfCancellationRequested();
                try { Directory.Delete(dir, recursive: true); }
                catch { failures++; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { failures++; }

        return failures;
    }

    public int CountRestorePoints() => RestorePointService.Instance.List().Count;

    public (int Removed, int Failed) RemoveOldRestorePoints()
        => RestorePointService.Instance.RemoveAllButNewest();
}
