using System;
using System.Linq;
using OpenClean.Services;
using Xunit;

namespace OpenClean.Tests;

public sealed class ThemePaletteParityTests
{
    [Fact]
    public void PaletteUri_Existiert_Fuer_Jedes_Theme()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            Uri u = ThemeService.PaletteUri(t);
            Assert.Contains("Themes/", u.OriginalString);
            Assert.EndsWith("Theme.xaml", u.OriginalString);
        }
    }

    [Fact]
    public void Enum_Enthaelt_Alle_Neun_Paletten()
    {
        var names = Enum.GetNames<AppTheme>();
        Assert.Equal(
            new[] { "Dark", "Light", "Cyberpunk", "GamingRgb", "Nord", "Catppuccin", "Dracula", "Dev", "Windows11" },
            names);
    }

    private static string ThemesDir()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "OpenClean.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, "OpenClean", "Themes");
    }

    private static System.Collections.Generic.HashSet<string> KeysOf(string file)
    {
        var doc = System.Xml.Linq.XDocument.Load(file);
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return doc.Descendants()
            .Select(e => (string?)e.Attribute(x + "Key"))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet();
    }

    [Theory]
    [InlineData("CyberpunkTheme.xaml")]
    [InlineData("GamingRgbTheme.xaml")]
    [InlineData("NordTheme.xaml")]
    [InlineData("CatppuccinTheme.xaml")]
    [InlineData("DraculaTheme.xaml")]
    [InlineData("LightTheme.xaml")]
    [InlineData("DevTheme.xaml")]
    [InlineData("Windows11Theme.xaml")]
    public void Palette_Hat_Exakt_Das_Referenz_KeySet(string file)
    {
        var dir = ThemesDir();
        var reference = KeysOf(System.IO.Path.Combine(dir, "DarkTheme.xaml"));
        var actual = KeysOf(System.IO.Path.Combine(dir, file));
        Assert.Equal(reference.OrderBy(k => k), actual.OrderBy(k => k));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(120.0)]
    [InlineData(240.0)]
    [InlineData(359.9)]
    public void HueToColor_Ist_Voll_Deckend_Und_Nicht_Grau(double hue)
    {
        var c = ThemeService.HueToColor(hue);
        Assert.Equal(0xFF, c.A);
        // Voll gesättigt: min- und max-Kanal unterscheiden sich deutlich.
        int max = System.Math.Max(c.R, System.Math.Max(c.G, c.B));
        int min = System.Math.Min(c.R, System.Math.Min(c.G, c.B));
        Assert.True(max - min > 100);
    }
}
