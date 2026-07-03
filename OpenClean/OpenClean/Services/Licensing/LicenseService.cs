using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClean.Contracts;

namespace OpenClean.Services.Licensing;

/// <summary>Zustand der lokal gespeicherten Lizenz.</summary>
public enum LicenseState
{
    /// <summary>Keine (gültige) Lizenz vorhanden.</summary>
    None,

    /// <summary>Lizenz-Token ist signiert, gehört zu diesem Gerät und ist nicht abgelaufen.</summary>
    Valid,

    /// <summary>Token war gültig, die Offline-Frist ist aber abgelaufen (Online-Refresh nötig).</summary>
    Expired
}

/// <summary>Signierter Inhalt des Lizenz-Tokens (vom Server erzeugt, hier nur gelesen).</summary>
public sealed class LicensePayload
{
    [JsonPropertyName("v")] public int Version { get; set; }
    [JsonPropertyName("kid")] public string KeyId { get; set; } = "";
    [JsonPropertyName("fp")] public string Fingerprint { get; set; } = "";
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("features")] public List<string> Features { get; set; } = new();
    [JsonPropertyName("iat")] public DateTime IssuedUtc { get; set; }
    [JsonPropertyName("exp")] public DateTime ExpiresUtc { get; set; }
    [JsonPropertyName("moduleSha256")] public string? ModuleSha256 { get; set; }
    [JsonPropertyName("moduleVersion")] public string? ModuleVersion { get; set; }
}

/// <summary>
/// Verwaltet die lokal gespeicherte Premium-Lizenz (<c>license.json</c> im Datenverzeichnis).
///
/// Kernprinzipien:
/// <list type="bullet">
///   <item>OFFLINE-FIRST: die Prüfung erfolgt rein lokal über die ECDSA-P-256-Signatur des
///   Tokens (eingebetteter Public Key). Es findet NIE ein Netzwerkzugriff beim App-Start
///   statt; der Server signiert Tokens mit begrenzter Laufzeit (Offline-Frist ~30 Tage),
///   die die App im Hintergrund erneuert, wenn Premium-UI genutzt wird.</item>
///   <item>GERÄTEBINDUNG: das Token enthält den Geräte-Hash; ein Token eines anderen
///   Geräts wird wie „keine Lizenz" behandelt.</item>
///   <item>FEHLER SIND NIE FATAL: kaputte/fehlende Dateien führen still zu
///   <see cref="LicenseState.None"/>.</item>
/// </list>
/// </summary>
public sealed class LicenseService
{
    /// <summary>
    /// Öffentlicher ECDSA-P-256-Schlüssel (SubjectPublicKeyInfo, Base64), mit dem der
    /// Lizenzserver auf daonware.de die Tokens signiert. Der zugehörige private Schlüssel
    /// existiert AUSSCHLIESSLICH auf dem Server – Tokens lassen sich daher nicht fälschen.
    /// </summary>
    private const string PublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZrbF8dm0h71FSJtZSReDN5uvcXwIRsOMQExFRnxlHbqeB9pOhwHDUUQzY9/52WQisXck2eCUrEbR3i/f+TQ9rg==";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Prozessweiter Singleton.</summary>
    public static LicenseService Instance { get; } = new();

    private readonly string _filePath;
    private LicensePayload? _payload;
    private string? _licenseKey;
    private LicenseState _state = LicenseState.None;

    private LicenseService()
    {
        _filePath = AppPaths.GetDataFile("license.json");
        Reload();
    }

    /// <summary>Wird nach jeder Lizenzänderung (Aktivierung, Refresh, Entfernen) ausgelöst.</summary>
    public event EventHandler? Changed;

    /// <summary>Aktueller Lizenzzustand (rein lokale Prüfung).</summary>
    public LicenseState State => _state;

    /// <summary>Der gespeicherte Lizenzschlüssel (für Refresh/Deaktivieren); null ohne Lizenz.</summary>
    public string? LicenseKey => _licenseKey;

    /// <summary>Verifizierte Lizenzdaten für das Premium-Modul; null, solange nicht <see cref="LicenseState.Valid"/>.</summary>
    public LicenseInfo? License => _state == LicenseState.Valid && _payload is not null
        ? new LicenseInfo(_payload.KeyId, _payload.Edition, _payload.Features, _payload.ExpiresUtc)
        : null;

