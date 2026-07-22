using System;
using System.Collections.Generic;
using System.Linq;
using OpenClean.Models;
using OpenClean.Services.Privacy;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die reine, seiteneffektfreie Such- und Anzeige-Logik rund um die Cookie-Namen ab:
/// <see cref="CookieInventoryEntry.Matches"/> (Filter über Domain UND Cookie-Namen),
/// <see cref="CookieInventoryEntry.NamesPreview"/> / <see cref="CookieInventoryEntry.HasNames"/>
/// sowie die Namens-Zusammenführung in <see cref="CookieInventoryService.Aggregate"/>.
/// Kein Dateisystem, keine echten Browser – der Produktionscode wird nicht verändert.
/// </summary>
public class CookieFilterTests
{
    /// <summary>Erzeugt eine Whitelist über den injizierbaren Konstruktor (keine settings.json).</summary>
    private static CookieWhitelistService Whitelist(params string[] domains)
        => new(new List<string>(domains), () => { });

    /// <summary>Rohzeile mit Cookie-Namen.</summary>
    private static CookieInventoryService.CookieRow Row(
        string? host, string browser, long count, params string[] names)
        => new(host, browser, count, names);

    /// <summary>Rohzeile ohne Namen (alter 3-Parameter-Aufruf).</summary>
    private static CookieInventoryService.CookieRow RowOhneNamen(string? host, string browser, long count)
        => new(host, browser, count);

    /// <summary>Baut einen Eintrag direkt, ohne den Umweg über die Aggregation.</summary>
    private static CookieInventoryEntry Entry(string domain, params string[] names)
        => new()
        {
            Domain = domain,
            Count = names.Length,
            Browsers = "Chrome",
            Names = names
        };

    // ---- A) Matches ----------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_akzeptiert_leeren_Suchbegriff_immer(string? query)
    {
        var entry = Entry("google.com", "LSID", "SID");

        Assert.True(entry.Matches(query));
    }

    [Theory]
    [InlineData("goog")]
    [InlineData("GOOG")]
    [InlineData("GoOgLe.CoM")]
    [InlineData("le.co")]
    public void Matches_trifft_Domain_unabhaengig_von_Grossschreibung(string query)
    {
        var entry = Entry("google.com", "LSID");

        Assert.True(entry.Matches(query));
    }

    [Theory]
    [InlineData("lsid")]
    [InlineData("LSID")]
    [InlineData("sid")]
    public void Matches_trifft_Cookie_Namen_auch_wenn_Domain_nicht_passt(string query)
    {
        var entry = Entry("example.org", "LSID");

        Assert.True(entry.Matches(query));
    }

    [Fact]
    public void Matches_liefert_false_ohne_Treffer()
    {
        var entry = Entry("example.org", "LSID", "SAPISID");

        Assert.False(entry.Matches("facebook"));
        Assert.False(entry.Matches("xyz123"));
    }

    [Fact]
    public void Matches_kommt_mit_Eintrag_ohne_Namen_klar()
    {
        var entry = Entry("example.org");

        Assert.True(entry.Matches("example"));
        Assert.True(entry.Matches(null));
        Assert.False(entry.Matches("lsid"));
    }

    // ---- B) NamesPreview / HasNames -----------------------------------------

