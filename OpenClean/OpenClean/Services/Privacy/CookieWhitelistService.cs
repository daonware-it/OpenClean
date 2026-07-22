using System.Text.RegularExpressions;
using OpenClean.Services;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Verwaltet die Cookie-Whitelist: Domains, deren Cookies bei der Privatsphäre-Bereinigung
/// NIE gelöscht werden (der Nutzer bleibt dort angemeldet). Der Dienst bindet an eine
/// injizierte Liste plus eine Persistierungs-Aktion, damit er sowohl an die echten
/// Einstellungen (App-Singleton) als auch an eine In-Memory-Liste (Tests) gehängt werden kann.
///
/// Alle öffentlichen Methoden sind defensiv: sie reichen niemals eine Ausnahme bis zum
/// Aufrufer durch (im Zweifel <c>false</c> / kein Effekt), passend zum robusten Stil der
/// übrigen Privacy-Klassen.
/// </summary>
public sealed class CookieWhitelistService
{
    /// <summary>Prozessweiter Singleton, gebunden an die persistierten Einstellungen.</summary>
    public static CookieWhitelistService Instance { get; } =
        new(SettingsService.Instance.Current.CookieWhitelist, SettingsService.Instance.Save);

    // Gültige Domain: nur [a-z0-9.-], gültige Labels, mindestens ein Punkt.
    private static readonly Regex DomainPattern =
        new("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IList<string> _store;
    private readonly Action _persist;

    /// <summary>
    /// Erzeugt den Dienst über einer beliebigen Liste (der Speicher) und einer Aktion,
    /// die den Speicher persistiert. Für Tests injizierbar (kein Zugriff auf settings.json).
    /// </summary>
    public CookieWhitelistService(IList<string> store, Action persist)
    {
        _store = store;
        _persist = persist;
    }

    /// <summary>Alphabetisch (ordinal, groß/klein-egal) sortierte, schreibgeschützte Sicht auf die Whitelist.</summary>
    public IReadOnlyList<string> Domains
    {
        get
        {
            try
            {
                return _store.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }

    /// <summary>
    /// Normalisiert die Eingabe und fügt sie hinzu (dedupliziert, persistiert via <c>persist</c>).
    /// Liefert <c>true</c>, wenn tatsächlich NEU hinzugefügt; <c>false</c> bei ungültiger
    /// Eingabe oder Duplikat.
    /// </summary>
    public bool Add(string? domain)
    {
        try
        {
            string? normalized = Normalize(domain);
            if (normalized is null) return false;
            if (_store.Any(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase)))
                return false;

            _store.Add(normalized);
            _persist();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Entfernt den (normalisierten) Eintrag und persistiert. Liefert <c>true</c>, wenn entfernt.</summary>
    public bool Remove(string? domain)
    {
        try
        {
            string? normalized = Normalize(domain);
            if (normalized is null) return false;

            int index = -1;
            for (int i = 0; i < _store.Count; i++)
            {
                if (string.Equals(_store[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0) return false;

            _store.RemoveAt(index);
            _persist();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Liefert <c>true</c>, wenn der Host durch mindestens einen Whitelist-Eintrag geschützt ist.
    /// Robust: wirft nie.
    /// </summary>
    public bool Contains(string? host)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            foreach (var domain in _store)
            {
                if (HostMatches(host, domain)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalisiert eine Nutzereingabe zu einer Domain: trim, lowercase (invariant),
    /// Schema (<c>http://</c>, <c>https://</c>) entfernen, alles ab erstem <c>/</c> abschneiden,
    /// Port <c>:123</c> entfernen, führendes <c>www.</c> und führenden <c>.</c> entfernen.
    /// Liefert <c>null</c>, wenn das Ergebnis keine plausible Domain ist (muss mindestens einen
    /// Punkt enthalten und nur <c>[a-z0-9.-]</c> mit gültigen Labels).
    /// </summary>
    public static string? Normalize(string? input)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            string s = input.Trim().ToLowerInvariant();

            // Schema entfernen.
            if (s.StartsWith("http://", StringComparison.Ordinal))
                s = s.Substring("http://".Length);
            else if (s.StartsWith("https://", StringComparison.Ordinal))
                s = s.Substring("https://".Length);

            // Alles ab erstem '/' abschneiden (Pfad/Query).
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);

            // Port entfernen.
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(0, colon);

            // Führendes 'www.' und führenden '.' entfernen.
            if (s.StartsWith(".", StringComparison.Ordinal))
                s = s.Substring(1);
            if (s.StartsWith("www.", StringComparison.Ordinal))
                s = s.Substring("www.".Length);

            if (s.Length == 0) return null;
            return DomainPattern.IsMatch(s) ? s : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reines Matching (seiteneffektfrei, für Tests). Der Host darf einen führenden <c>.</c>
    /// haben (Chromium/Firefox nutzen <c>".google.com"</c>). <paramref name="domain"/> ist
    /// bereits normalisiert (kein führender Punkt/www). Match, wenn normalisierter Host gleich
    /// <paramref name="domain"/> ist ODER auf <c>"." + domain</c> endet. Groß/klein-egal.
    /// </summary>
    public static bool HostMatches(string? host, string domain)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(domain))
                return false;

            string h = host.Trim().ToLowerInvariant();
            if (h.StartsWith(".", StringComparison.Ordinal))
                h = h.Substring(1);

            string d = domain.Trim().ToLowerInvariant();

            if (string.Equals(h, d, StringComparison.Ordinal)) return true;
            return h.EndsWith("." + d, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
