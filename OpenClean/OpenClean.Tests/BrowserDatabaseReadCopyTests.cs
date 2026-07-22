using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using OpenClean.Services.Privacy;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt das Kopieren einer Browser-DB in eine temporäre Lesekopie ab
/// (<see cref="BrowserDatabase.CreateReadCopy(string, out BrowserDatabase.ReadCopyResult)"/>).
///
/// Hintergrund: Chromium hält <c>Cookies</c> exklusiv geöffnet (FileShare.None), solange der
/// Browser läuft. Zuvor wurde der Kopierfehler still verschluckt und trotzdem ein Pfad
/// geliefert – der Aufrufer öffnete dann eine nicht existierende Datei und zählte still
/// null Cookies. Eine gesperrte Quelle MUSS als solche gemeldet werden.
/// </summary>
public class BrowserDatabaseReadCopyTests : IDisposable
{
    private readonly string _dir;

    public BrowserDatabaseReadCopyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OpenCleanTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string NewFile(string name, string content = "payload")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- 1) Exklusiv gesperrte Quelle ---------------------------------------

    [Fact]
    public void CreateReadCopy_meldet_Locked_und_liefert_null_wenn_Quelle_exklusiv_gesperrt_ist()
    {
        string db = NewFile("Cookies");

        // Genau das tut Chromium mit seiner Cookies-DB: kein Sharing.
        using var _ = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None);

        string? copy = BrowserDatabase.CreateReadCopy(db, out var result);

        Assert.Null(copy);
        Assert.Equal(BrowserDatabase.ReadCopyResult.Locked, result);
    }

    // ---- 2) Freie Quelle -----------------------------------------------------

    [Fact]
    public void CreateReadCopy_liefert_lesbare_Kopie_bei_freier_Datei()
    {
        string db = NewFile("Cookies", "hello");

        string? copy = BrowserDatabase.CreateReadCopy(db, out var result);

        Assert.Equal(BrowserDatabase.ReadCopyResult.Ok, result);
        Assert.NotNull(copy);
        Assert.True(File.Exists(copy));
        Assert.Equal("hello", File.ReadAllText(copy!));

        BrowserDatabase.DeleteReadCopy(copy);
        Assert.False(File.Exists(copy));
    }

    // ---- 3) Quelle mit Lese-Sharing (Firefox-Fall) ---------------------------

    [Fact]
    public void CreateReadCopy_gelingt_wenn_Quelle_mit_Sharing_geoeffnet_ist()
    {
        string db = NewFile("cookies.sqlite", "ff");

        // Firefox hält die DB offen, erlaubt aber Mitlesen.
        using var _ = new FileStream(db, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        string? copy = BrowserDatabase.CreateReadCopy(db, out var result);

        Assert.Equal(BrowserDatabase.ReadCopyResult.Ok, result);
        Assert.NotNull(copy);
        Assert.Equal("ff", File.ReadAllText(copy!));

        BrowserDatabase.DeleteReadCopy(copy);
    }

    // ---- 4) Fehlende Quelle --------------------------------------------------

    [Fact]
    public void CreateReadCopy_meldet_Missing_wenn_Datei_nicht_existiert()
    {
        string db = Path.Combine(_dir, "gibtsnicht");

        string? copy = BrowserDatabase.CreateReadCopy(db, out var result);

        Assert.Null(copy);
        Assert.Equal(BrowserDatabase.ReadCopyResult.Missing, result);
    }

    // ---- 5) Erfolgspfad: echte SQLite-DB durch die volle Lesekette ----------

    [Fact]
    public void Lesekette_liefert_Cookie_Zeilen_aus_einer_nicht_gesperrten_Chromium_DB()
    {
        // Chromium-förmige DB aufbauen (host_key wie im Original, inkl. führendem Punkt).
        string db = Path.Combine(_dir, "Cookies");
        string seedCs = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        using (var seed = new SqliteConnection(seedCs))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE cookies (host_key TEXT, name TEXT); " +
                "INSERT INTO cookies VALUES ('.google.com','a'),('.google.com','b'),('example.org','c');";
            cmd.ExecuteNonQuery();
        }

        // Exakt der Weg, den CookieInventoryService geht.
        string? copy = BrowserDatabase.CreateReadCopy(db, out var result);
        Assert.Equal(BrowserDatabase.ReadCopyResult.Ok, result);
        Assert.NotNull(copy);

        var rows = new List<CookieInventoryService.CookieRow>();
        try
        {
            using var conn = new SqliteConnection(BrowserDatabase.ReadOnlyConnectionString(copy!));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_key, COUNT(*) FROM cookies GROUP BY host_key ORDER BY host_key";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new CookieInventoryService.CookieRow(reader.GetString(0), "Chrome", reader.GetInt64(1)));
        }
        finally { BrowserDatabase.DeleteReadCopy(copy); }

        var entries = CookieInventoryService.Aggregate(rows, new CookieWhitelistService(new List<string>(), () => { }));

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries.Single(e => e.Domain == "google.com").Count);
        Assert.Equal(1, entries.Single(e => e.Domain == "example.org").Count);
    }

    // ---- 6) Keine Temp-Leichen bei gesperrter Quelle -------------------------

    [Fact]
    public void CreateReadCopy_laesst_bei_Fehlschlag_keinen_Temp_Ordner_zurueck()
    {
        string db = NewFile("Cookies");
        string tempRoot = Path.Combine(Path.GetTempPath(), "OpenClean");
        int before = Directory.Exists(tempRoot) ? Directory.GetDirectories(tempRoot).Length : 0;

        using (new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(BrowserDatabase.CreateReadCopy(db, out _));
        }

        int after = Directory.Exists(tempRoot) ? Directory.GetDirectories(tempRoot).Length : 0;
        Assert.Equal(before, after);
    }
}
