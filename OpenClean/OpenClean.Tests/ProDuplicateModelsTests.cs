using System.Collections.Generic;
using OpenClean.Models;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die reine Modell-/Options-Logik der Premium-Duplikat- und Secure-Delete-Funktionen ab:
/// das Begrenzen der Ähnlichkeitsschwelle, das Einrasten der Überschreib-Durchgänge auf {1,3,7}
/// und die Berechnung des verschwendeten Speichers einer Ähnlichkeitsgruppe.
///
/// Bewusst NICHT getestet: die eigentliche Perceptual-Hash-Suche bzw. das mehrfache Überschreiben
/// beim sicheren Löschen – beides liegt ausschließlich im Closed-Source-Premium-Modul. Hier wird
/// nur der Vertrag der reinen DTOs geprüft, ohne echte Datei-, Modul- oder Settings-Interaktion.
/// </summary>
public class ProDuplicateModelsTests
{
    // --- FuzzyScanOptions.NormalizeThreshold (Clamp 50..100) ---

    [Theory]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(-10, 50)]
    [InlineData(101, 100)]
    [InlineData(999, 100)]
    [InlineData(50, 50)]
    [InlineData(75, 75)]
    [InlineData(90, 90)]
    [InlineData(100, 100)]
    public void NormalizeThreshold_begrenzt_auf_Bereich_50_bis_100(int eingabe, int erwartet)
    {
        Assert.Equal(erwartet, FuzzyScanOptions.NormalizeThreshold(eingabe));
    }

    // --- SecureDeleteOptions.NormalizePasses (nächstgelegener Wert aus {1,3,7}) ---

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 7)]
    [InlineData(0, 1)]
    [InlineData(6, 7)]
    [InlineData(100, 7)]
    [InlineData(4, 3)]
    public void NormalizePasses_rastet_auf_naechstgelegenen_erlaubten_Wert_ein(int eingabe, int erwartet)
    {
        Assert.Equal(erwartet, SecureDeleteOptions.NormalizePasses(eingabe));
    }

    [Fact]
    public void NormalizePasses_liefert_immer_einen_Wert_aus_1_3_7()
    {
        for (int eingabe = 0; eingabe <= 12; eingabe++)
        {
            int ergebnis = SecureDeleteOptions.NormalizePasses(eingabe);
            Assert.Contains(ergebnis, new[] { 1, 3, 7 });
        }
    }

    // --- FuzzyDuplicateGroup.WastedBytes (Summe der Größen minus größte Datei) ---

    [Fact]
    public void WastedBytes_ist_Summe_der_Groessen_minus_groesste_Datei()
    {
        var gruppe = new FuzzyDuplicateGroup
        {
            ReferencePath = @"C:\bilder\referenz.jpg",
            Files = new List<FuzzyDuplicateFile>
            {
                new() { Path = @"C:\bilder\a.jpg", SizeBytes = 100, SimilarityScore = 100 },
                new() { Path = @"C:\bilder\b.jpg", SizeBytes = 250, SimilarityScore = 95 },
                new() { Path = @"C:\bilder\c.jpg", SizeBytes = 400, SimilarityScore = 90 },
            },
        };

        // Summe = 750, größte = 400 -> verschwendet = 350
        Assert.Equal(350, gruppe.WastedBytes);
    }

    [Fact]
    public void WastedBytes_ist_null_bei_nur_einer_Datei()
    {
        var gruppe = new FuzzyDuplicateGroup
        {
            ReferencePath = @"C:\bilder\einzeln.jpg",
            Files = new List<FuzzyDuplicateFile>
            {
                new() { Path = @"C:\bilder\einzeln.jpg", SizeBytes = 500, SimilarityScore = 100 },
            },
        };

        Assert.Equal(0, gruppe.WastedBytes);
    }
}
