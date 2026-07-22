using System.IO;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Reines Parsing der VLC-Recent-Listen aus der <c>vlcrc</c>-INI und das Decodieren von
/// MRLs (Media Resource Locators). Zuvor privat im <c>MediaPlayerProvider</c> – als
/// eigenständige, testbare Einheit ausgelagert (Parsing ≠ Registry-/Datei-IO).
/// </summary>
internal static class VlcRecentList
{
    /// <summary>Schlüssel, die (je nach VLC-Version) die Recent-Liste tragen.</summary>
    internal static readonly string[] RecentKeys =
    {
        "recentsmrl-list",
        "qt-recentplay-list"
    };

    /// <summary>
    /// Zerlegt eine <c>vlcrc</c>-Zeile in den (bekannten) Schlüssel und die
    /// komma-getrennten, nicht-leeren Werte. Kommentar-/Leerzeilen und fremde
    /// Schlüssel liefern false.
    /// </summary>
    internal static bool TryParseLine(string line, out string key, out List<string> values)
    {
        key = "";
        values = new List<string>();

        if (string.IsNullOrWhiteSpace(line)) return false;

        string trimmed = line.TrimStart();
        // Kommentare in vlcrc beginnen mit '#'.
        if (trimmed.StartsWith('#')) return false;

        int eq = trimmed.IndexOf('=');
        if (eq <= 0) return false;

        string candidate = trimmed[..eq].Trim();
        if (!RecentKeys.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return false;

        key = candidate;
        string rhs = trimmed[(eq + 1)..];

        foreach (var part in rhs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            values.Add(part);

        return true;
    }

    /// <summary>Wandelt eine MRL (z. B. <c>file:///C:/Filme/x.mp4</c>) in den Dateinamen.</summary>
    internal static string MrlToDisplayName(string mrl)
    {
        try
        {
            string path = MrlToPath(mrl);
            string name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? mrl : name;
        }
        catch { return mrl; }
    }

    /// <summary>Entschlüsselt eine MRL in einen lesbaren Pfad (file://-URIs -> lokaler Pfad).</summary>
    internal static string MrlToPath(string mrl)
    {
        try
        {
            if (Uri.TryCreate(mrl, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;

            return Uri.UnescapeDataString(mrl);
        }
        catch { return mrl; }
    }
}
