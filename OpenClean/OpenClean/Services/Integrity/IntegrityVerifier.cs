using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OpenClean.Services.Integrity;

/// <summary>
/// Echte Authenticode-Prüfung einer Datei über WinVerifyTrust (OPCL-20): prüft die
/// Vertrauenskette UND den Datei-Hash und unterscheidet damit "unsigniert" von "verändert".
///
/// Abgrenzung zu <see cref="AuthenticodeService"/>: der liest nur den Herausgebernamen
/// fremder Dateien für die Autostart-/Kontextmenü-Listen (schnell, ohne Ketten- oder
/// Hash-Prüfung) und wird über hunderte Dateien aufgerufen. Diese Klasse hier prüft
/// wenige Dateien wirklich (eigene EXE, Premium-Modul).
///
/// Geprüft werden ausschließlich EINGEBETTETE Signaturen (WTD_CHOICE_FILE), keine
/// Katalogsignaturen. Das genügt: OpenClean.exe und OpenClean.Premium.dll werden direkt
/// signiert. Eine nur katalogsignierte Datei (viele Windows-Systemdateien) gilt hier
/// folgerichtig als <see cref="IntegritySignatureStatus.Unsigned"/> – nie als manipuliert.
/// </summary>
public static class IntegrityVerifier
{
    // ---- WinTrust-Interop -------------------------------------------------------------

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    // Achtung: WTD_SAFER_FLAG (0x100) wird hier BEWUSST nicht gesetzt. Es fasst die
    // Fehlerursachen nach SAFER-/AppLocker-Semantik zusammen und meldet für eine signierte,
    // aber nachträglich veränderte Datei TRUST_E_NOSIGNATURE statt TRUST_E_BAD_DIGEST –
    // eine manipulierte EXE käme damit als "unsigniert" durch und würde nicht gesperrt.
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    private const uint WTD_DISABLE_MD2_MD4 = 0x00002000;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    // HRESULTs von WinVerifyTrust (winerror.h).
    private const int S_OK = 0;
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);
    private const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    private const int CRYPT_E_NO_MATCH = unchecked((int)0x80092009);
    private const int CRYPT_E_SECURITY_SETTINGS = unchecked((int)0x80092026);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    // ---- Öffentliche API --------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, IntegrityResult> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prüft die Authenticode-Signatur der Datei. Wirft nie; ein Fehler in der Prüfung selbst
    /// liefert <see cref="IntegritySignatureStatus.Error"/>.
    /// </summary>
    public static IntegrityResult Verify(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return IntegrityResult.Failed(TRUST_E_SUBJECT_FORM_UNKNOWN);

        return Cache.GetOrAdd(filePath, VerifyUncached);
    }

    private static IntegrityResult VerifyUncached(string filePath)
    {
        try
        {
            // Die Datei bleibt während der gesamten Prüfung offen und ist damit für andere
            // schreibgesperrt (FileShare.Read). Das schließt das Zeitfenster zwischen
            // "Signatur prüfen" und "Zertifikat lesen", in dem die Datei sonst ausgetauscht
            // werden könnte (TOCTOU). Die Sperre leistet den Schutz – das Handle muss dafür
            // nicht an WinVerifyTrust durchgereicht werden.
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            int hr = RunWinVerifyTrust(filePath);
            var status = MapStatus(hr);

            // Herausgeber nur lesen, wenn überhaupt eine Signatur vorhanden ist.
            string publisher = "", thumbprint = "";
            if (status is IntegritySignatureStatus.Valid or IntegritySignatureStatus.Untrusted
                       or IntegritySignatureStatus.Tampered)
            {
                (publisher, thumbprint) = ReadSigner(filePath);
            }

            return new IntegrityResult(status, publisher, thumbprint, hr);
        }
        catch (Exception)
        {
            // Datei gesperrt/nicht lesbar/Provider defekt -> "nicht prüfbar", NIE Manipulationsverdacht.
            return IntegrityResult.Failed(TRUST_E_PROVIDER_UNKNOWN);
        }
    }

    /// <summary>
    /// Ruft WinVerifyTrust auf und schließt den Trust-Provider-Zustand wieder.
    /// </summary>
    private static int RunWinVerifyTrust(string filePath)
    {
        IntPtr pPath = IntPtr.Zero, pFileInfo = IntPtr.Zero, pData = IntPtr.Zero;
        var action = WinTrustActionGenericVerifyV2;

        try
        {
            pPath = Marshal.StringToCoTaskMemUni(filePath);

            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pPath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            pFileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                // Keine Rückrufprüfung und ausschließlich der lokale Cache: sonst versucht die
                // CryptoAPI beim Start, CRL-/AIA-Zertifikate nachzuladen. Auf einem Rechner ohne
                // Netz hängt der Start dann bis zum Timeout (bis zu ~15 s). Die drei Flags
                // zusammen garantieren, dass die Prüfung nie ins Netz geht.
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE
                            | WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_DISABLE_MD2_MD4
            };
            pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, pData, false);

            int hr = WinVerifyTrust(InvalidHandleValue, ref action, pData);

            // Aufräumen MUSS über denselben unmanaged Block laufen: WinVerifyTrust hat
            // hWVTStateData dort hineingeschrieben. Ein CLOSE über eine verwaltete Kopie
            // würde ein Null-Handle übergeben und den Provider-Zustand bei jedem Aufruf lecken.
            var written = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            written.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(written, pData, false);
            WinVerifyTrust(InvalidHandleValue, ref action, pData);

            return hr;
        }
        finally
        {
            if (pData != IntPtr.Zero) Marshal.FreeCoTaskMem(pData);
            if (pFileInfo != IntPtr.Zero) Marshal.FreeCoTaskMem(pFileInfo);
            if (pPath != IntPtr.Zero) Marshal.FreeCoTaskMem(pPath);
        }
    }

    /// <summary>Bildet den HRESULT auf den Schweregrad ab.</summary>
    private static IntegritySignatureStatus MapStatus(int hr) => hr switch
    {
        S_OK => IntegritySignatureStatus.Valid,

        // Keine Signatur vorhanden bzw. kein Signatur-Subject in der Datei.
        TRUST_E_NOSIGNATURE => IntegritySignatureStatus.Unsigned,
        TRUST_E_SUBJECT_FORM_UNKNOWN => IntegritySignatureStatus.Unsigned,
        CRYPT_E_NO_MATCH => IntegritySignatureStatus.Unsigned,

        // Signatur vorhanden, passt aber nicht mehr zum Inhalt -> die Datei wurde verändert.
        TRUST_E_BAD_DIGEST => IntegritySignatureStatus.Tampered,
        TRUST_E_EXPLICIT_DISTRUST => IntegritySignatureStatus.Tampered,

        // Signatur vorhanden, Kette aber nicht vertrauenswürdig.
        TRUST_E_SUBJECT_NOT_TRUSTED => IntegritySignatureStatus.Untrusted,
        CERT_E_UNTRUSTEDROOT => IntegritySignatureStatus.Untrusted,
        CERT_E_CHAINING => IntegritySignatureStatus.Untrusted,
        CERT_E_EXPIRED => IntegritySignatureStatus.Untrusted,
        CERT_E_REVOKED => IntegritySignatureStatus.Untrusted,

        // Prüfung nicht durchführbar (z. B. Unternehmens-Policy) -> kein Verdacht.
        CRYPT_E_SECURITY_SETTINGS => IntegritySignatureStatus.Error,
        _ => IntegritySignatureStatus.Error
    };

    /// <summary>Liest Herausgeber (Subject-CN) und Fingerabdruck aus der Signatur.</summary>
    private static (string Publisher, string Thumbprint) ReadSigner(string filePath)
    {
        try
        {
            // CreateFromSignedFile extrahiert das Authenticode-Zertifikat; ein nicht-veralteter
            // Ersatz existiert für dieses Szenario nicht -> gezielt unterdrücken (wie in AuthenticodeService).
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            return (cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "", cert.Thumbprint ?? "");
        }
        catch
        {
            return ("", "");
        }
    }
}