    [Fact]
    public void NamesPreview_ist_leer_ohne_Namen()
    {
        var entry = Entry("example.org");

        Assert.Equal("", entry.NamesPreview);
        Assert.False(entry.HasNames);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NamesPreview_zeigt_bis_zu_vier_Namen_vollstaendig(int anzahl)
    {
        var names = Enumerable.Range(1, anzahl).Select(i => $"cookie{i}").ToArray();
        var entry = Entry("example.org", names);

        Assert.True(entry.HasNames);
        Assert.Equal(string.Join(", ", names), entry.NamesPreview);
    }

    [Fact]
    public void NamesPreview_kuerzt_ab_fuenf_Namen_und_nennt_die_Restanzahl()
    {
        // 7 Namen -> die ersten 4 werden gezeigt, 3 bleiben übrig.
        var names = new[] { "aaa", "bbb", "ccc", "ddd", "eee", "fff", "ggg" };
        var entry = Entry("example.org", names);

        string preview = entry.NamesPreview;

        // Die ersten vier Namen stehen im Text …
        Assert.Contains("aaa", preview, StringComparison.Ordinal);
        Assert.Contains("bbb", preview, StringComparison.Ordinal);
        Assert.Contains("ccc", preview, StringComparison.Ordinal);
        Assert.Contains("ddd", preview, StringComparison.Ordinal);

        // … der fünfte nicht mehr.
        Assert.DoesNotContain("eee", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("fff", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("ggg", preview, StringComparison.Ordinal);

        // Die Restanzahl taucht auf; der Suffix-Text selbst ist lokalisiert und
        // wird daher bewusst nicht auf ein Literal geprüft.
        Assert.Contains("3", preview, StringComparison.Ordinal);
        Assert.True(entry.HasNames);
    }

    [Fact]
    public void NamesPreview_zeigt_bei_genau_fuenf_Namen_einen_Rest_von_eins()
    {
        var entry = Entry("example.org", "aaa", "bbb", "ccc", "ddd", "eee");

        string preview = entry.NamesPreview;

        Assert.DoesNotContain("eee", preview, StringComparison.Ordinal);
        Assert.Contains("1", preview, StringComparison.Ordinal);
    }

    // ---- C) Aggregate mit Namen ---------------------------------------------

    [Fact]
    public void Aggregate_fuehrt_Namen_mehrerer_Rows_derselben_Domain_zusammen()
    {
        var rows = new[]
        {
            Row(".google.com", "Chrome", 2, "SID", "HSID"),
            Row("google.com", "Edge", 1, "NID"),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var google = Assert.Single(result);
        Assert.Equal(new[] { "HSID", "NID", "SID" }, google.Names.ToArray());
    }

    [Fact]
    public void Aggregate_dedupliziert_und_sortiert_Namen_alphabetisch()
    {
        var rows = new[]
        {
            Row("example.org", "Chrome", 3, "zeta", "alpha", "zeta"),
            Row("example.org", "Edge", 2, "alpha", "mu"),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var entry = Assert.Single(result);
        Assert.Equal(new[] { "alpha", "mu", "zeta" }, entry.Names.ToArray());
    }

    [Fact]
    public void Aggregate_verwirft_leere_Namen()
    {
        var rows = new[]
        {
            Row("example.org", "Chrome", 3, "", "sid", ""),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var entry = Assert.Single(result);
        Assert.Equal(new[] { "sid" }, entry.Names.ToArray());
    }

    [Fact]
    public void Aggregate_kommt_mit_Rows_ohne_Namen_klar()
    {
        var rows = new[]
        {
            RowOhneNamen("example.org", "Chrome", 4),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var entry = Assert.Single(result);
        Assert.Empty(entry.Names);
        Assert.False(entry.HasNames);
        Assert.Equal(4, entry.Count);
    }

    [Fact]
    public void Aggregate_mischt_Rows_mit_und_ohne_Namen()
    {
        var rows = new[]
        {
            RowOhneNamen("example.org", "Firefox", 2),
            Row("example.org", "Chrome", 1, "sid"),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var entry = Assert.Single(result);
        Assert.Equal(new[] { "sid" }, entry.Names.ToArray());
        Assert.Equal(3, entry.Count);
        Assert.Equal("Chrome, Firefox", entry.Browsers);
    }

    [Fact]
    public void Aggregate_laesst_Count_und_Browser_Aggregation_durch_Namen_unberuehrt()
    {
        var rows = new[]
        {
            Row("google.com", "Chrome", 2, "SID"),
            Row("google.com", "Chrome", 3, "SID", "HSID"),
            Row("google.com", "Edge", 1, "NID"),
            Row("google.com", "Brave", 4, "SID"),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var google = Assert.Single(result);
        Assert.Equal("google.com", google.Domain);
        Assert.Equal(10, google.Count);
        Assert.Equal("Brave, Chrome, Edge", google.Browsers);
        Assert.Equal(new[] { "HSID", "NID", "SID" }, google.Names.ToArray());
    }

    [Fact]
    public void Aggregate_erzeugt_Eintraege_die_ueber_Cookie_Namen_gefunden_werden()
    {
        var rows = new[]
        {
            Row("example.org", "Chrome", 1, "LSID"),
            Row("apple.com", "Chrome", 1, "geo"),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var treffer = result.Where(e => e.Matches("lsid")).ToList();

        var einziger = Assert.Single(treffer);
        Assert.Equal("example.org", einziger.Domain);
    }
}
