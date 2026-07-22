using System;
using System.Collections.Generic;
using OpenClean.Services.Privacy;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die reine Logik der Cookie-Whitelist ab: Normalisierung von Nutzereingaben zu Domains,
/// Host-Matching (inkl. Subdomains und führendem Punkt), sowie das Verwalten der Liste
/// (Add/Remove/Contains/Domains) über den injizierbaren Konstruktor.
///
/// Bewusst NICHT getestet: die echte Persistierung nach settings.json. Der Dienst wird hier an
/// eine frische <see cref="List{T}"/> und einen Zähler-Delegaten gehängt, sodass keine echten
/// Einstellungen geschrieben werden; geprüft wird nur, DASS persist zum richtigen Zeitpunkt läuft.
/// </summary>
public class CookieWhitelistTests
{
    /// <summary>Erzeugt einen frischen Dienst über einer In-Memory-Liste mit Zähler für persist.</summary>
    private static CookieWhitelistService NewService(out List<string> store, out Func<int> persistCalls)
    {
        var s = new List<string>();
        int calls = 0;
        var svc = new CookieWhitelistService(s, () => calls++);
        store = s;
        persistCalls = () => calls;
        return svc;
    }

    // ---- Normalize -------------------------------------------------------

    [Theory]
    [InlineData("https://WWW.Google.com/pfad?x=1", "google.com")]
    [InlineData("http://www.google.com", "google.com")]
    [InlineData(".accounts.google.com:443", "accounts.google.com")]
    [InlineData("Google.COM", "google.com")]
    [InlineData("  example.org  ", "example.org")]
    [InlineData("www.example.org", "example.org")]
    [InlineData("example.org:8080", "example.org")]
    [InlineData("example.org/path/to/thing", "example.org")]
    [InlineData("sub.domain.example.co.uk", "sub.domain.example.co.uk")]
    public void Normalize_GueltigeEingaben_LiefernBereinigteDomain(string input, string expected)
    {
        Assert.Equal(expected, CookieWhitelistService.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("http://")]
    [InlineData("www.")]
    public void Normalize_UngueltigeEingaben_LiefernNull(string? input)
    {
        Assert.Null(CookieWhitelistService.Normalize(input));
    }

    // ---- HostMatches -----------------------------------------------------

    [Theory]
    [InlineData("google.com", "google.com")]              // Gleichheit
    [InlineData(".google.com", "google.com")]             // führender Punkt am Host
    [InlineData("accounts.google.com", "google.com")]     // echte Subdomain
    [InlineData(".accounts.google.com", "google.com")]    // Subdomain mit führendem Punkt
    [InlineData("GOOGLE.COM", "google.com")]              // Groß-/Kleinschreibung
    public void HostMatches_TrefferFaelle_LiefernTrue(string host, string domain)
    {
        Assert.True(CookieWhitelistService.HostMatches(host, domain));
    }

    [Theory]
    [InlineData("notgoogle.com", "google.com")]           // gleicher Suffix, aber keine Subdomain
    [InlineData("google.com.evil.com", "google.com")]     // Domain nur im Präfix
    [InlineData("example.com", "google.com")]             // völlig unabhängig
    public void HostMatches_NichtTreffer_LiefernFalse(string host, string domain)
    {
        Assert.False(CookieWhitelistService.HostMatches(host, domain));
    }

    // ---- Add -------------------------------------------------------------

    [Fact]
    public void Add_NeueDomain_LiefertTrueUndRuftPersist()
    {
        var svc = NewService(out var store, out var persistCalls);

        Assert.True(svc.Add("example.org"));
        Assert.Contains("example.org", store);
        Assert.Equal(1, persistCalls());
    }

    [Fact]
    public void Add_Duplikat_WirdAbgelehntUndPersistiertNichtErneut()
    {
        var svc = NewService(out var store, out var persistCalls);
        svc.Add("example.org");

        // Gleiche Domain, andere Schreibweise/Form -> nach Normalisierung identisch.
        Assert.False(svc.Add("https://WWW.Example.ORG/pfad"));
        Assert.Single(store);
        Assert.Equal(1, persistCalls());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    public void Add_UngueltigeEingabe_WirdAbgelehnt(string? input)
    {
        var svc = NewService(out var store, out var persistCalls);

        Assert.False(svc.Add(input));
        Assert.Empty(store);
        Assert.Equal(0, persistCalls());
    }

    [Fact]
    public void Add_NormalisierungWirkt_SubdomainDanachGeschuetzt()
    {
        var svc = NewService(out _, out _);

        Assert.True(svc.Add("https://WWW.Google.com/"));
        Assert.True(svc.Contains("accounts.google.com"));
    }

    // ---- Remove ----------------------------------------------------------

    [Fact]
    public void Remove_VorhandeneDomain_LiefertTrueUndRuftPersist()
    {
        var svc = NewService(out var store, out var persistCalls);
        svc.Add("example.org");
        int callsBefore = persistCalls();

        // Entfernen mit abweichender Form -> normalisiert identisch.
        Assert.True(svc.Remove("WWW.Example.org"));
        Assert.Empty(store);
        Assert.Equal(callsBefore + 1, persistCalls());
    }

    [Fact]
    public void Remove_NichtVorhanden_LiefertFalse()
    {
        var svc = NewService(out _, out var persistCalls);

        Assert.False(svc.Remove("example.org"));
        Assert.Equal(0, persistCalls());
    }

    // ---- Contains --------------------------------------------------------

    [Fact]
    public void Contains_SubdomainUndFuehrenderPunkt_SindGeschuetzt()
    {
        var svc = NewService(out _, out _);
        svc.Add("google.com");

        Assert.True(svc.Contains("google.com"));
        Assert.True(svc.Contains("accounts.google.com"));
        Assert.True(svc.Contains(".google.com"));
    }

    [Fact]
    public void Contains_UnabhaengigerHost_IstNichtGeschuetzt()
    {
        var svc = NewService(out _, out _);
        svc.Add("google.com");

        Assert.False(svc.Contains("notgoogle.com"));
        Assert.False(svc.Contains("example.com"));
    }

    // ---- Domains ---------------------------------------------------------

    [Fact]
    public void Domains_LiefertAlphabetischSortiert()
    {
        var svc = NewService(out _, out _);
        svc.Add("zebra.com");
        svc.Add("alpha.com");
        svc.Add("mango.org");

        Assert.Equal(new[] { "alpha.com", "mango.org", "zebra.com" }, svc.Domains);
    }
}