    /// <summary>Erwarteter SHA-256-Hash des Premium-Moduls laut Token (null = keine Vorgabe).</summary>
    public string? ModuleSha256 => _payload?.ModuleSha256;

    /// <summary>True, wenn eine gültige Lizenz das Feature enthält.</summary>
    public bool HasFeature(string feature)
        => _state == LicenseState.Valid &&
           _payload is not null &&
           _payload.Features.Contains(feature, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True, wenn das Token demnächst abläuft bzw. älter als eine Woche ist und bei
    /// Gelegenheit (Premium-UI offen, nie blockierend) online erneuert werden sollte.
    /// </summary>
    public bool ShouldRefresh
        => _payload is not null &&
           (_state == LicenseState.Expired ||
            DateTime.UtcNow - _payload.IssuedUtc > TimeSpan.FromDays(7));

    /// <summary>
    /// Prüft und speichert ein vom Server geliefertes Token (Base64-Payload + Signatur).
    /// Nur bei gültiger Signatur UND passendem Geräte-Hash wird gespeichert.
    /// </summary>
    public bool TrySaveToken(string licenseKey, string payloadBase64, string signatureBase64)
    {
        var payload = VerifyToken(payloadBase64, signatureBase64);
        if (payload is null) return false;
        if (!string.Equals(payload.Fingerprint, MachineFingerprint.Get(), StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var file = new LicenseFile
            {
                Key = licenseKey,
                Payload = payloadBase64,
                Signature = signatureBase64
            };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch
        {
            // Nicht schreibbar -> Lizenz gilt zumindest für diese Sitzung (in-memory).
        }

        ApplyVerified(licenseKey, payload);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Entfernt die lokale Lizenz (z. B. nach „Gerät deaktivieren").</summary>
    public void RemoveLicense()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch
        {
            // Löschfehler ignorieren; der In-Memory-Zustand wird trotzdem zurückgesetzt.
        }

        _payload = null;
        _licenseKey = null;
        _state = LicenseState.None;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Laden / Verifikation ------------------------------------------------

    private void Reload()
    {
        _payload = null;
        _licenseKey = null;
        _state = LicenseState.None;

        try
        {
            if (!File.Exists(_filePath)) return;

            var file = JsonSerializer.Deserialize<LicenseFile>(File.ReadAllText(_filePath), JsonOptions);
            if (file is null || string.IsNullOrWhiteSpace(file.Payload) || string.IsNullOrWhiteSpace(file.Signature))
                return;

            var payload = VerifyToken(file.Payload, file.Signature);
            if (payload is null) return;
            if (!string.Equals(payload.Fingerprint, MachineFingerprint.Get(), StringComparison.OrdinalIgnoreCase))
                return; // Token eines anderen Geräts -> wie keine Lizenz.

            ApplyVerified(file.Key, payload);
        }
        catch
        {
            // Kaputte Datei -> keine Lizenz.
        }
    }

    private void ApplyVerified(string? licenseKey, LicensePayload payload)
    {
        _payload = payload;
        _licenseKey = licenseKey;
        _state = payload.ExpiresUtc > DateTime.UtcNow ? LicenseState.Valid : LicenseState.Expired;
    }

    /// <summary>
    /// Prüft die ECDSA-Signatur (DER-Format, wie von PHP <c>openssl_sign</c> erzeugt) über
    /// die rohen Payload-Bytes und liefert bei Erfolg die deserialisierte Payload.
    /// </summary>
    private static LicensePayload? VerifyToken(string payloadBase64, string signatureBase64)
    {
        try
        {
            byte[] payloadBytes = Convert.FromBase64String(payloadBase64);
            byte[] signature = Convert.FromBase64String(signatureBase64);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySpkiBase64), out _);

            bool valid = ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            if (!valid) return null;

            var payload = JsonSerializer.Deserialize<LicensePayload>(Encoding.UTF8.GetString(payloadBytes));
            return payload is { Version: >= 1 } ? payload : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persistiertes Dateiformat von <c>license.json</c>.</summary>
    private sealed class LicenseFile
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("payload")] public string Payload { get; set; } = "";
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    }
}
