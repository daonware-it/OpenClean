using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Liest read-only ein vollständiges Inventar ALLER im Browser vorhandenen Cookies
/// (Chrome, Edge, Brave, Firefox), gruppiert nach normalisierter Domain. Anders als
/// <see cref="CookiesProvider"/> wird hier NICHTS ausgeblendet: bereits durch die
/// Whitelist geschützte Domains erscheinen ebenfalls und werden über
/// <see cref="CookieInventoryEntry.IsWhitelisted"/> markiert.
///
/// Gelesen wird – wie beim <see cref="CookiesProvider"/> – immer über eine temporäre
/// Lesekopie (<see cref="BrowserDatabase.CreateReadCopy"/>), robust gegen gesperrte/defekte
/// DBs (try/catch, kein Absturz, finally DeleteReadCopy).
/// </summary>
public sealed class CookieInventoryService
{
    public static CookieInventoryService Instance { get; } = new();

    /// <summary>
    /// Eine Rohzeile aus einer Cookie-DB (vor Normalisierung/Gruppierung): ein Host samt
    /// Anzahl seiner Cookies und – soweit gelesen – deren Namen. <c>host_key</c> und
    /// <c>name</c> stehen in Chromium wie in Firefox im Klartext; nur der Cookie-WERT ist
    /// verschlüsselt. Da für Whitelist und Anzeige nur Domain und Name gebraucht werden,
    /// ist keinerlei Entschlüsselung (DPAPI o. Ä.) nötig.
    /// </summary>
    public readonly record struct CookieRow(
        string? Host, string Browser, long Count, IReadOnlyList<string>? Names = null);

    /// <summary>
    /// Ergebnis eines Inventar-Scans. <see cref="LockedBrowsers"/> nennt die Browser, deren
    /// Cookie-DB exklusiv gesperrt war (Browser läuft) – ohne diese Info sähe der Nutzer nur
    /// eine unerklärlich leere Liste.
    /// </summary>
    public sealed record CookieScanResult(
        IReadOnlyList<CookieInventoryEntry> Entries,
        IReadOnlyList<string> LockedBrowsers,
        int DatabasesRead)
    {
        /// <summary>Leeres Ergebnis (Fallback, wenn der Scan komplett scheitert).</summary>
        public static CookieScanResult Empty { get; } =
            new(Array.Empty<CookieInventoryEntry>(), Array.Empty<string>(), 0);
    }

    /// <summary>Liest alle Cookie-DBs (read-only) und liefert das aggregierte Inventar.</summary>
    public Task<CookieScanResult> ScanAsync() => Task.Run(Scan);

    // ---- Scan ---------------------------------------------------------------

    private CookieScanResult Scan()
    {
        var rows = new List<CookieRow>();
        var locked = new List<string>();
        int read = 0;

        try
        {
            foreach (var (browser, db) in ChromiumCookieDbs())
            {
                if (ScanChromium(rows, browser, db)) read++;
                else locked.Add(browser);
            }

            foreach (var db in FirefoxCookieDbs())
            {
                if (ScanFirefox(rows, db)) read++;
                else locked.Add("Firefox");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OpenClean] Cookie-Inventar-Scan abgebrochen: {ex.Message}");
        }

        var entries = Aggregate(rows, CookieWhitelistService.Instance);
        var lockedDistinct = locked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Trace.WriteLine(
            $"[OpenClean] Cookie-Inventar: {read} DB(s) gelesen, {rows.Count} Rohzeilen, " +
            $"{entries.Count} Domain(s), gesperrt: {(lockedDistinct.Count == 0 ? "-" : string.Join(", ", lockedDistinct))}");

        return new CookieScanResult(entries, lockedDistinct, read);
    }

