using System;
using System.IO;

namespace OpenClean.Services;

/// <summary>
/// Zentrale Auflösung des Datenverzeichnisses (Einstellungen, Protokolle, Berichte).
/// Unterscheidet zwei Betriebsarten:
/// <list type="bullet">
///   <item><b>Portable</b> – erkannt an der Marker-Datei <c>OpenClean.portable</c>
///   neben der EXE: Daten liegen im Unterordner <c>Data\</c> direkt neben der EXE,
///   sodass sich die App komplett vom USB-Stick verwenden lässt (v0.11.0).</item>
///   <item><b>Installiert</b> – Standardfall ohne Marker: Daten liegen wie bisher
///   unter <c>%AppData%\OpenClean\</c>.</item>
/// </list>
/// Ersetzt die zuvor an drei Stellen duplizierte Pfadbildung. Alle Konsumenten
/// (SettingsService, AutoCleanReportStore, UpdateLogService) beziehen ihren Pfad hierüber.
/// </summary>
public static class AppPaths
{
    /// <summary>Name der Marker-Datei, deren Vorhandensein neben der EXE den Portable-Modus aktiviert.</summary>
    public const string PortableMarkerFileName = "OpenClean.portable";

    private const string AppFolderName = "OpenClean";

    // Einmalig ausgewertet: Betriebsart und Datenverzeichnis ändern sich zur Laufzeit nicht.
    private static readonly bool _isPortable = DetectPortable();
    private static readonly string _dataDirectory = ResolveDataDirectory();

    /// <summary>
    /// Verzeichnis der laufenden EXE. Nutzt <see cref="AppContext.BaseDirectory"/>, da
    /// dies auch bei einem Single-File-Publish korrekt aufgelöst wird
    /// (<c>Assembly.Location</c> ist dort leer).
    /// </summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary><c>true</c>, wenn die App im Portable-Modus läuft (Marker-Datei neben der EXE).</summary>
    public static bool IsPortable => _isPortable;

    /// <summary>
    /// Verzeichnis für persistente App-Daten. Portable: <c>&lt;EXE-Ordner&gt;\Data</c>,
    /// sonst <c>%AppData%\OpenClean</c>. Das Verzeichnis wird bei Bedarf von den
    /// Konsumenten (bzw. <see cref="GetDataFile"/>) angelegt.
    /// </summary>
    public static string DataDirectory => _dataDirectory;

    /// <summary>
    /// Vollständiger Pfad einer Datei im Datenverzeichnis. Legt das Verzeichnis defensiv
    /// an; ein Fehler dabei wird geschluckt (die aufrufenden Dienste behandeln IO-Fehler
    /// ohnehin selbst und fallen auf Sitzungs-Defaults zurück).
    /// </summary>
    public static string GetDataFile(string fileName)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
        }
        catch
        {
            // Nicht anlegbar (Rechte/Datenträger) -> Aufrufer behandelt Schreibfehler selbst.
        }

        return Path.Combine(_dataDirectory, fileName);
    }

    private static bool DetectPortable()
    {
        try
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName));
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveDataDirectory()
    {
        if (_isPortable)
            return Path.Combine(AppContext.BaseDirectory, "Data");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName);
    }
}
