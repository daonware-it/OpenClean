namespace OpenClean.Models;

/// <summary>
/// Ein installiertes Programm, gelesen aus den Windows-Uninstall-Registry-Schlüsseln
/// (derselbe Weg, den auch „Apps &amp; Features" nutzt). Die Klasse LÖSCHT/verändert
/// nichts – sie beschreibt nur, was installiert ist.
/// </summary>
public sealed class InstalledApp
{
    /// <summary>Anzeigename (DisplayName).</summary>
    public required string Name { get; init; }

    /// <summary>Version (DisplayVersion), ggf. leer.</summary>
    public string Version { get; init; } = "";

    /// <summary>Herausgeber (Publisher), ggf. leer.</summary>
    public string Publisher { get; init; } = "";

    /// <summary>Installationsordner (InstallLocation), ggf. leer.</summary>
    public string InstallLocation { get; init; } = "";

    /// <summary>
    /// Pfad zur Datei, aus der das Programm-Icon extrahiert werden kann (aus DisplayIcon,
    /// ohne Icon-Index; meist die Haupt-EXE oder eine .ico-Datei). Leer, wenn keine
    /// brauchbare Quelle vorliegt – dann wird ein Buchstaben-Avatar angezeigt.
    /// </summary>
    public string IconPath { get; init; } = "";

    /// <summary>
    /// Ordner, aus dem die Größe berechnet werden darf: InstallLocation oder – falls
    /// diese fehlt/unsicher ist – abgeleitet aus DisplayIcon bzw. Uninstaller-Pfad.
    /// NUR für die Größenberechnung; die Reste-Suche nutzt weiterhin InstallLocation,
    /// damit ein geratener Ordner (z. B. Chrome bei Web-Apps) nie zur Löschung vorgeschlagen wird.
    /// </summary>
    public string SizeFolder { get; init; } = "";

    /// <summary>Geschätzte belegte Größe in Bytes (aus EstimatedSize, KB → Bytes). 0 = unbekannt.</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>Installationsdatum, falls im Registry-Format (yyyymmdd) lesbar.</summary>
    public DateTime? InstallDate { get; init; }

    /// <summary>Kommando zum Deinstallieren (UninstallString).</summary>
    public string UninstallString { get; init; } = "";

    /// <summary>Stilles Deinstallations-Kommando (QuietUninstallString), falls vorhanden.</summary>
    public string QuietUninstallString { get; init; } = "";

    /// <summary>MSI-Produktcode ({GUID}), falls es sich um ein Windows-Installer-Paket handelt.</summary>
    public string? MsiProductCode { get; init; }

    /// <summary>True, wenn überhaupt ein Deinstallations-Kommando bekannt ist.</summary>
    public bool CanUninstall =>
        !string.IsNullOrWhiteSpace(UninstallString) ||
        !string.IsNullOrWhiteSpace(QuietUninstallString) ||
        !string.IsNullOrWhiteSpace(MsiProductCode);
}
