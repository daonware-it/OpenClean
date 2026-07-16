namespace OpenClean.Services.Integrity;

/// <summary>Was aus einem Prüfergebnis folgt.</summary>
public enum IntegrityAction
{
    /// <summary>Alles in Ordnung – App läuft uneingeschränkt.</summary>
    Allow,

    /// <summary>Auffällig, aber kein Manipulationsverdacht: Hinweis anzeigen, nichts sperren.</summary>
    Warn,

    /// <summary>Manipulation erkannt: Hinweis anzeigen UND die ändernden Funktionen sperren.</summary>
    WarnAndLock,

    /// <summary>Fremde/nicht vertrauenswürdige Binärdatei: Dialog anzeigen und beenden.</summary>
    Block
}

/// <summary>
/// Eingangsgrößen der Entscheidung. Bewusst ein reiner Datensatz ohne Windows-Bezug,
/// damit <see cref="IntegrityPolicy.Decide"/> ohne Signaturen testbar bleibt.
/// </summary>
public sealed record IntegrityContext(
    IntegritySignatureStatus Status,
    bool PublisherMatches,
    bool IsDeveloperBuild);

/// <summary>
/// Die Politik von OPCL-20: Signaturstatus → Reaktion. Reine Funktion, keine Seiteneffekte.
///
/// Leitgedanke: OpenClean ist Open Source (Apache). Wer aus dem Quellcode baut, bekommt eine
/// unsignierte EXE – die darf NICHT beschnitten werden. Ein Manipulationsverdacht besteht erst,
/// wenn eine Signatur vorhanden, aber gebrochen ist, oder wenn jemand Fremdes signiert hat.
/// </summary>
public static class IntegrityPolicy
{
    /// <summary>Entscheidet, wie auf ein Prüfergebnis reagiert wird.</summary>
    public static IntegrityAction Decide(IntegrityContext ctx)
    {
        // Entwicklungs-/Debug-Builds werden nie geprüft.
        if (ctx.IsDeveloperBuild) return IntegrityAction.Allow;

        return ctx.Status switch
        {
            // Gültig signiert: nur von uns akzeptiert. Ein fremder, gültiger Signierer bedeutet,
            // dass jemand OpenClean umverpackt und neu signiert hat -> nicht ausführen.
            IntegritySignatureStatus.Valid => ctx.PublisherMatches
                ? IntegrityAction.Allow
                : IntegrityAction.Block,

            // Kette kaputt/widerrufen/abgelaufen -> fremde oder ersetzte Binärdatei.
            IntegritySignatureStatus.Untrusted => IntegrityAction.Block,

            // Signatur da, Hash gebrochen: die Datei wurde nach dem Signieren verändert.
            IntegritySignatureStatus.Tampered => IntegrityAction.WarnAndLock,

            // Gar keine Signatur: höchstwahrscheinlich ein Build aus dem Quellcode.
            // Hinweis ja, Sperre nein – sonst wären eigene Builds nur noch Anzeige-Tools.
            IntegritySignatureStatus.Unsigned => IntegrityAction.Warn,

            // Prüfung nicht durchführbar (Unternehmens-Policy, defekter WinTrust-Provider):
            // bewusst fail-open. Ein kaputter Prüfmechanismus darf den Cleaner nicht lahmlegen.
            IntegritySignatureStatus.Error => IntegrityAction.Warn,

            _ => IntegrityAction.Warn
        };
    }

    /// <summary>Der i18n-Schlüssel, der den Grund für Banner/Dialog benennt.</summary>
    public static string ReasonKey(IntegritySignatureStatus status) => status switch
    {
        IntegritySignatureStatus.Tampered => "integrity.reason.tampered",
        IntegritySignatureStatus.Untrusted => "integrity.reason.untrusted",
        IntegritySignatureStatus.Unsigned => "integrity.reason.unsigned",
        _ => "integrity.reason.unknown"
    };
}
