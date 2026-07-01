using System.IO;
using Microsoft.Data.Sqlite;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 2 (Agent 1): Löscht Cookies von Chrome, Edge, Brave (Chromium-DB
/// <c>Network\Cookies</c> bzw. <c>Cookies</c>, Tabelle <c>cookies</c>) und Firefox
/// (<c>cookies.sqlite</c>, Tabelle <c>moz_cookies</c>).
///
/// Cookies werden nach Host gruppiert und sind einzeln auswählbar. Da das Löschen
/// aktive Anmeldungen beendet, erfordert diese Kategorie eine ausdrückliche
/// Bestätigung (<see cref="RequiresConfirmation"/> = true). Scan liest read-only über
/// eine temporäre Kopie; Clean löscht die Zeilen der ausgewählten Hosts per DELETE.
/// Gesperrte Dateien (Browser offen) werden robust abgefangen – kein Absturz.
/// </summary>
public sealed class CookiesProvider : IPrivacyProvider
{
    public string Name => "Cookies";
    public string Description => "Gespeicherte Cookies je Website – Löschen beendet aktive Anmeldungen.";
    public bool RequiresConfirmation => true;

    public bool IsAvailable => ChromiumCookieDbs().Any() || FirefoxCookieDbs().Any();

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync() => Task.Run(Scan);

    public Task<int> CleanAsync(IEnumerable<PrivacyItem> selected) => Task.Run(() => Clean(selected));

    // ---- Scan ---------------------------------------------------------------

    private IReadOnlyList<PrivacyItem> Scan()
    {
        var items = new List<PrivacyItem>();

        foreach (var (browser, db) in ChromiumCookieDbs())
            ScanChromium(items, browser, db);

        foreach (var db in FirefoxCookieDbs())
            ScanFirefox(items, db);

        return items;
    }

    private static void ScanChromium(List<PrivacyItem> items, string browser, string dbPath)
    {
        string? copy = BrowserDatabase.CreateReadCopy(dbPath);
        if (copy is null) return;
        try
        {
            using var conn = new SqliteConnection(BrowserDatabase.ReadOnlyConnectionString(copy));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT host_key, COUNT(*) FROM cookies GROUP BY host_key ORDER BY host_key";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string host = reader.IsDBNull(0) ? "" : reader.GetString(0);
                long count = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                if (string.IsNullOrWhiteSpace(host)) continue;

                items.Add(new PrivacyItem
                {
                    Name = host,
                    Detail = $"{browser} · {count} Cookie(s)",
                    Tag = new CookieHandle(dbPath, host, IsFirefox: false)
                });
            }
        }
        catch { /* defekte/gesperrte DB -> überspringen, kein Absturz */ }
        finally { BrowserDatabase.DeleteReadCopy(copy); }
    }

    private static void ScanFirefox(List<PrivacyItem> items, string dbPath)
    {
        string? copy = BrowserDatabase.CreateReadCopy(dbPath);
        if (copy is null) return;
        try
        {
            using var conn = new SqliteConnection(BrowserDatabase.ReadOnlyConnectionString(copy));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT host, COUNT(*) FROM moz_cookies GROUP BY host ORDER BY host";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string host = reader.IsDBNull(0) ? "" : reader.GetString(0);
                long count = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                if (string.IsNullOrWhiteSpace(host)) continue;

                items.Add(new PrivacyItem
                {
                    Name = host,
                    Detail = $"Firefox · {count} Cookie(s)",
                    Tag = new CookieHandle(dbPath, host, IsFirefox: true)
                });
            }
        }
        catch { /* defekte/gesperrte DB -> überspringen, kein Absturz */ }
        finally { BrowserDatabase.DeleteReadCopy(copy); }
    }

    // ---- Clean --------------------------------------------------------------

    private int Clean(IEnumerable<PrivacyItem> selected)
    {
        int deleted = 0;

        var byDb = selected
            .Select(i => i.Tag)
            .OfType<CookieHandle>()
            .GroupBy(h => h.DbPath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byDb)
        {
            bool firefox = group.First().IsFirefox;
            try
            {
                using var conn = new SqliteConnection(BrowserDatabase.ReadWriteConnectionString(group.Key));
                conn.Open();
                using var tx = conn.BeginTransaction();

                foreach (var handle in group)
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = firefox
                            ? "DELETE FROM moz_cookies WHERE host = $host"
                            : "DELETE FROM cookies WHERE host_key = $host";
                        cmd.Parameters.AddWithValue("$host", handle.Host);
                        deleted += cmd.ExecuteNonQuery();
                    }
                    catch { /* einzelner Host gesperrt/defekt -> überspringen */ }
                }

                tx.Commit();
            }
            catch { /* DB gesperrt (Browser offen) -> ganze DB überspringen, kein Absturz */ }
        }

        return deleted;
    }

    // ---- Quellen ------------------------------------------------------------

    // Chromium: moderne Ablage unter <Profil>\Network\Cookies, ältere direkt <Profil>\Cookies.
    private static IEnumerable<(string Browser, string DbPath)> ChromiumCookieDbs()
    {
        foreach (var (browser, userData) in ChromiumSources())
        {
            if (!Directory.Exists(userData)) continue;

            string[] profiles;
            try { profiles = Directory.GetDirectories(userData); }
            catch { continue; }

            foreach (var profile in profiles)
            {
                string modern = Path.Combine(profile, "Network", "Cookies");
                string legacy = Path.Combine(profile, "Cookies");
                if (SafeExists(modern)) yield return (browser, modern);
                else if (SafeExists(legacy)) yield return (browser, legacy);
            }
        }
    }

    private static IEnumerable<string> FirefoxCookieDbs()
    {
        foreach (var profile in BrowserDatabase.FirefoxProfileDirs())
        {
            string db = Path.Combine(profile, "cookies.sqlite");
            if (SafeExists(db)) yield return db;
        }
    }

    private static IEnumerable<(string Browser, string UserData)> ChromiumSources()
    {
        yield return ("Chrome", BrowserDatabase.ChromeUserData);
        yield return ("Edge", BrowserDatabase.EdgeUserData);
        yield return ("Brave", BrowserDatabase.BraveUserData);
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    /// <summary>Opaker Lösch-Handle für alle Cookies eines Hosts (Datei + Host + Browser-Familie).</summary>
    private sealed record CookieHandle(string DbPath, string Host, bool IsFirefox);
}
