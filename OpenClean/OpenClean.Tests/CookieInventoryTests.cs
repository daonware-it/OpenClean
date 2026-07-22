using System;
using System.Collections.Generic;
using System.Linq;
using OpenClean.Models;
using OpenClean.Services.Privacy;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die reine, seiteneffektfreie Aggregations-Logik von
/// <see cref="CookieInventoryService.Aggregate"/> ab: Host-Normalisierung, Gruppierung
/// nach Domain, Summierung der Cookie-Anzahl, Browser-Sammlung (distinct/sortiert),
/// Whitelist-Markierung und die alphabetische Endsortierung. Der Dienst ist korrekt und
/// wird nicht verändert; getestet wird ausschließlich über den öffentlichen Vertrag.
/// </summary>
public class CookieInventoryTests
{
    /// <summary>Erzeugt eine Whitelist über den injizierbaren Konstruktor (keine settings.json).</summary>
    private static CookieWhitelistService Whitelist(params string[] domains)
        => new(new List<string>(domains), () => { });

    private static CookieInventoryService.CookieRow Row(string? host, string browser, long count)
        => new(host, browser, count);

    // ---- 1) Gruppierung + Normalisierung ------------------------------------

    [Fact]
    public void Aggregate_gruppiert_und_normalisiert_Hosts_zu_Domains()
    {
        var rows = new[]
        {
            Row(".google.com", "Chrome", 3),
            Row("google.com", "Edge", 1),
            Row("accounts.google.com", "Firefox", 2),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        Assert.Equal(2, result.Count);

        var google = result.Single(e => e.Domain == "google.com");
        Assert.Equal(4, google.Count);
        Assert.Equal("Chrome, Edge", google.Browsers);

        var accounts = result.Single(e => e.Domain == "accounts.google.com");
        Assert.Equal(2, accounts.Count);
        Assert.Equal("Firefox", accounts.Browsers);
    }

    // ---- 2) Ungültige Hosts werden verworfen --------------------------------

    [Fact]
    public void Aggregate_verwirft_ungueltige_Hosts()
    {
        var rows = new[]
        {
            Row("", "Chrome", 5),
            Row("localhost", "Edge", 7),
            Row(null, "Firefox", 9),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData(null)]
    public void Aggregate_erzeugt_keinen_Eintrag_fuer_ungueltigen_Host(string? host)
    {
        var rows = new[] { Row(host, "Chrome", 3) };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        Assert.Empty(result);
    }

    // ---- 3) IsWhitelisted-Markierung (inkl. Subdomains) ---------------------

    [Fact]
    public void Aggregate_markiert_Domain_und_Subdomain_als_whitelisted()
    {
        var rows = new[]
        {
            Row("google.com", "Chrome", 1),
            Row("accounts.google.com", "Chrome", 1),
            Row("example.org", "Chrome", 1),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist("google.com"));

        var google = result.Single(e => e.Domain == "google.com");
        Assert.True(google.IsWhitelisted);
        Assert.False(google.CanAdd);

        var accounts = result.Single(e => e.Domain == "accounts.google.com");
        Assert.True(accounts.IsWhitelisted);
        Assert.False(accounts.CanAdd);

        var example = result.Single(e => e.Domain == "example.org");
        Assert.False(example.IsWhitelisted);
        Assert.True(example.CanAdd);
    }

    // ---- 4) Browser distinct + sortiert, Counts summiert --------------------

    [Fact]
    public void Aggregate_dedupliziert_und_sortiert_Browser_und_summiert_Counts()
    {
        var rows = new[]
        {
            Row("google.com", "Chrome", 2), // Profil 1
            Row("google.com", "Chrome", 3), // Profil 2, gleicher Browser
            Row("google.com", "Edge", 1),
            Row("google.com", "Brave", 4),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var google = Assert.Single(result);
        Assert.Equal("google.com", google.Domain);
        Assert.Equal(10, google.Count);
        // distinct (Chrome nur einmal) und alphabetisch sortiert
        Assert.Equal("Brave, Chrome, Edge", google.Browsers);
    }

    // ---- 5) Sortierung des Ergebnisses --------------------------------------

    [Fact]
    public void Aggregate_sortiert_Ergebnis_alphabetisch_nach_Domain()
    {
        var rows = new[]
        {
            Row("zulip.com", "Chrome", 1),
            Row("apple.com", "Chrome", 1),
            Row("microsoft.com", "Chrome", 1),
            Row("github.com", "Chrome", 1),
        };

        var result = CookieInventoryService.Aggregate(rows, Whitelist());

        var domains = result.Select(e => e.Domain).ToArray();
        Assert.Equal(new[] { "apple.com", "github.com", "microsoft.com", "zulip.com" }, domains);
    }

    // ---- 6) Leere Eingabe ----------------------------------------------------

    [Fact]
    public void Aggregate_liefert_bei_leerer_Eingabe_leere_Liste()
    {
        var result = CookieInventoryService.Aggregate(
            Array.Empty<CookieInventoryService.CookieRow>(), Whitelist());

        Assert.Empty(result);
    }
}
