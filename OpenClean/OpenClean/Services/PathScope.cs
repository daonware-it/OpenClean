using System.IO;

namespace OpenClean.Services;

/// <summary>
/// Kleine, geteilte Pfad-Primitiven für die Sicherheits-/Grenzprüfungen der Scanner
/// und des Cleaners. Kapselt die fehleranfällige „gleich oder echtes Nachfahren-
/// Verhältnis am Trennzeichen"-Logik an EINER getesteten Stelle, damit sie nicht in
/// jedem Dienst (potenziell subtil falsch) neu entsteht.
///
/// <para>Bewusst NUR das Grenz-Primitiv – NICHT die Ausschluss-Policies. Welche
/// Wurzeln ein Dienst sperrt, entscheidet weiterhin jeder Dienst selbst
/// (Cleaner ≠ Große-Dateien-Finder ≠ Leftover-Scan ≠ App-Größe), denn diese Regeln
/// unterscheiden sich absichtlich.</para>
/// </summary>
internal static class PathScope
{
    /// <summary>
    /// Best-Effort-Normalisierung: voll qualifizierter Pfad ohne End-Trennzeichen
    /// (Groß-/Kleinschreibung bleibt erhalten). Bei ungültigem Pfad wird der reine
    /// getrimmte Originalpfad zurückgegeben statt zu werfen.
    /// </summary>
    internal static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    /// <summary>
    /// True, wenn zwei bereits normalisierte Pfade gleich sind oder <paramref name="path"/>
    /// ein echter Nachfahre von <paramref name="basePath"/> ist. Die Grenze liegt am
    /// Trennzeichen (kein reiner <c>StartsWith</c>), damit z. B. „…\.net" nicht
    /// fälschlich „…\.network" matcht. Groß-/Kleinschreibung wird ignoriert.
    /// </summary>
    internal static bool IsSameOrUnderNormalized(string path, string basePath)
        => path.Equals(basePath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalisiert beide Pfade (<see cref="Normalize"/>) und prüft dann das
    /// Gleich-oder-Nachfahre-Verhältnis. Für Aufrufer, die rohe Pfade vergleichen.
    /// </summary>
    internal static bool IsSameOrUnder(string path, string basePath)
        => IsSameOrUnderNormalized(Normalize(path), Normalize(basePath));
}
