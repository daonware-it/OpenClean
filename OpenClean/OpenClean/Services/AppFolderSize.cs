using System.Collections.Concurrent;
using System.IO;

namespace OpenClean.Services;

/// <summary>
/// Best-Effort-Größe von Installationsordnern mit prozessweitem Cache und Sicherheits-Filter.
/// Zuvor Teil des Inventar-Service <c>InstalledAppsService</c> – als eigene, fokussierte
/// Einheit ausgelagert (Registry-Lesen ≠ Ordnergröße-Berechnung). Bewusst NICHT über
/// <see cref="DirectorySizeCalculator"/>: nutzt <c>EnumerationOptions</c> (überspringt
/// Unzugängliches, folgt Reparse-Points nicht) und cacht das Ergebnis.
/// </summary>
internal static class AppFolderSize
{
    private static readonly ConcurrentDictionary<string, long> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string CacheKey(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Bereits berechnete Größe für einen Pfad, sonst 0 (ohne Neuberechnung).</summary>
    internal static long GetCached(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        return Cache.TryGetValue(CacheKey(path), out var size) ? size : 0;
    }

    /// <summary>
    /// Best-effort-Größe eines Installationsordners (Summe aller Dateien, rekursiv), mit Cache.
    /// Fallback, wenn die Registry keine <c>EstimatedSize</c> liefert (z. B. Steam-Spiele).
    /// Läuft potenziell länger → vom Aufrufer im Hintergrund ausführen. 0 bei Fehler.
    /// </summary>
    internal static long TryGet(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        path = path.Trim().Trim('"');
        if (!Directory.Exists(path) || !IsSafeFolder(path)) return 0;

        string key = CacheKey(path);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        long total = 0;
        try
        {
            // IgnoreInaccessible: gesperrte Unterordner überspringen statt die gesamte
            // Aufzählung abzubrechen (sonst liefern z. B. Spiele-Ordner 0 oder Teilsummen).
            // ReparsePoint: Symlinks/Junctions nicht verfolgen (keine Doppelzählung/Schleifen).
            // AttributesToSkip explizit setzen: der Default würde Hidden/System-Dateien auslassen.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try { total += file.Length; }
                catch { /* gesperrt/nicht lesbar -> überspringen */ }
            }
        }
        catch { /* Zugriff verweigert -> Teilsumme */ }

        if (total > 0) Cache[key] = total;
        return total;
    }

    /// <summary>
    /// Schutz vor absurden Größen: keine Laufwerkswurzeln, nichts unterhalb von Windows
    /// und keine Sammel-Wurzeln (Program Files, AppData, ProgramData, Benutzerprofil) –
    /// manche Installer tragen genau solche Pfade als InstallLocation ein.
    /// </summary>
    internal static bool IsSafeFolder(string path)
    {
        try
        {
            string full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? root = Path.GetPathRoot(full)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(full) ||
                string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return false;

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(windows) && PathScope.IsSameOrUnderNormalized(full, windows))
                return false;

            foreach (var folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData
            })
            {
                string p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p) &&
                    full.Equals(p.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch { return false; }
    }
}
