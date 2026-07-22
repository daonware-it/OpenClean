using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using OpenClean.Models;
using OpenClean.Services.Dashboard;
using OpenClean.ViewModels;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die reine Empfehlungs-Engine ab: Arten, Dringlichkeiten, Sortierung, den
/// „Alles gut“-Rückfall und die Verdrahtung der Aktionen. Die Nebenwirkungen
/// (Navigation, Scan, RAM-Refresh) werden über No-op-Delegates/Dummy-Commands injiziert,
/// sodass kein WPF-Kontext nötig ist.
/// </summary>
public class RecommendationBuilderTests
{
    private sealed class DummyCommand : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    private static readonly ICommand CleanNow = new DummyCommand();
    private static readonly ICommand RefreshRam = new DummyCommand();

    private static RecommendationActions Actions()
        => new(_ => { }, CleanNow, RefreshRam);

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
    public void Build_GutesSystem_LiefertGenauEineAllGoodEmpfehlung()
    {
        IReadOnlyList<Recommendation> result = RecommendationBuilder.Build(Measure(), Actions());

        Recommendation reco = Assert.Single(result);
        Assert.Equal(RecommendationKind.AllGood, reco.Kind);
        Assert.Equal(RecommendationSeverity.Positive, reco.Severity);
        Assert.False(reco.HasAction);
    }

    [Fact]
    public void Build_TempUeber200Mb_ErzeugtReclaimableMitAktion()
    {
        // 300 MB > 200 MB, aber ≤ 1 GB → Info-Severity; ActionCommand muss gesetzt sein.
        IReadOnlyList<Recommendation> result =
            RecommendationBuilder.Build(Measure(tempBytes: 300 * MiB, tempItems: 42), Actions());

        Recommendation reco = Assert.Single(result);
        Assert.Equal(RecommendationKind.Reclaimable, reco.Kind);
        Assert.Equal(RecommendationSeverity.Info, reco.Severity);
        Assert.True(reco.HasAction);
        Assert.Same(CleanNow, reco.ActionCommand);
    }

    [Fact]
    public void Build_TempUeber1Gb_IstWarnung()
    {
        IReadOnlyList<Recommendation> result =
            RecommendationBuilder.Build(Measure(tempBytes: 2 * GiB), Actions());

        Recommendation reco = Assert.Single(result);
        Assert.Equal(RecommendationKind.Reclaimable, reco.Kind);
        Assert.Equal(RecommendationSeverity.Warning, reco.Severity);
    }

    [Fact]
    public void Build_KritischVollesLaufwerk_IstCriticalStorage()
    {
        IReadOnlyList<Recommendation> result =
            RecommendationBuilder.Build(Measure(drives: new[] { Drive(5) }), Actions());

        Recommendation reco = Assert.Single(result);
        Assert.Equal(RecommendationKind.Storage, reco.Kind);
        Assert.Equal(RecommendationSeverity.Critical, reco.Severity);
        Assert.True(reco.HasAction);
    }

    [Fact]
    public void Build_KnappVollesLaufwerk_IstWarnung()
    {
        // Zwischen 10 % und 15 % frei → Warnung statt Kritisch.
        IReadOnlyList<Recommendation> result =
            RecommendationBuilder.Build(Measure(drives: new[] { Drive(12) }), Actions());

        Recommendation reco = Assert.Single(result);
        Assert.Equal(RecommendationKind.Storage, reco.Kind);
        Assert.Equal(RecommendationSeverity.Warning, reco.Severity);
    }

    [Fact]
    public void Build_HoheRamLast_ErzeugtInfoMitRefreshAktion()
    {
        IReadOnlyList<Recommendation> result =
            RecommendationBuilder.Build(Measure(ramValid: true, ramPercent: 90), Actions());

        Recommendation reco = Assert.Single(result);
        Assert.Equal(RecommendationKind.Ram, reco.Kind);
        Assert.Equal(RecommendationSeverity.Info, reco.Severity);
        Assert.Same(RefreshRam, reco.ActionCommand);
    }

    [Fact]
    public void Build_Kombination_SortiertKritischVorWarnungVorHinweis()
    {
        // Kritisch (Laufwerk < 10 %), Warnung (High-Impact-Autostart), Hinweis (RAM > 85 %).
        SystemMeasurements m = Measure(
            drives: new[] { Drive(5) },
            startupEnabled: 5,
            startupHighImpact: 1,
            ramValid: true,
            ramPercent: 90);

        IReadOnlyList<Recommendation> result = RecommendationBuilder.Build(m, Actions());

        Assert.Equal(3, result.Count);
        Assert.Equal(RecommendationSeverity.Critical, result[0].Severity);
        Assert.Equal(RecommendationSeverity.Warning, result[1].Severity);
        Assert.Equal(RecommendationSeverity.Info, result[2].Severity);

        // Absteigend nach Dringlichkeit – keine Ausnahme in der Reihenfolge.
        int[] severities = result.Select(r => (int)r.Severity).ToArray();
        Assert.Equal(severities.OrderByDescending(s => s), severities);
    }

    [Fact]
    public void Build_AutostartOhneHighImpact_IstNurHinweis()
    {
        IReadOnlyList<Recommendation> result =
            RecommendationBuilder.Build(Measure(startupEnabled: 12), Actions());

        Recommendation reco = Assert.Single(result);
        Assert.Equal(RecommendationKind.Startup, reco.Kind);
        Assert.Equal(RecommendationSeverity.Info, reco.Severity);
    }

    [Fact]
    public void Build_MehrereVolleLaufwerke_ErzeugtJeEineEmpfehlung()
    {
        IReadOnlyList<Recommendation> result = RecommendationBuilder.Build(
            Measure(drives: new[] { Drive(5, "C:"), Drive(8, "D:"), Drive(50, "E:") }),
            Actions());

        // Nur die beiden Laufwerke unter 15 % frei erzeugen eine Empfehlung.
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(RecommendationKind.Storage, r.Kind));
    }
}
