using OpenClean.Services.Privacy;

namespace OpenClean.Services;

/// <summary>
/// Zentrale Registrierung ALLER Privatsphäre-Provider (feste Liste, Owner: Agent 1).
/// Aggregiert die 8 Provider-Klassen und liefert die auf diesem System verfügbaren.
///
/// WICHTIG: Agent 2 &amp; 3 ändern diese Liste NICHT – sie füllen nur die Bodies ihrer
/// eigenen Provider-Dateien. Neue Provider werden ausschließlich hier registriert.
/// </summary>
public sealed class PrivacyScannerService
{
    private readonly IReadOnlyList<IPrivacyProvider> _providers = new IPrivacyProvider[]
    {
        // Agent 1 (voll implementiert)
        new BrowserHistoryProvider(),   // Task 1
        new CookiesProvider(),          // Task 2
        // Agent 2 (Stubs)
        new StartMenuSearchProvider(),  // Task 3
        new ExplorerSearchProvider(),   // Task 4
        new RecentFilesProvider(),      // Task 5
        new OfficeMruProvider(),        // Task 7
        // Agent 3 (Stubs)
        new MediaPlayerProvider(),      // Task 6
        new ClipboardProvider()         // Task 8
    };

    /// <summary>Alle registrierten Provider (unabhängig von der Verfügbarkeit).</summary>
    public IReadOnlyList<IPrivacyProvider> AllProviders => _providers;

    /// <summary>Nur die auf diesem System verfügbaren Provider (install-gated).</summary>
    public IReadOnlyList<IPrivacyProvider> AvailableProviders()
        => _providers.Where(p => SafeIsAvailable(p)).ToList();

    private static bool SafeIsAvailable(IPrivacyProvider provider)
    {
        // Ein defekter Provider darf die Auflistung der übrigen nicht abbrechen.
        try { return provider.IsAvailable; }
        catch { return false; }
    }
}
