using System.Net;
using System.Net.Sockets;

namespace OpenClean.Services.Network;

/// <summary>Transportprotokoll einer Verbindung.</summary>
public enum NetworkProtocol
{
    Tcp,
    Udp
}

/// <summary>
/// Zustand einer Verbindung, auf die für den Nutzer relevanten Stufen zusammengefasst.
/// Der rohe TCP-Zustand bleibt in <see cref="NetworkConnection.RawState"/> erhalten.
/// </summary>
public enum NetworkConnectionState
{
    /// <summary>Nicht ermittelbar.</summary>
    Unbekannt,

    /// <summary>ESTABLISHED – Daten fließen tatsächlich.</summary>
    Aktiv,

    /// <summary>SYN_SENT / SYN_RCVD – Verbindung wird gerade aufgebaut.</summary>
    Aufbau,

    /// <summary>LISTEN – wartet auf eingehende Verbindungen.</summary>
    Lauschend,

    /// <summary>FIN_WAIT/CLOSE_WAIT/CLOSING/LAST_ACK/TIME_WAIT – Abbau läuft.</summary>
    Wartend,

    /// <summary>CLOSED / DELETE_TCB.</summary>
    Geschlossen,

    /// <summary>UDP: verbindungsloser Endpunkt, kein Zustand vorhanden.</summary>
    Datagramm
}

/// <summary>
/// Bildet die MIB_TCP_STATE-Werte von iphlpapi auf <see cref="NetworkConnectionState"/> ab.
/// </summary>
public static class TcpStateMapper
{
    /// <summary>Grobzustand für einen rohen MIB_TCP_STATE-Wert (1–12).</summary>
    public static NetworkConnectionState Map(uint rawState) => rawState switch
    {
        1 => NetworkConnectionState.Geschlossen,   // CLOSED
        2 => NetworkConnectionState.Lauschend,     // LISTEN
        3 or 4 => NetworkConnectionState.Aufbau,   // SYN_SENT, SYN_RCVD
        5 => NetworkConnectionState.Aktiv,         // ESTABLISHED
        6 or 7 or 8 or 9 or 10 or 11 => NetworkConnectionState.Wartend,
        12 => NetworkConnectionState.Geschlossen,  // DELETE_TCB
        _ => NetworkConnectionState.Unbekannt
    };

    /// <summary>Lokalisierte Beschriftung eines Grobzustands.</summary>
    public static string Display(NetworkConnectionState state) => state switch
    {
        NetworkConnectionState.Aktiv => Loc.T("network.state.active"),
        NetworkConnectionState.Aufbau => Loc.T("network.state.connecting"),
        NetworkConnectionState.Lauschend => Loc.T("network.state.listening"),
        NetworkConnectionState.Wartend => Loc.T("network.state.waiting"),
        NetworkConnectionState.Geschlossen => Loc.T("network.state.closed"),
        NetworkConnectionState.Datagramm => Loc.T("network.state.datagram"),
        _ => Loc.T("network.state.unknown")
    };
}

/// <summary>
/// Ein Endpunkt (Adresse + Port) mit korrekter Darstellung: IPv6 wird geklammert,
/// damit der Port eindeutig vom Adressteil getrennt ist (RFC 3986).
/// </summary>
public sealed class NetworkEndpoint
{
    public NetworkEndpoint(IPAddress address, int port)
    {
        Address = address;
        Port = port;
    }

    public IPAddress Address { get; }
    public int Port { get; }

    /// <summary>„1.2.3.4:443" bzw. „[2606:1::1]:443".</summary>
    public string Display => Format(Address, Port);

    /// <summary>Adresse ohne Port (für Reverse-DNS und Sortierung).</summary>
    public string AddressText => Address.ToString();

    /// <inheritdoc cref="Display"/>
    public static string Format(IPAddress? address, int port)
    {
        if (address is null) return "";
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}"
            : $"{address}:{port}";
    }

    /// <summary>
    /// Dekodiert einen Port aus dem von iphlpapi gelieferten DWORD: die beiden
    /// niederwertigen Bytes enthalten den Port in Netzwerk-Byte-Reihenfolge (big endian),
    /// die oberen beiden Bytes sind unbenutzt.
    /// </summary>
    public static int PortFromNetworkOrder(uint value)
        => (int)(((value & 0x000000FFu) << 8) | ((value & 0x0000FF00u) >> 8));
}

/// <summary>
/// Entscheidet, ob eine Adresse rein lokal ist. Kern des Features ist „nach außen":
/// Loopback (127.0.0.0/8, ::1), die unbestimmte Adresse (0.0.0.0, ::) und
/// Link-Local (169.254.0.0/16, fe80::/10) verlassen den Rechner nicht.
/// </summary>
public static class NetworkAddressInfo
{
    /// <summary>True, wenn die Adresse den Rechner nicht verlässt.</summary>
    public static bool IsLocal(IPAddress? address)
    {
        if (address is null) return true;

        // ::ffff:127.0.0.1 & Co. zuerst auf ihre IPv4-Form zurückführen.
        IPAddress a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(a)) return true;
        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;

        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = a.GetAddressBytes();
            return b[0] == 169 && b[1] == 254; // Link-Local (APIPA)
        }

        return a.IsIPv6LinkLocal;
    }
}

/// <summary>
/// Eine beobachtete Verbindung bzw. ein UDP-Endpunkt samt besitzendem Prozess.
/// Reine Momentaufnahme – unveränderlich, thread-sicher übergebbar.
/// </summary>
public sealed class NetworkConnection
{
    public NetworkProtocol Protocol { get; init; }

    public required NetworkEndpoint Local { get; init; }

    /// <summary>Gegenstelle; bei UDP <c>null</c> (verbindungslos).</summary>
    public NetworkEndpoint? Remote { get; init; }

    public NetworkConnectionState State { get; init; }

    /// <summary>Roher MIB_TCP_STATE-Wert (0 bei UDP).</summary>
    public uint RawState { get; init; }

    public int ProcessId { get; init; }

    /// <summary>Prozessname ohne Endung, leer wenn nicht ermittelbar.</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>Vollständiger Pfad der EXE; leer, wenn er nicht gelesen werden konnte
    /// (geschützte Prozesse, System, PID 0/4).</summary>
    public string ProcessPath { get; init; } = "";

    /// <summary>
    /// True, wenn die Verbindung den Rechner nicht verlässt (Loopback, gebundener
    /// Lauschsocket ohne Gegenstelle …).
    /// </summary>
    public bool IsLocalOnly =>
        Remote is null
            ? true
            : NetworkAddressInfo.IsLocal(Remote.Address) || Remote.Port == 0;

    /// <summary>
    /// Stabile Identität einer Verbindung über mehrere Abfragen hinweg – Grundlage für das
    /// inkrementelle Aktualisieren der Liste (keine Neuaufbauten, keine Selektionsverluste).
    /// Der Zustand gehört bewusst NICHT dazu: er ändert sich an derselben Verbindung.
    /// </summary>
    public string Key =>
        $"{Protocol}|{Local.Display}|{Remote?.Display ?? "-"}|{ProcessId}";
}
