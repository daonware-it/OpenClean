using OpenClean.Services.Integrity;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die Politik-Tabelle aus OPCL-20 vollständig ab: Signaturstatus → Reaktion.
/// </summary>
public class IntegrityPolicyTests
{
    private static IntegrityContext Ctx(
        IntegritySignatureStatus status,
        bool publisherMatches = true,
        bool devBuild = false)
        => new(status, publisherMatches, devBuild);

    [Theory]
    [InlineData(IntegritySignatureStatus.Valid)]
    [InlineData(IntegritySignatureStatus.Unsigned)]
    [InlineData(IntegritySignatureStatus.Untrusted)]
    [InlineData(IntegritySignatureStatus.Tampered)]
    [InlineData(IntegritySignatureStatus.Error)]
    public void EntwicklungsBuild_WirdNieGeprueft(IntegritySignatureStatus status)
    {
        var action = IntegrityPolicy.Decide(Ctx(status, publisherMatches: false, devBuild: true));
        Assert.Equal(IntegrityAction.Allow, action);
    }

    [Fact]
    public void GueltigUndVonDaonWare_Erlaubt()
        => Assert.Equal(
            IntegrityAction.Allow,
            IntegrityPolicy.Decide(Ctx(IntegritySignatureStatus.Valid)));

    [Fact]
    public void GueltigAberFremdSigniert_Blockiert()
        => Assert.Equal(
            IntegrityAction.Block,
            IntegrityPolicy.Decide(Ctx(IntegritySignatureStatus.Valid, publisherMatches: false)));

    [Fact]
    public void NichtVertrauenswuerdigeKette_Blockiert()
        => Assert.Equal(
            IntegrityAction.Block,
            IntegrityPolicy.Decide(Ctx(IntegritySignatureStatus.Untrusted)));

    [Fact]
    public void GebrocheneSignatur_WarntUndSperrt()
        => Assert.Equal(
            IntegrityAction.WarnAndLock,
            IntegrityPolicy.Decide(Ctx(IntegritySignatureStatus.Tampered)));

    /// <summary>
    /// Der Kern der Entscheidung: Ein Build aus dem Quellcode (Apache-Lizenz) ist unsigniert und
    /// muss voll nutzbar bleiben – Hinweis ja, Sperre nein.
    /// </summary>
    [Fact]
    public void Unsigniert_WarntNurUndSperrtNicht()
        => Assert.Equal(
            IntegrityAction.Warn,
            IntegrityPolicy.Decide(Ctx(IntegritySignatureStatus.Unsigned, publisherMatches: false)));

    /// <summary>Ein defekter Prüfmechanismus darf den Cleaner nicht lahmlegen (fail-open).</summary>
    [Fact]
    public void NichtPruefbar_WarntNurUndSperrtNicht()
        => Assert.Equal(
            IntegrityAction.Warn,
            IntegrityPolicy.Decide(Ctx(IntegritySignatureStatus.Error, publisherMatches: false)));

    [Theory]
    [InlineData(IntegritySignatureStatus.Tampered, "integrity.reason.tampered")]
    [InlineData(IntegritySignatureStatus.Untrusted, "integrity.reason.untrusted")]
    [InlineData(IntegritySignatureStatus.Unsigned, "integrity.reason.unsigned")]
    public void GrundSchluessel_PasstZumStatus(IntegritySignatureStatus status, string expected)
        => Assert.Equal(expected, IntegrityPolicy.ReasonKey(status));
}
