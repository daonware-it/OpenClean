using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OpenClean.Services.Licensing;

/// <summary>Ergebnis eines Lizenzserver-Aufrufs (Aktivierung/Refresh).</summary>
public sealed record LicenseApiResult(
    bool Success,
    LicenseApiError Error,
    string? PayloadBase64 = null,
    string? SignatureBase64 = null,
    string? DownloadToken = null);

/// <summary>Fehlerkategorien des Lizenzservers (für lokalisierte Meldungen).</summary>
public enum LicenseApiError
{
    None,
    /// <summary>Schlüssel unbekannt, widerrufen oder falsch formatiert.</summary>
    InvalidKey,
    /// <summary>Geräte-Limit der Lizenz erreicht.</summary>
    DeviceLimit,
    /// <summary>Zu viele Anfragen in kurzer Zeit – der Server drosselt (HTTP 429).</summary>
    RateLimited,
    /// <summary>Server nicht erreichbar / Zeitüberschreitung / unerwartete Antwort.</summary>
    Network
}

/// <summary>
/// HTTP-Client für den Lizenzserver auf daonware.de. Einziger Netzwerkzugriff der App –
/// und der findet AUSSCHLIESSLICH auf Nutzeraktion (Aktivieren/Deaktivieren) bzw. als
/// Hintergrund-Refresh einer vorhandenen Lizenz statt (siehe PRIVACY.md). Übertragen
/// werden nur: Lizenzschlüssel, anonymer Geräte-Hash und App-Version.
/// Alle Fehler werden auf <see cref="LicenseApiError"/> abgebildet – nie Exceptions.
/// </summary>
public sealed class LicenseApiClient
{
    /// <summary>Basis-URL der Lizenz-API (v1).</summary>
    public const string BaseUrl = "https://daonware.de/oc-api/v1";

    /// <summary>Kaufseite (wird im Browser geöffnet).</summary>
    public const string BuyUrl = "https://daonware.de/openclean/premium";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Aktiviert einen Lizenzschlüssel für dieses Gerät.</summary>
    public Task<LicenseApiResult> ActivateAsync(string licenseKey)
        => PostAsync("/activate", new TokenRequest
        {
            Key = licenseKey.Trim(),
            Fingerprint = MachineFingerprint.Get(),
            AppVersion = AppVersion()
        });

    /// <summary>Erneuert das Token dieses Geräts (Hintergrund-Refresh der Offline-Frist).</summary>
    public Task<LicenseApiResult> ValidateAsync(string licenseKey)
        => PostAsync("/validate", new TokenRequest
        {
            Key = licenseKey.Trim(),
            Fingerprint = MachineFingerprint.Get(),
            AppVersion = AppVersion()
        });

    /// <summary>Gibt den Geräte-Slot dieser Lizenz auf dem Server wieder frei.</summary>
    public Task<LicenseApiResult> DeactivateAsync(string licenseKey)
        => PostAsync("/deactivate", new TokenRequest
        {
            Key = licenseKey.Trim(),
            Fingerprint = MachineFingerprint.Get(),
            AppVersion = AppVersion()
        });

    /// <summary>
    /// Lädt das Premium-Modul über einen kurzlebigen Download-Token (aus Aktivierung/Refresh).
    /// Liefert die DLL-Bytes oder null bei Fehler.
    /// </summary>
    public async Task<byte[]?> DownloadModuleAsync(string downloadToken)
    {
        try
        {
            string url = $"{BaseUrl}/module?dl={Uri.EscapeDataString(downloadToken)}" +
                         $"&app={Uri.EscapeDataString(AppVersion())}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }
    }

    // ---- intern ---------------------------------------------------------------

    private static async Task<LicenseApiResult> PostAsync(string path, TokenRequest request)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(BaseUrl + path, request);
            var body = await ReadBodyAsync(response);

            if (response.IsSuccessStatusCode && body is not null &&
                !string.IsNullOrWhiteSpace(body.Payload) && !string.IsNullOrWhiteSpace(body.Signature))
            {
                return new LicenseApiResult(true, LicenseApiError.None,
                    body.Payload, body.Signature, body.DownloadToken);
            }

            // Deaktivieren liefert kein Token; 200 ohne Payload gilt dort als Erfolg.
            if (response.IsSuccessStatusCode)
                return new LicenseApiResult(true, LicenseApiError.None);

            return new LicenseApiResult(false, MapError(response.StatusCode, body?.Error));
        }
        catch
        {
            return new LicenseApiResult(false, LicenseApiError.Network);
        }
    }

    private static async Task<TokenResponse?> ReadBodyAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadFromJsonAsync<TokenResponse>(); }
        catch { return null; }
    }

    private static LicenseApiError MapError(HttpStatusCode status, string? error) => status switch
    {
        HttpStatusCode.Conflict => LicenseApiError.DeviceLimit,
        // 429: Ratenbegrenzung – NICHT als „nicht erreichbar" melden, sonst denkt der
        // Nutzer, sein Internet sei defekt, obwohl er nur kurz warten muss.
        HttpStatusCode.TooManyRequests => LicenseApiError.RateLimited,
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest
            => error == "device_limit" ? LicenseApiError.DeviceLimit : LicenseApiError.InvalidKey,
        _ => LicenseApiError.Network
    };

    private static string AppVersion()
        => typeof(LicenseApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private sealed class TokenRequest
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
        [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("payload")] public string? Payload { get; set; }
        [JsonPropertyName("signature")] public string? Signature { get; set; }
        [JsonPropertyName("downloadToken")] public string? DownloadToken { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
