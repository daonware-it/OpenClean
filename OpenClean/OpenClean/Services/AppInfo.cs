using System.Reflection;

namespace OpenClean.Services;

/// <summary>
/// Zentrale Quelle für App-Metadaten. Die Produktversion wird zur Laufzeit aus der
/// Assembly gelesen – gespeist aus <c>&lt;Version&gt;</c> in <c>OpenClean.csproj</c> –
/// und darf nirgends sonst hartkodiert werden. Wer die Version anzeigt oder
/// versendet (Sidebar, Lizenzserver-Anfragen …), referenziert <see cref="Version"/>.
/// </summary>
public static class AppInfo
{
    /// <summary>Produktversion in der Form <c>1.3.0</c> (drei Stellen, ohne Revision).</summary>
    public static string Version { get; }
        = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
