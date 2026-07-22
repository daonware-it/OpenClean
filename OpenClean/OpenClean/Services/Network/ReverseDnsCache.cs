using System.Collections.Concurrent;
using System.Net;

namespace OpenClean.Services.Network;

/// <summary>
/// Optionale Rückwärtsauflösung IP → Hostname. Bewusst NIE blockierend: <see cref="TryGet"/>
/// liefert nur, was bereits im Cache liegt, und stößt die Auflösung im Hintergrund an.
/// Das Ergebnis wird über den Rückruf nachgetragen. Negative Ergebnisse werden ebenfalls
/// gecacht, damit nicht bei jedem Abfragezyklus erneut ins Netz gefragt wird.
/// </summary>
public sealed class ReverseDnsCache
{
    public static ReverseDnsCache Instance { get; } = new();

    /// <summary>Aufgelöste Namen; "" = nachweislich nicht auflösbar.</summary>
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Laufende Auflösungen – verhindert Mehrfachanfragen für dieselbe IP.</summary>
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kappt einzelne hängende Auflösungen, damit die Hintergrundlast begrenzt bleibt.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Bereits bekannter Hostname oder <c>null</c>. Löst NICHT aus und wartet nie.
    /// </summary>
    public string? TryGet(IPAddress address)
    {
        if (!_cache.TryGetValue(address.ToString(), out string? host)) return null;
        return string.IsNullOrEmpty(host) ? null : host;
    }

    /// <summary>
    /// Stößt – falls nötig – die Auflösung im Hintergrund an. <paramref name="onResolved"/>
    /// läuft auf einem Threadpool-Thread; der Aufrufer muss selbst zum Dispatcher wechseln.
    /// Lokale Adressen werden übersprungen (Kern des Features ist „nach außen").
    /// </summary>
    public void Request(IPAddress address, Action<string> onResolved)
    {
        if (NetworkAddressInfo.IsLocal(address)) return;

        string key = address.ToString();
        if (_cache.ContainsKey(key)) return;
        if (!_pending.TryAdd(key, 0)) return;

        _ = ResolveAsync(address, key, onResolved);
    }

    private async Task ResolveAsync(IPAddress address, string key, Action<string> onResolved)
    {
        string host = "";
        try
        {
            // WaitAsync statt CancellationToken: die IPAddress-Überladung von
            // GetHostEntryAsync kennt keinen Token. Die Auflösung selbst läuft im
            // Hintergrund weiter, blockiert aber nichts.
            var entry = await Dns.GetHostEntryAsync(address)
                .WaitAsync(Timeout).ConfigureAwait(false);
            // Ein PTR, der nur die IP zurückgibt, ist keine zusätzliche Information.
            if (!string.Equals(entry.HostName, key, StringComparison.OrdinalIgnoreCase))
                host = entry.HostName;
        }
        catch
        {
            // Kein PTR-Eintrag, Timeout, kein Netz … → negativ cachen und Ruhe geben.
        }
        finally
        {
            _cache[key] = host;
            _pending.TryRemove(key, out _);
        }

        if (host.Length > 0)
        {
            try { onResolved(host); }
            catch { /* Rückruf-Fehler dürfen den Hintergrundlauf nicht abschießen */ }
        }
    }
}
