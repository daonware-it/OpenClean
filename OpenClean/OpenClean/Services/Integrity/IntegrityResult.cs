namespace OpenClean.Services.Integrity;

/// <summary>
/// Ergebnis der Authenticode-Prüfung einer Datei (OPCL-20).
/// </summary>
public enum IntegritySignatureStatus
{
    /// <summary>Signatur vorhanden, Kette vertrauenswürdig, Datei unverändert.</summary>
    Valid,

    /// <summary>Gar keine Signatur vorhanden (typisch für einen Build aus dem Quellcode).</summary>
    Unsigned,

    /// <summary>Signatur vorhanden, aber Kette nicht vertrauenswürdig (fremde Wurzel, widerrufen, abgelaufen).</summary>
    Untrusted,

    /// <summary>Signatur vorhanden, aber der Datei-Hash passt nicht mehr: die Datei wurde nach dem Signieren verändert.</summary>
    Tampered,

    /// <summary>
    /// Die Prüfung selbst war nicht durchführbar (WinTrust-Provider fehlt, Unternehmens-Policy
    /// verbietet sie, Datei nicht lesbar). Bewusst KEIN Manipulationsverdacht.
    /// </summary>
    Error
}

/// <summary>
/// Prüfergebnis einer Datei: Status, Herausgeber (Subject-CN) und der rohe HRESULT von
/// WinVerifyTrust (nur für die Fehlersuche).
/// </summary>
public sealed record IntegrityResult(
    IntegritySignatureStatus Status,
    string Publisher,
    string Thumbprint,
    int HResult)
{
    /// <summary>Ergebnis für eine nicht prüfbare Datei.</summary>
    public static IntegrityResult Failed(int hr) => new(IntegritySignatureStatus.Error, "", "", hr);
}
