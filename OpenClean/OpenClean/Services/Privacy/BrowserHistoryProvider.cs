using System.IO;
using Microsoft.Data.Sqlite;
using OpenClean.Models;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 1 (Agent 1): Löscht den Browser-Verlauf von Chrome, Edge, Brave (Chromium,
/// Tabellen <c>urls</c>/<c>visits</c>) und Firefox (<c>places.sqlite</c>,
/// Tabellen <c>moz_places</c>/<c>moz_historyvisits</c>).
///
/// Scan liest die zuletzt besuchten Einträge read-only über eine temporäre Kopie
/// (funktioniert auch bei geöffnetem Browser). Clean löscht die ausgewählten Zeilen
/// per DELETE aus der echten DB. Gesperrte Dateien (Browser offen) werden robust
/// abgefangen – der betroffene Eintrag gilt als übersprungen, es gibt NIE einen Absturz.
/// </summary>
public sealed class BrowserHistoryProvider : IPrivacyProvider
{
    // Wie viele der neuesten Einträge je Profil gelistet werden (Vorschau-Grenze).
    private const int MaxEntriesPerProfile = 300;

    public string Name => "Browser-Verlauf";
    public string Description => "Zuletzt besuchte Webseiten in Chrome, Edge, Brave und Firefox.";
    public bool RequiresConfirmation => false;

    public bool IsAvailable =>
        BrowserDatabase.ChromiumProfiles(BrowserDatabase.ChromeUserData, "History").Any()
        || BrowserDatabase.ChromiumProfiles(BrowserDatabase.EdgeUserData, "History").Any()
        || BrowserDatabase.ChromiumProfiles(BrowserDatabase.BraveUserData, "History").Any()
        || FirefoxHistoryDbs().Any();

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync() => Task.Run(Scan);

    public Task<int> CleanAsync(IEnumerable<PrivacyItem> selected) => Task.Run(() => Clean(selected));

    // ---- Scan ---------------------------------------------------------------

    private IReadOnlyList<PrivacyItem> Scan()
    {
        var items = new List<PrivacyItem>();

        // Chromium-Browser (Chrome/Edge/Brave)
        foreach (var (browser, userData) in ChromiumSources())
        {
            foreach (var profile in BrowserDatabase.ChromiumProfiles(userData, "History"))
            {
                string db = Path.Combine(profile, "History");
                ScanChromium(items, browser, db);
            }
        }

        // Firefox
        foreach (var db in FirefoxHistoryDbs())
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
                "SELECT id, url, title, last_visit_time FROM urls " +
                "ORDER BY last_visit_time DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", MaxEntriesPerProfile);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string url = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string title = reader.IsDBNull(2) ? "" : reader.GetString(2);
                long time = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                items.Add(new PrivacyItem
                {
                    Name = string.IsNullOrWhiteSpace(title) ? url : title,
                    Detail = BuildDetail(browser, url, BrowserDatabase.FromChromiumTime(time)),
                    Tag = new HistoryHandle(dbPath, id, IsFirefox: false)
                });
            }
        }
        catch { /* defekte/gesperrte DB -> Profil überspringen, kein Absturz */ }
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
                "SELECT id, url, title, last_visit_date FROM moz_places " +
                "WHERE last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", MaxEntriesPerProfile);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string url = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string title = reader.IsDBNull(2) ? "" : reader.GetString(2);
                long time = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                items.Add(new PrivacyItem
                {
                    Name = string.IsNullOrWhiteSpace(title) ? url : title,
                    Detail = BuildDetail("Firefox", url, BrowserDatabase.FromUnixMicroseconds(time)),
                    Tag = new HistoryHandle(dbPath, id, IsFirefox: true)
                });
            }
        }
        catch { /* defekte/gesperrte DB -> Profil überspringen, kein Absturz */ }
        finally { BrowserDatabase.DeleteReadCopy(copy); }
    }

    private static string BuildDetail(string browser, string url, DateTime? time)
    {
        string when = time.HasValue ? time.Value.ToString("dd.MM.yyyy HH:mm") : "unbekannt";
        return $"{browser} · {when} · {url}";
    }

    // ---- Clean --------------------------------------------------------------

    private int Clean(IEnumerable<PrivacyItem> selected)
    {
        int deleted = 0;

        // Nach DB-Datei gruppieren: pro DB genau eine Verbindung öffnen.
        var byDb = selected
            .Select(i => i.Tag)
            .OfType<HistoryHandle>()
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
                        deleted += firefox
                            ? DeleteFirefoxRow(conn, tx, handle.RowId)
                            : DeleteChromiumRow(conn, tx, handle.RowId);
                    }
                    catch { /* einzelne Zeile gesperrt/defekt -> überspringen */ }
                }

                tx.Commit();
            }
            catch { /* DB gesperrt (Browser offen) -> ganze DB überspringen, kein Absturz */ }
        }

        return deleted;
    }

    private static int DeleteChromiumRow(SqliteConnection conn, SqliteTransaction tx, long urlId)
    {
        // Zuerst die Besuche, dann die URL-Zeile (visits.url -> urls.id).
        using (var v = conn.CreateCommand())
        {
            v.Transaction = tx;
            v.CommandText = "DELETE FROM visits WHERE url = $id";
            v.Parameters.AddWithValue("$id", urlId);
            v.ExecuteNonQuery();
        }
        using var u = conn.CreateCommand();
        u.Transaction = tx;
        u.CommandText = "DELETE FROM urls WHERE id = $id";
        u.Parameters.AddWithValue("$id", urlId);
        return u.ExecuteNonQuery();
    }

    private static int DeleteFirefoxRow(SqliteConnection conn, SqliteTransaction tx, long placeId)
    {
        // Zuerst die Besuche, dann die Place-Zeile (moz_historyvisits.place_id -> moz_places.id).
        using (var v = conn.CreateCommand())
        {
            v.Transaction = tx;
            v.CommandText = "DELETE FROM moz_historyvisits WHERE place_id = $id";
            v.Parameters.AddWithValue("$id", placeId);
            v.ExecuteNonQuery();
        }
        using var p = conn.CreateCommand();
        p.Transaction = tx;
        p.CommandText = "DELETE FROM moz_places WHERE id = $id";
        p.Parameters.AddWithValue("$id", placeId);
        return p.ExecuteNonQuery();
    }

    // ---- Quellen ------------------------------------------------------------

    private static IEnumerable<(string Browser, string UserData)> ChromiumSources()
    {
        yield return ("Chrome", BrowserDatabase.ChromeUserData);
        yield return ("Edge", BrowserDatabase.EdgeUserData);
        yield return ("Brave", BrowserDatabase.BraveUserData);
    }

    private static IEnumerable<string> FirefoxHistoryDbs()
    {
        foreach (var profile in BrowserDatabase.FirefoxProfileDirs())
        {
            string db = Path.Combine(profile, "places.sqlite");
            bool exists;
            try { exists = File.Exists(db); }
            catch { exists = false; }
            if (exists) yield return db;
        }
    }

    /// <summary>Opaker Lösch-Handle für eine Verlaufs-Zeile (Datei + Zeilen-ID + Browser-Familie).</summary>
    private sealed record HistoryHandle(string DbPath, long RowId, bool IsFirefox);
}
