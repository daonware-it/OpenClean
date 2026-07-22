using OpenClean.Services.Privacy;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Sichert das aus <c>MediaPlayerProvider</c> ausgelagerte VLC-Parsing ab
/// (vlcrc-Zeilen + MRL-Decodierung).
/// </summary>
public class VlcRecentListTests
{
    [Fact]
    public void TryParseLine_ErkenntRecentListe_UndTeiltWerte()
    {
        bool ok = VlcRecentList.TryParseLine(
            "recentsmrl-list=file:///C:/a.mp4, file:///C:/b.mp4", out var key, out var values);

        Assert.True(ok);
        Assert.Equal("recentsmrl-list", key);
        Assert.Equal(2, values.Count);
        Assert.Equal("file:///C:/a.mp4", values[0]); // TrimEntries entfernt das führende Leerzeichen
    }

    [Fact]
    public void TryParseLine_LeereEintraege_WerdenVerworfen()
    {
        VlcRecentList.TryParseLine("qt-recentplay-list=a,,b", out _, out var values);
        Assert.Equal(new[] { "a", "b" }, values);
    }

    [Theory]
    [InlineData("# Kommentarzeile")]
    [InlineData("andere-option=wert")] // kein bekannter Recent-Schlüssel
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseLine_FremdeOderLeereZeilen_LiefernFalse(string line)
    {
        Assert.False(VlcRecentList.TryParseLine(line, out _, out _));
    }

    [Fact]
    public void MrlToPath_FileUri_WirdLokalerPfad()
    {
        Assert.Equal(@"C:\Filme\x.mp4", VlcRecentList.MrlToPath("file:///C:/Filme/x.mp4"));
    }

    [Fact]
    public void MrlToDisplayName_LiefertDateiname()
    {
        Assert.Equal("x.mp4", VlcRecentList.MrlToDisplayName("file:///C:/Filme/x.mp4"));
    }
}
