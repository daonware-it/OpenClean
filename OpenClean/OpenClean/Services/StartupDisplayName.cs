using System.Text.RegularExpressions;

namespace OpenClean.Services;

/// <summary>
/// Reine Namens-Heuristiken für Autostart-Einträge (vor allem UWP/Store-Apps): erzeugt
/// aus technischen <c>PackageFamilyName</c>/<c>TaskId</c>-Werten einen lesbaren
/// Anzeigenamen. Zuvor privat in <c>StartupService</c> – als eigenständige, testbare
/// Einheit ausgelagert.
/// </summary>
internal static class StartupDisplayName
{
    /// <summary>Segment zwischen erstem '.' und '_' (Paketname), Fallback: Teil vor '_'.</summary>
    internal static string PackageNameSegment(string packageFamilyName)
    {
        string beforeUnderscore = packageFamilyName.Split('_', 2)[0];
        int dot = beforeUnderscore.IndexOf('.');
        if (dot >= 0 && dot + 1 < beforeUnderscore.Length)
            return beforeUnderscore[(dot + 1)..];
        return beforeUnderscore;
    }

    /// <summary>True, wenn der Text wie ein Hash aussieht: rein hexadezimal (≥6) oder nur Ziffern.</summary>
    internal static bool LooksLikeHash(string text)
        => !string.IsNullOrEmpty(text)
           && (Regex.IsMatch(text, "^[0-9A-Fa-f]{6,}$") || text.All(char.IsDigit));

    /// <summary>
    /// Fügt vor Großbuchstaben Leerzeichen ein ("SpotifyMusic" → "Spotify Music") und
    /// entfernt ein angehängtes "Desktop"/"App".
    /// </summary>
    internal static string Prettify(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        // Nur bei "CamelCase" ohne vorhandene Trennzeichen splitten.
        if (!name.Contains(' ') && !name.Contains('-') && !name.Contains('.'))
            name = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");

        foreach (var suffix in new[] { " Desktop", " App" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                name = name[..^suffix.Length];
        }

        return name.Trim();
    }
}
