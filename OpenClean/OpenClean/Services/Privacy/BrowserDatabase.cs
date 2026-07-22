using System.Diagnostics;
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

    /// <summary>Warum eine Lesekopie nicht zustande kam (bzw. <see cref="Ok"/>).</summary>
    public enum ReadCopyResult
    {
        /// <summary>Kopie steht bereit.</summary>
        Ok,
        /// <summary>Quelldatei existiert nicht (Browser/Profil nicht vorhanden).</summary>
        Missing,
        /// <summary>Quelle ist exklusiv gesperrt – der Browser läuft (Chromium: FileShare.None).</summary>
        Locked,
        /// <summary>Sonstiger Fehler (Rechte, Datenträger, …).</summary>
        Failed
    }

    /// <summary>
    /// Erstellt eine temporäre Kopie der DB (inkl. -wal/-shm, falls vorhanden), damit
    /// auch bei geöffnetem Browser gelesen werden kann. Liefert den Pfad der Kopie
    /// oder null, wenn die DB nicht existiert/nicht kopierbar ist. Der Aufrufer muss
    /// die Kopie anschließend per <see cref="DeleteReadCopy"/> entfernen.
    /// </summary>
    public static string? CreateReadCopy(string dbPath) => CreateReadCopy(dbPath, out _);

    /// <summary>
    /// Wie <see cref="CreateReadCopy(string)"/>, meldet über <paramref name="result"/> aber den
    /// Grund eines Fehlschlags. Wichtig: Chromium hält <c>Cookies</c> exklusiv geöffnet, solange
    /// der Browser läuft – dann ist KEIN Lesen möglich und der Aufrufer muss das dem Nutzer
    /// zeigen, statt still ein leeres Ergebnis zu liefern.
    /// </summary>
    /// <param name="allowRawRead">
    /// Wenn <c>true</c>, wird eine exklusiv gesperrte Datei per Rohzugriff auf das Volume
    /// gelesen (einziger Weg an Chromes Cookie-DB bei laufendem Browser). Bewusst opt-in:
    /// der Zugriff braucht Adminrechte und ist nur dort gerechtfertigt, wo der Nutzer wartet.
    /// </param>
    public static string? CreateReadCopy(string dbPath, out ReadCopyResult result, bool allowRawRead = false)
    {
        string? tempDir = null;
        try
        {
            if (!File.Exists(dbPath))
            {
                result = ReadCopyResult.Missing;
                return null;
            }

            tempDir = Path.Combine(Path.GetTempPath(), "OpenClean", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string copyMain = Path.Combine(tempDir, Path.GetFileName(dbPath));

            // Die Hauptdatei ist Pflicht: schlägt sie fehl, darf KEIN Pfad zurückkommen –
            // sonst öffnet der Aufrufer eine nicht existierende Datei und zählt still 0 Zeilen.
            result = CopyShared(dbPath, copyMain);

            // Gesperrt (laufender Browser) -> zweiter Versuch per Rohzugriff auf das Volume.
            // Bewusst KEINE VSS-Schattenkopie: deren Freigabe zwingt Windows, alle älteren
            // Schattenkopien zu löschen – das vernichtet die Wiederherstellungspunkte des
            // Nutzers (volsnap-Ereignis 95). Der Rohzugriff verändert am System nichts.
            if (result == ReadCopyResult.Locked && allowRawRead && RawVolumeReader.IsAvailable)
            {
                Trace.WriteLine($"[OpenClean] '{dbPath}' gesperrt – versuche Rohzugriff auf das Volume.");
                if (RawVolumeReader.TryCopyRaw(dbPath, copyMain))
                    result = ReadCopyResult.Ok;
            }

            if (result != ReadCopyResult.Ok)
            {
                DeleteReadCopy(copyMain);
                return null;
            }

            // Nebendateien sind optional (best effort).
            if (File.Exists(dbPath + "-wal")) CopyShared(dbPath + "-wal", copyMain + "-wal");
            if (File.Exists(dbPath + "-shm")) CopyShared(dbPath + "-shm", copyMain + "-shm");

            return copyMain;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OpenClean] CreateReadCopy('{dbPath}') unerwartet fehlgeschlagen: {ex.Message}");
            if (tempDir is not null) DeleteReadCopy(Path.Combine(tempDir, "x"));
            result = ReadCopyResult.Failed;
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

    /// <summary>
    /// Kopiert eine (evtl. geöffnete) Datei. Anders als <c>File.Copy</c> wird die Quelle mit
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> geöffnet – so lässt sich mitlesen, solange
    /// der Halter überhaupt Sharing erlaubt (Firefox tut das). Chromium erlaubt es für
    /// <c>Cookies</c> nicht; dann kommt <see cref="ReadCopyResult.Locked"/> zurück.
    /// </summary>
    private static ReadCopyResult CopyShared(string source, string dest)
    {
        try
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
            return ReadCopyResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[OpenClean] '{source}' ist gesperrt/nicht lesbar: {ex.Message}");
            return ReadCopyResult.Locked;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OpenClean] Kopieren von '{source}' fehlgeschlagen: {ex.Message}");
            return ReadCopyResult.Failed;
        }
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
