using System.Windows.Media;
using OpenClean.Services.Theming;
using Xunit;

namespace OpenClean.Tests;

public sealed class WindowsAccentTests
{
    [Fact]
    public void Derive_Soft_Hat_Alpha_0x26_Und_Gleiche_RGB()
    {
        var accent = Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
        var (_, _, soft) = WindowsAccent.Derive(accent);
        Assert.Equal(0x26, soft.A);
        Assert.Equal(accent.R, soft.R);
        Assert.Equal(accent.G, soft.G);
        Assert.Equal(accent.B, soft.B);
    }

    [Fact]
    public void Derive_Dark_Ist_Dunkler_Aber_Voll_Deckend()
    {
        var accent = Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
        var (_, dark, _) = WindowsAccent.Derive(accent);
        Assert.Equal(0xFF, dark.A);
        Assert.True(dark.R < accent.R);
        Assert.True(dark.G < accent.G);
        Assert.True(dark.B < accent.B);
    }

    [Fact]
    public void Derive_Gibt_Accent_Unveraendert_Zurueck()
    {
        var accent = Color.FromArgb(0xFF, 0x10, 0x20, 0x30);
        var (a, _, _) = WindowsAccent.Derive(accent);
        Assert.Equal(accent, a);
    }
}
