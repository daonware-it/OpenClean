using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenClean.Services.Privacy;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Sichert den Vertrag von <see cref="CookieInventoryService.CookieScanResult"/> ab: Ein Scan,
/// bei dem eine Cookie-DB gesperrt ist, muss den betroffenen Browser melden – früher lieferte
/// der Dienst still eine leere Liste, und die UI zeigte unerklärlich „nichts gefunden".
/// </summary>
public class CookieScanResultTests
{
    [Fact]
    public void Empty_liefert_leeres_Ergebnis_ohne_Sperren()
    {
        var empty = CookieInventoryService.CookieScanResult.Empty;

        Assert.Empty(empty.Entries);
        Assert.Empty(empty.LockedBrowsers);
        Assert.Equal(0, empty.DatabasesRead);
    }

    [Fact]
    public async Task ScanAsync_wirft_nie_und_liefert_immer_ein_Ergebnis()
    {
        // Läuft gegen die echten Profile der Maschine: egal ob Browser offen, geschlossen
        // oder gar nicht installiert – es muss ein Ergebnis herauskommen, keine Ausnahme.
        var result = await CookieInventoryService.Instance.ScanAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Entries);
        Assert.NotNull(result.LockedBrowsers);
        Assert.True(result.DatabasesRead >= 0);
    }

    [Fact]
    public async Task ScanAsync_meldet_entweder_Eintraege_oder_gesperrte_Browser()
    {
        var result = await CookieInventoryService.Instance.ScanAsync();

        // Der Kern des Fixes: ein leeres Inventar ohne jede gelesene DB darf nicht mehr
        // kommentarlos passieren – dann muss mindestens ein gesperrter Browser benannt sein.
        if (result.Entries.Count == 0 && result.DatabasesRead == 0 && HasAnyCookieDb())
            Assert.NotEmpty(result.LockedBrowsers);
    }

    [Fact]
    public async Task ScanAsync_meldet_gesperrte_Browser_ohne_Duplikate_und_sortiert()
    {
        var result = await CookieInventoryService.Instance.ScanAsync();

        Assert.Equal(result.LockedBrowsers.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            result.LockedBrowsers.Count);
        Assert.Equal(
            result.LockedBrowsers.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToArray(),
            result.LockedBrowsers.ToArray());
    }

    /// <summary>Ob auf dieser Maschine überhaupt eine Chromium-Cookie-DB existiert.</summary>
    private static bool HasAnyCookieDb()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] roots =
        {
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var profile in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(profile, "Network", "Cookies"))) return true;
                if (File.Exists(Path.Combine(profile, "Cookies"))) return true;
            }
        }
        return false;
    }
}
