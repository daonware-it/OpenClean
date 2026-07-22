using System.IO;
using OpenClean.Services;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Sichert das geteilte Grenz-Primitiv ab. Der wichtigste Fall ist die
/// Trennzeichen-Grenze (<c>…\.net</c> darf NICHT <c>…\.network</c> matchen) –
/// ein Fehler hier wäre eine echte Sicherheitslücke in den Löschpfaden.
/// </summary>
public class PathScopeTests
{
    [Fact]
    public void GleicherPfad_IstSameOrUnder()
    {
        Assert.True(PathScope.IsSameOrUnder(@"C:\Temp\.net", @"C:\Temp\.net"));
    }

    [Fact]
    public void EchterNachfahre_IstSameOrUnder()
    {
        Assert.True(PathScope.IsSameOrUnder(@"C:\Temp\.net\app\v1", @"C:\Temp\.net"));
    }

    [Fact]
    public void NamensPraefixOhneTrennzeichen_IstNichtDarunter()
    {
        // Der entscheidende Sicherheitsfall: gemeinsamer Präfix, aber kein Nachfahre.
        Assert.False(PathScope.IsSameOrUnder(@"C:\Temp\.network", @"C:\Temp\.net"));
        Assert.False(PathScope.IsSameOrUnder(@"C:\TempX", @"C:\Temp"));
    }

    [Fact]
    public void UnverwandterPfad_IstNichtDarunter()
    {
        Assert.False(PathScope.IsSameOrUnder(@"C:\Windows", @"C:\Temp\.net"));
    }

    [Fact]
    public void GrossKleinschreibungUndTrailingSlash_SindEgal()
    {
        Assert.True(PathScope.IsSameOrUnder(@"c:\temp\.NET\SUB\", @"C:\Temp\.net"));
    }

    [Fact]
    public void Normalize_EntferntTrailingSlash_UndVereinheitlichtTrenner()
    {
        Assert.Equal(@"C:\Temp\.net", PathScope.Normalize(@"C:\Temp\.net\"));
        Assert.Equal(@"C:\Temp\.net", PathScope.Normalize(@"C:/Temp/.net"));
    }
}
