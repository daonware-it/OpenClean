using OpenClean.Services.Integrity;
using Xunit;

namespace OpenClean.Tests;

/// <summary>Erkennung des Entwicklungs-Layouts (Dev-Bypass).</summary>
public class IntegrityGuardTests
{
    [Theory]
    [InlineData(@"E:\Coding\OpenClean\OpenClean\OpenClean\bin\Debug\net10.0-windows\")]
    [InlineData(@"E:\Coding\OpenClean\OpenClean\OpenClean\bin\Release\net10.0-windows\")]
    [InlineData(@"C:\repo\app\bin\debug\net10.0\")]           // Groß-/Kleinschreibung egal
    [InlineData(@"C:/repo/app/bin/Release/net10.0/")]         // auch mit Schrägstrichen
    public void BuildAusgabeOrdner_GiltAlsEntwicklung(string path)
        => Assert.True(IntegrityGuard.LooksLikeDeveloperLayout(path));

    [Theory]
    [InlineData(@"C:\Program Files\OpenClean\")]
    [InlineData(@"D:\PortableApps\OpenClean\")]
    [InlineData(@"C:\Users\max\Downloads\")]
    [InlineData("")]
    [InlineData(null)]
    public void AusgelieferteOrdner_GeltenNichtAlsEntwicklung(string? path)
        => Assert.False(IntegrityGuard.LooksLikeDeveloperLayout(path));

    /// <summary>
    /// Ein Ordner, der zufällig "bin" heißt, ist kein Build-Ausgabeordner – sonst könnte ein
    /// Angreifer die Prüfung einfach durch einen passend benannten Pfad abschalten.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Tools\bin\")]
    [InlineData(@"C:\Users\max\bin\Debugger\")]
    [InlineData(@"C:\bin\Release-Notes\")]
    public void AehnlichBenannteOrdner_GeltenNichtAlsEntwicklung(string path)
        => Assert.False(IntegrityGuard.LooksLikeDeveloperLayout(path));
}
