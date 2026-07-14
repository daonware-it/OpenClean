using System.IO;

namespace OpenClean.Services;

/// <summary>
/// Zentrale Sicherheitsprüfung für den Löschpfad des Große-Dateien-Finders.
///
/// <para>Der Finder arbeitet auf echten Nutzerdaten – anders als die Bereinigung, die nur
/// bekannte Temp-Kategorien anfasst. Deshalb gibt es genau eine Stelle, die entscheidet,
/// ob ein Pfad überhaupt angefasst werden darf; sie wird zweimal befragt: als Filter im
/// Scan (<see cref="IsExcludedFolder"/>) und noch einmal direkt vor dem Löschen
/// (<see cref="IsDeletable"/>).</para>
/// </summary>
public static class PathSafety
{
    /// <summary>Ordner, deren Inhalt weder gescannt noch gelöscht wird (Vollpfade).</summary>
    private static readonly string[] ExcludedRoots = BuildExcludedRoots();

    /// <summary>Reine Dateinamen, die nie gelöscht werden – egal wo sie liegen.</summary>
    private static readonly string[] ExcludedFileNames =
    {
        "hiberfil.sys", "pagefile.sys", "swapfile.sys"
    };

    /// <summary>Ordnernamen, die auf jedem Laufwerk gesperrt sind.</summary>
    private static readonly string[] ExcludedAnyDriveFolders =
    {
        "$Recycle.Bin", "System Volume Information"
    };

    private static string[] BuildExcludedRoots()
    {
        var roots = new List<string>();

        void Add(Environment.SpecialFolder folder)
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path)) roots.Add(Normalize(path));
        }

        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
            roots.Add(Normalize(Path.Combine(programData, "Package Cache")));

        return roots.ToArray();
    }

    /// <summary>
    /// True, wenn dieser Pfad gelöscht werden darf. Im Zweifel (leerer Pfad, unlesbarer
    /// Pfad, Laufwerkswurzel) immer false – die sichere Antwort ist „nein".
    /// </summary>
    public static bool IsDeletable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized;
        try { normalized = Normalize(path); }
        catch { return false; }

        // Laufwerkswurzel selbst (z. B. "C:\") ist nie löschbar.
        if (IsDriveRoot(normalized)) return false;

        string fileName = Path.GetFileName(normalized);
        if (ExcludedFileNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
            return false;

        return !IsUnderExcludedRoot(normalized);
    }

    /// <summary>
    /// True, wenn dieser Ordner beim Scan des Datei-Finders übersprungen wird
    /// (Systemordner). Der Sunburst benutzt diese Prüfung bewusst NICHT.
    /// </summary>
    public static bool IsExcludedFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized;
        try { normalized = Normalize(path); }
        catch { return false; }

        return IsUnderExcludedRoot(normalized);
    }

    private static bool IsUnderExcludedRoot(string normalized)
    {
        foreach (string root in ExcludedRoots)
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Auf JEDEM Laufwerk gesperrte Ordnernamen (z. B. "D:\$Recycle.Bin\…").
        foreach (string segment in normalized.Split(Path.DirectorySeparatorChar))
        {
            if (ExcludedAnyDriveFolders.Any(f => string.Equals(f, segment, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>Trailing-Slash entfernen (außer bei "C:\") und Groß-/Kleinschreibung beibehalten.</summary>
    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        if (IsDriveRoot(full)) return full;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsDriveRoot(string fullPath)
        => fullPath.Length <= 3 && fullPath.Length >= 2 && fullPath[1] == ':';
}
