namespace OpenClean.Services.Integrity;

/// <summary>Vertrauensstufe des laufenden Prozesses.</summary>
public enum IntegrityLevel
{
    /// <summary>Eigene Binärdatei ist unverändert (oder Entwicklungs-Build).</summary>
    Trusted,

    /// <summary>Etwas stimmt nicht – Hinweis wird angezeigt. Ob gesperrt wird, sagt <see cref="IntegrityState.IsBlocked"/>.</summary>
    Degraded
}

/// <summary>
/// Prozessweiter Integritätszustand (OPCL-20). Wird beim Start EINMAL von
/// <see cref="IntegrityGuard"/> gesetzt und danach nur noch gelesen.
///
/// Voreinstellung ist bewusst "vertrauenswürdig, nicht gesperrt": Läuft der Code in einem
/// Kontext ohne App-Start (Tests), darf er nicht stillschweigend alles blockieren.
/// </summary>
public static class IntegrityState
{
    /// <summary>Vertrauensstufe des Prozesses.</summary>
    public static IntegrityLevel Level { get; private set; } = IntegrityLevel.Trusted;

    /// <summary>
    /// True, wenn die ändernden Funktionen gesperrt sind (Löschen, Deinstallieren,
    /// Autostart-/Kontextmenü-Änderungen, Zeitplan, Premium-Modul). Das eigentliche Gate.
    /// </summary>
    public static bool IsBlocked { get; private set; }

    /// <summary>True, wenn ein Hinweis angezeigt werden soll (mit oder ohne Sperre).</summary>
    public static bool IsDegraded => Level == IntegrityLevel.Degraded;

    /// <summary>
    /// i18n-SCHLÜSSEL des Grundes (nicht der übersetzte Text): Zum Prüfzeitpunkt steht die
    /// Sprache noch nicht zwingend fest.
    /// </summary>
    public static string ReasonKey { get; private set; } = "";

    /// <summary>Ergebnis der Signaturprüfung der eigenen EXE – nur für Anzeige/Diagnose.</summary>
    public static IntegritySignatureStatus SelfStatus { get; private set; } = IntegritySignatureStatus.Valid;

    /// <summary>Setzt den Zustand. Nur von <see cref="IntegrityGuard"/> aufzurufen.</summary>
    internal static void Set(IntegrityLevel level, bool blocked, string reasonKey, IntegritySignatureStatus selfStatus)
    {
        Level = level;
        IsBlocked = blocked;
        ReasonKey = reasonKey;
        SelfStatus = selfStatus;
    }
}
