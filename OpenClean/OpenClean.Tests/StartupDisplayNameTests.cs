using OpenClean.Services;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Sichert die aus <c>StartupService</c> ausgelagerten Namens-Heuristiken für
/// Autostart-Einträge ab (PackageFamilyName/TaskId → lesbarer Anzeigename).
/// </summary>
public class StartupDisplayNameTests
{
    [Theory]
    [InlineData("Microsoft.SpotifyMusic_abc123", "SpotifyMusic")] // Segment nach erstem Punkt
    [InlineData("SomeApp_xyz", "SomeApp")]                        // ohne Punkt: Teil vor '_'
    [InlineData("A.B.C_x", "B.C")]                                // nur am ERSTEN Punkt trennen
    public void PackageNameSegment_LiefertPaketsegment(string input, string expected)
    {
        Assert.Equal(expected, StartupDisplayName.PackageNameSegment(input));
    }

    [Theory]
    [InlineData("deadbeef", true)]   // hex, >= 6
    [InlineData("a1b2c3", true)]     // hex, genau 6
    [InlineData("12345", true)]      // reine Ziffern
    [InlineData("abc", false)]       // zu kurz, nicht rein numerisch
    [InlineData("Spotify", false)]   // enthält Nicht-Hex-Buchstaben
    [InlineData("", false)]
    public void LooksLikeHash_ErkenntHashArtigeTexte(string input, bool expected)
    {
        Assert.Equal(expected, StartupDisplayName.LooksLikeHash(input));
    }

    [Theory]
    [InlineData("SpotifyMusic", "Spotify Music")]     // CamelCase aufsplitten
    [InlineData("Spotify Music", "Spotify Music")]    // bereits getrennt: unverändert
    [InlineData("MyApp Desktop", "MyApp")]            // Suffix " Desktop" entfernen
    [InlineData("SpotifyMusicApp", "Spotify Music")]  // splitten UND Suffix " App" entfernen
    public void Prettify_ErzeugtLesbarenNamen(string input, string expected)
    {
        Assert.Equal(expected, StartupDisplayName.Prettify(input));
    }
}
