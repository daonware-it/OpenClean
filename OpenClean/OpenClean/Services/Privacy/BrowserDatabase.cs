using System.IO;
using Microsoft.Data.Sqlite;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Geteilte Hilfsfunktionen für den Zugriff auf Browser-SQLite-Datenbanken
/// (Verlauf, Cookies). Kapselt die bekannten Basispfade der unterstützten Browser,
/// das gefahrlose Lesen einer eventuell gesperrten DB (über eine temporäre Kopie)
/// sowie die Zeitstempel-Umrechnung.
///
/// Owner: Agent 1. Wird von <see cref="BrowserHistoryProvider"/> und
/// <see cref="CookiesProvider"/> genutzt.
/// </summary>
internal static class BrowserDatabase
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // ---- Basispfade der Browser (User-Data-Wurzeln) -------------------------

    public static string ChromeUserData => Path.Combine(Local, "Google", "Chrome", "User Data");
    public static string EdgeUserData => Path.Combine(Local, "Microsoft", "Edge", "User Data");
    public static string BraveUserData => Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data");
    public static string FirefoxProfiles => Path.Combine(Roaming, "Mozilla", "Firefox", "Profiles");

    /// <summary>
    /// Liefert die Chromium-Profilordner unterhalb einer User-Data-Wurzel
    /// ("Default", "Profile 1", …). Nur Ordner, die die gesuchte Datei enthalten.
    /// </summary>
    public static IEnumerable<string> ChromiumProfiles(string userData, string relativeFile)
    {
        if (!Directory.Exists(userData)) yield break;

        string[] dirs;
        try { dirs = Directory.GetDirectories(userData); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            string file = Path.Combine(dir, relativeFile);
            bool exists;
            try { exists = File.Exists(file); }
            catch { exists = false; }
            if (exists) yield return dir;
        }
    }

    /// <summary>Liefert die Firefox-Profilordner unter %AppData%\Mozilla\Firefox\Profiles.</summary>
    public static IEnumerable<string> FirefoxProfileDirs()
    {
        if (!Directory.Exists(FirefoxProfiles)) return Array.Empty<string>();
        try { return Directory.GetDirectories(FirefoxProfiles); }
        catch { return Array.Empty<string>(); }
    }

    // ---- Verbindungs-Strings ------------------------------------------------

    public static string ReadOnlyConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();

    public static string ReadWriteConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private
        }.ToString();

    // ---- Gefahrloses Lesen einer evtl. gesperrten DB ------------------------

    /// <summary>
    /// Erstellt eine temporäre Kopie der DB (inkl. -wal/-shm, falls vorhanden), damit
    /// auch bei geöffnetem Browser gelesen werden kann. Liefert den Pfad der Kopie
    /// oder null, wenn die DB nicht existiert/nicht kopierbar ist. Der Aufrufer muss
    /// die Kopie anschließend per <see cref="DeleteReadCopy"/> entfernen.
    /// </summary>
    public static string? CreateReadCopy(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath)) return null;

            string tempDir = Path.Combine(Path.GetTempPath(), "OpenClean", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string copyMain = Path.Combine(tempDir, Path.GetFileName(dbPath));

            CopyIfExists(dbPath, copyMain);
            CopyIfExists(dbPath + "-wal", copyMain + "-wal");
            CopyIfExists(dbPath + "-shm", copyMain + "-shm");

            return copyMain;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Entfernt die zuvor erstellte Lesekopie samt Nebendateien (best effort).</summary>
    public static void DeleteReadCopy(string? copyMainPath)
    {
        if (string.IsNullOrEmpty(copyMainPath)) return;
        try
        {
            string? dir = Path.GetDirectoryName(copyMainPath);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* Aufräumen best effort – Temp-Ordner wird ohnehin vom OS bereinigt */ }
    }

    private static void CopyIfExists(string source, string dest)
    {
        try
        {
            if (File.Exists(source))
                File.Copy(source, dest, overwrite: true);
        }
        catch { /* gesperrte Nebendatei -> überspringen, Hauptdatei genügt meist */ }
    }

    // ---- Zeitstempel --------------------------------------------------------

    /// <summary>
    /// Wandelt einen Chromium-Zeitstempel (Mikrosekunden seit 1601-01-01 UTC) in lokale Zeit.
    /// Liefert null bei 0/ungültig.
    /// </summary>
    public static DateTime? FromChromiumTime(long microseconds)
    {
        if (microseconds <= 0) return null;
        try
        {
            var epoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddTicks(microseconds * 10).ToLocalTime();
        }
        catch { return null; }
    }

    /// <summary>
    /// Wandelt einen Firefox-Zeitstempel (Mikrosekunden seit Unix-Epoche) in lokale Zeit.
    /// Liefert null bei 0/ungültig.
    /// </summary>
    public static DateTime? FromUnixMicroseconds(long microseconds)
    {
        if (microseconds <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000).LocalDateTime;
        }
        catch { return null; }
    }
}
