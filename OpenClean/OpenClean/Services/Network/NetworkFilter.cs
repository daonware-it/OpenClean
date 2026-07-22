namespace OpenClean.Services.Network;

/// <summary>
/// Reine Filterlogik der Netzwerk-Ansicht – bewusst ohne WPF-Bezug, damit sie ohne
/// Oberfläche und ohne Administratorrechte testbar ist.
/// </summary>
public static class NetworkFilter
{
    /// <summary>
    /// True, wenn die Zeile angezeigt werden soll.
    /// </summary>
    /// <param name="processName">Angezeigter Programmname.</param>
    /// <param name="path">Vollständiger Pfad (kann leer/„unbekannt" sein).</param>
    /// <param name="remote">Gegenstelle als Text (Adresse:Port).</param>
    /// <param name="hostName">Aufgelöster Hostname, falls vorhanden.</param>
    /// <param name="isLocalOnly">True, wenn die Verbindung den Rechner nicht verlässt.</param>
    /// <param name="searchText">Freitextsuche; leer = kein Textfilter.</param>
    /// <param name="externalOnly">True = nur Verbindungen nach außen zeigen.</param>
    public static bool Matches(
        string processName, string path, string remote, string hostName,
        bool isLocalOnly, string searchText, bool externalOnly)
    {
        // Kern des Features: „nach außen". Lokales (Loopback, Lauschsockets) ist
        // standardmäßig ausgeblendet, bleibt aber zuschaltbar.
        if (externalOnly && isLocalOnly) return false;

        if (string.IsNullOrWhiteSpace(searchText)) return true;

        string q = searchText.Trim();
        return Contains(processName, q)
               || Contains(path, q)
               || Contains(remote, q)
               || Contains(hostName, q);
    }

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack)
           && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
