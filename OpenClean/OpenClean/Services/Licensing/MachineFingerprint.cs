using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace OpenClean.Services.Licensing;

/// <summary>
/// Erzeugt einen anonymen, stabilen Geräte-Hash für die Lizenz-Gerätebindung.
/// Es wird NIE eine Roh-Kennung übertragen, sondern ausschließlich ein SHA-256-Hash
/// der Windows-MachineGuid (Rückfall: Rechnername) mit App-Präfix – daraus lässt sich
/// weder der Rechner identifizieren noch die Kennung zurückrechnen.
/// </summary>
public static class MachineFingerprint
{
    private static string? _cached;

    /// <summary>Der Geräte-Hash als 64-stelliger Hex-String (klein geschrieben).</summary>
    public static string Get()
    {
        if (_cached is not null) return _cached;

        string source = ReadMachineGuid() ?? $"fallback|{Environment.MachineName}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("openclean|" + source));
        _cached = Convert.ToHexString(hash).ToLowerInvariant();
        return _cached;
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            // Explizit die 64-Bit-Sicht: die MachineGuid liegt nur dort.
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            string? guid = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(guid) ? null : guid.Trim();
        }
        catch
        {
            return null;
        }
    }
}