    /// <summary>Liest eine Chromium-DB. Liefert <c>false</c>, wenn sie nicht lesbar war (gesperrt).</summary>
    private static bool ScanChromium(List<CookieRow> rows, string browser, string dbPath)
    {
        string? copy = BrowserDatabase.CreateReadCopy(dbPath, out var status, allowRawRead: true);
        if (copy is null)
        {
            Trace.WriteLine($"[OpenClean] {browser}: '{dbPath}' nicht lesbar ({status}).");
            return false;
        }
        try
        {
            using var conn = new SqliteConnection(BrowserDatabase.ReadOnlyConnectionString(copy));
            conn.Open();
            using var cmd = conn.CreateCommand();
            // host_key und name im Klartext – der verschlüsselte encrypted_value wird bewusst
            // NICHT gelesen: für Whitelist und Anzeige ist er nicht nötig.
            cmd.CommandText = "SELECT host_key, name FROM cookies ORDER BY host_key";

            using var reader = cmd.ExecuteReader();
            rows.AddRange(ReadHostRows(reader, browser));
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OpenClean] {browser}: Lesen von '{dbPath}' fehlgeschlagen: {ex.Message}");
            return false;
        }
        finally { BrowserDatabase.DeleteReadCopy(copy); }
    }

    /// <summary>Liest eine Firefox-DB. Liefert <c>false</c>, wenn sie nicht lesbar war (gesperrt).</summary>
    private static bool ScanFirefox(List<CookieRow> rows, string dbPath)
    {
        string? copy = BrowserDatabase.CreateReadCopy(dbPath, out var status, allowRawRead: true);
        if (copy is null)
        {
            Trace.WriteLine($"[OpenClean] Firefox: '{dbPath}' nicht lesbar ({status}).");
            return false;
        }
        try
        {
            using var conn = new SqliteConnection(BrowserDatabase.ReadOnlyConnectionString(copy));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host, name FROM moz_cookies ORDER BY host";

            using var reader = cmd.ExecuteReader();
            rows.AddRange(ReadHostRows(reader, "Firefox"));
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OpenClean] Firefox: Lesen von '{dbPath}' fehlgeschlagen: {ex.Message}");
            return false;
        }
        finally { BrowserDatabase.DeleteReadCopy(copy); }
    }

    /// <summary>
    /// Fasst einen Reader über (Host, Name)-Paare zu je einer <see cref="CookieRow"/> pro Host
    /// zusammen. Erwartet nach Host sortierte Zeilen, kommt aber auch mit unsortierten klar.
    /// </summary>
    private static IEnumerable<CookieRow> ReadHostRows(SqliteDataReader reader, string browser)
    {
        var perHost = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            string host = reader.GetString(0);
            string name = reader.IsDBNull(1) ? "" : reader.GetString(1);

            if (!perHost.TryGetValue(host, out var names))
            {
                names = new List<string>();
                perHost[host] = names;
            }
            names.Add(name);
        }

        foreach (var kvp in perHost)
            yield return new CookieRow(kvp.Key, browser, kvp.Value.Count, kvp.Value);
    }

    // ---- Aggregation --------------------------------------------------------

    /// <summary>
    /// Reine, seiteneffektfreie Aggregation (für Tests): normalisiert jede Host-Zeile zu einer
    /// Domain (ungültige/leere via CookieWhitelistService.Normalize == null werden verworfen),
    /// gruppiert nach Domain (OrdinalIgnoreCase), summiert die Anzahl, sammelt die Browser
    /// (distinct, alphabetisch, per ", " verbunden) und markiert IsWhitelisted via
    /// whitelist.Contains(domain). Ergebnis alphabetisch nach Domain (OrdinalIgnoreCase) sortiert.
    /// </summary>
    public static IReadOnlyList<CookieInventoryEntry> Aggregate(
        IEnumerable<CookieRow> rows, CookieWhitelistService whitelist)
    {
        var result = new List<CookieInventoryEntry>();
        if (rows is null) return result;

        try
        {
            var groups = new Dictionary<string, (long Count, List<string> Browsers, List<string> Names)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                string? domain = CookieWhitelistService.Normalize(row.Host);
                if (domain is null) continue;

                if (!groups.TryGetValue(domain, out var agg))
                {
                    agg = (0L, new List<string>(), new List<string>());
                    groups[domain] = agg;
                }

                agg.Count += row.Count;
                if (!string.IsNullOrEmpty(row.Browser))
                    agg.Browsers.Add(row.Browser);
                if (row.Names is not null)
                    agg.Names.AddRange(row.Names.Where(n => !string.IsNullOrEmpty(n)));
                groups[domain] = agg;
            }

            foreach (var kvp in groups)
            {
                string domain = kvp.Key;
                var browsers = string.Join(", ", kvp.Value.Browsers
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(b => b, StringComparer.OrdinalIgnoreCase));

                var names = kvp.Value.Names
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool whitelisted = false;
                try { whitelisted = whitelist?.Contains(domain) ?? false; }
                catch { whitelisted = false; }

                result.Add(new CookieInventoryEntry
                {
                    Domain = domain,
                    Count = kvp.Value.Count,
                    Browsers = browsers,
                    Names = names,
                    IsWhitelisted = whitelisted
                });
            }

            result.Sort((a, b) => string.Compare(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* nie werfen -> Teilergebnis liefern */ }

        return result;
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
}
