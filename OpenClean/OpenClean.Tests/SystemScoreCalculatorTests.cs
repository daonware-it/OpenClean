using System;
using System.Collections.Generic;
using System.Linq;
using OpenClean.Models;
using OpenClean.Services.Dashboard;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die reine Score-Engine ab: Schwellen, Punktabzüge, Clamp und Level-Grenzen.
/// Bewusst NICHT geprüft werden die lokalisierten Beschreibungstexte – nur die messbaren
/// Größen (Score-Zahl, Level, Anzahl/Kind/Punkte der Faktoren), damit der Test
/// sprachunabhängig bleibt.
/// </summary>
public class SystemScoreCalculatorTests
{
    private static SystemMeasurements Measure(
        IReadOnlyList<DriveMeasurement>? drives = null,
        long tempBytes = 0,
        int tempItems = 0,
        int startupEnabled = 0,
        int startupHighImpact = 0,
        bool ramValid = true,
        double ramPercent = 30)
        => new(drives ?? Array.Empty<DriveMeasurement>(), tempBytes, tempItems,
               startupEnabled, startupHighImpact, ramValid, ramPercent);

    private static DriveMeasurement Drive(double freePercent, string letter = "C:")
        => new(letter, freePercent, 100 - freePercent, "10 GB");

    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    [Fact]
    public void Compute_MakellosesSystem_Liefert100GoodOhneFaktoren()
    {
        ScoreResult result = SystemScoreCalculator.Compute(Measure());

        Assert.Equal(100, result.Score);
        Assert.Equal(ScoreLevel.Good, result.Level);
        Assert.Empty(result.Factors);
    }

    [Fact]
    public void Compute_KritischWenigSpeicher_ZiehtStorage25Ab()
    {
        ScoreResult result = SystemScoreCalculator.Compute(Measure(drives: new[] { Drive(5) }));

        Assert.Equal(75, result.Score);
        ScoreFactor factor = Assert.Single(result.Factors);
        Assert.Equal(ScoreFactorKind.Storage, factor.Kind);
        Assert.Equal(-25, factor.Points);
    }

    [Fact]
    public void Compute_GrossesTemp_ZiehtReclaimable20Ab()
    {
        ScoreResult result = SystemScoreCalculator.Compute(Measure(tempBytes: 6 * GiB));

        Assert.Equal(80, result.Score);
        ScoreFactor factor = Assert.Single(result.Factors);
        Assert.Equal(ScoreFactorKind.Reclaimable, factor.Kind);
        Assert.Equal(-20, factor.Points);
    }

    [Fact]
    public void Compute_VieleAutostarts_ZiehtStartup15Ab()
    {
        ScoreResult result = SystemScoreCalculator.Compute(Measure(startupEnabled: 21));

        Assert.Equal(85, result.Score);
        ScoreFactor factor = Assert.Single(result.Factors);
        Assert.Equal(ScoreFactorKind.Startup, factor.Kind);
        Assert.Equal(-15, factor.Points);
    }

    [Fact]
    public void Compute_HoheRamLast_ZiehtRam15Ab()
    {
        ScoreResult result = SystemScoreCalculator.Compute(Measure(ramValid: true, ramPercent: 95));

        Assert.Equal(85, result.Score);
        ScoreFactor factor = Assert.Single(result.Factors);
        Assert.Equal(ScoreFactorKind.Ram, factor.Kind);
        Assert.Equal(-15, factor.Points);
    }

    [Fact]
    public void Compute_UngueltigerRam_ErzeugtKeinenRamFaktor()
    {
        // Ohne gültige Messung darf die RAM-Last den Score nicht mindern.
        ScoreResult result = SystemScoreCalculator.Compute(Measure(ramValid: false, ramPercent: 99));

        Assert.Equal(100, result.Score);
        Assert.Empty(result.Factors);
    }

    [Fact]
    public void Compute_VieleSchwereProbleme_ClampBei0()
    {
        // Fünf randvolle Laufwerke → 5 × −25 = −125, muss auf 0 gekappt werden.
        var drives = Enumerable.Range(0, 5)
            .Select(i => Drive(5, letter: $"{(char)('C' + i)}:"))
            .ToArray();

        ScoreResult result = SystemScoreCalculator.Compute(Measure(drives: drives));

        Assert.Equal(0, result.Score);
        Assert.Equal(ScoreLevel.Critical, result.Level);
        Assert.Equal(5, result.Factors.Count);
        Assert.All(result.Factors, f => Assert.Equal(ScoreFactorKind.Storage, f.Kind));
    }

    [Fact]
    public void Compute_Score80_IstNochGood()
    {
        // Genau an der Grenze ≥ 80.
        ScoreResult result = SystemScoreCalculator.Compute(Measure(tempBytes: 6 * GiB));

        Assert.Equal(80, result.Score);
        Assert.Equal(ScoreLevel.Good, result.Level);
    }

    [Fact]
    public void Compute_Score50_IstImprovable()
    {
        // Zwei kritische Laufwerke → 100 − 50 = 50, exakt an der Grenze ≥ 50.
        ScoreResult result = SystemScoreCalculator.Compute(
            Measure(drives: new[] { Drive(5, "C:"), Drive(5, "D:") }));

        Assert.Equal(50, result.Score);
        Assert.Equal(ScoreLevel.Improvable, result.Level);
    }

    [Fact]
    public void Compute_Score49_IstCritical()
    {
        // Zwei kritische Laufwerke (−50) plus grosses Temp (−20) → 30, klar unter 50.
        ScoreResult result = SystemScoreCalculator.Compute(
            Measure(drives: new[] { Drive(5, "C:"), Drive(5, "D:") }, tempBytes: 6 * GiB));

        Assert.Equal(30, result.Score);
        Assert.Equal(ScoreLevel.Critical, result.Level);
    }

    [Fact]
    public void Compute_KnappWenigSpeicher_ZiehtNur10Ab()
    {
        // 15 % frei liegt zwischen den Schwellen 10 % und 20 % → −10 statt −25.
        ScoreResult result = SystemScoreCalculator.Compute(Measure(drives: new[] { Drive(15) }));

        Assert.Equal(90, result.Score);
        Assert.Equal(-10, Assert.Single(result.Factors).Points);
    }

    [Fact]
    public void Compute_KleinesTempUeber200Mb_ZiehtNur5Ab()
    {
        ScoreResult result = SystemScoreCalculator.Compute(Measure(tempBytes: 300 * MiB));

        Assert.Equal(95, result.Score);
        Assert.Equal(-5, Assert.Single(result.Factors).Points);
    }

    [Fact]
    public void Compute_HighImpactAutostarts_WerdenBei12Gedeckelt()
    {
        // 10 × High → 30 Rohabzug, gedeckelt auf 12.
        ScoreResult result = SystemScoreCalculator.Compute(Measure(startupHighImpact: 10));

        Assert.Equal(88, result.Score);
        ScoreFactor factor = Assert.Single(result.Factors);
        Assert.Equal(ScoreFactorKind.Startup, factor.Kind);
        Assert.Equal(-12, factor.Points);
    }
}
