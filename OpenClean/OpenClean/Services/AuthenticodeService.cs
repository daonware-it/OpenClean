using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace OpenClean.Services;

/// <summary>
/// Liest den verifizierten Authenticode-Herausgeber einer Datei (Subject Common Name der
/// Signatur) – genutzt von Autostart und Kontextmenü, um Einträge einem Hersteller zuzuordnen.
/// Ergebnisse werden pro Pfad gecacht; unsignierte oder nicht lesbare Dateien liefern "".
/// </summary>
public static class AuthenticodeService
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Herausgeber der Datei; leer, wenn unsigniert/nicht lesbar.</summary>
    public static string PublisherFor(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return "";

        lock (Cache)
        {
            if (Cache.TryGetValue(filePath, out var cached))
                return cached;
        }

        string publisher = "";
        try
        {
            // CreateFromSignedFile extrahiert das Authenticode-Zertifikat aus der Datei.
            // Für dieses Szenario existiert kein nicht-veralteter Ersatz -> gezielt unterdrücken.
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            publisher = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "";
        }
        catch { /* unsigniert/nicht lesbar -> leer */ }

        lock (Cache)
        {
            Cache[filePath] = publisher;
        }

        return publisher;
    }
}
