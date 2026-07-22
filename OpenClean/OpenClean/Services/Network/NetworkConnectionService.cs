using System.Net;
using System.Runtime.InteropServices;

namespace OpenClean.Services.Network;

/// <summary>
/// Liest – rein lesend – alle aktiven TCP-Verbindungen und UDP-Endpunkte (IPv4 und IPv6)
/// samt besitzendem Prozess über <c>GetExtendedTcpTable</c>/<c>GetExtendedUdpTable</c>.
///
/// Bewusst NICHT enthalten ist das übertragene Datenvolumen je Prozess: Windows bietet dafür
/// keine rein lesende Schnittstelle (siehe Kommentar an <see cref="GetConnections"/>).
/// </summary>
public sealed class NetworkConnectionService
{
    /// <summary>Gemeinsame Instanz für die Ansicht (hält den PID→Pfad-Cache warm).</summary>
    public static NetworkConnectionService Instance { get; } = new();

    private readonly IExtendedTableApi _api;
    private readonly ProcessPathCache _paths = new();

    public NetworkConnectionService() : this(new IpHelperApi()) { }

    /// <summary>Für Tests: erlaubt eine Attrappe statt der echten iphlpapi.</summary>
    public NetworkConnectionService(IExtendedTableApi api) => _api = api;

    /// <summary>Letzter Win32-Fehlercode einer Tabellenabfrage (0 = alles gut).</summary>
    public uint LastError { get; private set; }

    /// <summary>
    /// Momentaufnahme aller Verbindungen. Fehler einzelner Tabellen (z. B. IPv6 deaktiviert)
    /// verschlucken die übrigen nicht; es wird nie geworfen.
    ///
    /// Zum Datenvolumen: pro TCP-Verbindung ließe es sich nur über
    /// <c>GetPerTcpConnectionEStats</c> ermitteln, was zuvor <c>SetPerTcpConnectionEStats</c>
    /// erfordert – also einen schreibenden Eingriff in den TCP-Stack. Das widerspricht dem
    /// rein lesenden Charakter dieser Ansicht, deckt UDP gar nicht ab und liefert für
    /// geschützte Prozesse ohnehin nichts. Deshalb wird die Spalte nicht angeboten.
    /// </summary>
    public IReadOnlyList<NetworkConnection> GetConnections()
    {
        var rows = new List<NetworkConnection>(256);
        uint firstError = 0;

        // Jede Tabelle einzeln: fällt eine aus (z. B. IPv6 deaktiviert), bleiben die anderen
        // nutzbar. TableBufferReader wirft nicht, sondern meldet über den Fehlercode.
        rows.AddRange(ReadTcp(IpVersion.IPv4, ParseTcp4, ref firstError));
        rows.AddRange(ReadTcp(IpVersion.IPv6, ParseTcp6, ref firstError));
        rows.AddRange(ReadUdp(IpVersion.IPv4, ParseUdp4, ref firstError));
        rows.AddRange(ReadUdp(IpVersion.IPv6, ParseUdp6, ref firstError));

        LastError = firstError;

        // PIDs, die es nicht mehr gibt, aus dem Cache werfen – sonst zeigte eine
        // wiederverwendete PID den Namen des alten Programms.
        var livePids = rows.Select(r => r.ProcessId).ToHashSet();
        _paths.Trim(livePids);

        // Prozessnamen/-pfade anreichern (gecacht, deshalb pro Zyklus nur für neue PIDs teuer).
        var result = new List<NetworkConnection>(rows.Count);
        foreach (var row in rows)
        {
            var id = _paths.Get(row.ProcessId);
            result.Add(new NetworkConnection
            {
                Protocol = row.Protocol,
                Local = row.Local,
                Remote = row.Remote,
                State = row.State,
                RawState = row.RawState,
                ProcessId = row.ProcessId,
                ProcessName = id.Name,
                ProcessPath = id.Path
            });
        }

        return result;
    }

    /// <summary>Verwirft den PID→Pfad-Cache (beim Öffnen der Ansicht).</summary>
    public void ResetCache() => _paths.Clear();

    private IReadOnlyList<NetworkConnection> ReadTcp(
        IpVersion version, Func<IntPtr, IReadOnlyList<NetworkConnection>> parse, ref uint firstError)
    {
        var api = _api;
        var list = TableBufferReader.Read(
            (IntPtr buffer, ref int size) => api.QueryTcpTable(buffer, ref size, version),
            parse, out uint error);
        if (error != 0 && firstError == 0) firstError = error;
        return list;
    }

    private IReadOnlyList<NetworkConnection> ReadUdp(
        IpVersion version, Func<IntPtr, IReadOnlyList<NetworkConnection>> parse, ref uint firstError)
    {
        var api = _api;
        var list = TableBufferReader.Read(
            (IntPtr buffer, ref int size) => api.QueryUdpTable(buffer, ref size, version),
            parse, out uint error);
        if (error != 0 && firstError == 0) firstError = error;
        return list;
    }

    // ---- Native Tabellenstrukturen -----------------------------------------
    // Alle Tabellen beginnen mit DWORD dwNumEntries, danach folgen die Zeilen direkt.

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    // ---- Parser -------------------------------------------------------------

    /// <summary>Läuft die Zeilen einer iphlpapi-Tabelle ab (dwNumEntries + dichtes Zeilen-Array).</summary>
    private static List<NetworkConnection> ParseRows<TRow>(
        IntPtr table, Func<TRow, NetworkConnection?> convert) where TRow : struct
    {
        int count = Marshal.ReadInt32(table);
        var list = new List<NetworkConnection>(Math.Max(0, count));
        if (count <= 0) return list;

        int rowSize = Marshal.SizeOf<TRow>();
        IntPtr cursor = IntPtr.Add(table, 4);

        for (int i = 0; i < count; i++)
        {
            var row = Marshal.PtrToStructure<TRow>(IntPtr.Add(cursor, i * rowSize));
            var connection = convert(row);
            if (connection is not null) list.Add(connection);
        }

        return list;
    }

    private static IReadOnlyList<NetworkConnection> ParseTcp4(IntPtr table)
        => ParseRows<MIB_TCPROW_OWNER_PID>(table, row => new NetworkConnection
        {
            Protocol = NetworkProtocol.Tcp,
            Local = new NetworkEndpoint(FromV4(row.dwLocalAddr),
                NetworkEndpoint.PortFromNetworkOrder(row.dwLocalPort)),
            Remote = new NetworkEndpoint(FromV4(row.dwRemoteAddr),
                NetworkEndpoint.PortFromNetworkOrder(row.dwRemotePort)),
            RawState = row.dwState,
            State = TcpStateMapper.Map(row.dwState),
            ProcessId = (int)row.dwOwningPid
        });

    private static IReadOnlyList<NetworkConnection> ParseTcp6(IntPtr table)
        => ParseRows<MIB_TCP6ROW_OWNER_PID>(table, row => new NetworkConnection
        {
            Protocol = NetworkProtocol.Tcp,
            Local = new NetworkEndpoint(FromV6(row.ucLocalAddr, row.dwLocalScopeId),
                NetworkEndpoint.PortFromNetworkOrder(row.dwLocalPort)),
            Remote = new NetworkEndpoint(FromV6(row.ucRemoteAddr, row.dwRemoteScopeId),
                NetworkEndpoint.PortFromNetworkOrder(row.dwRemotePort)),
            RawState = row.dwState,
            State = TcpStateMapper.Map(row.dwState),
            ProcessId = (int)row.dwOwningPid
        });

    private static IReadOnlyList<NetworkConnection> ParseUdp4(IntPtr table)
        => ParseRows<MIB_UDPROW_OWNER_PID>(table, row => new NetworkConnection
        {
            Protocol = NetworkProtocol.Udp,
            Local = new NetworkEndpoint(FromV4(row.dwLocalAddr),
                NetworkEndpoint.PortFromNetworkOrder(row.dwLocalPort)),
            Remote = null, // UDP ist verbindungslos – es gibt keine feste Gegenstelle.
            State = NetworkConnectionState.Datagramm,
            ProcessId = (int)row.dwOwningPid
        });

    private static IReadOnlyList<NetworkConnection> ParseUdp6(IntPtr table)
        => ParseRows<MIB_UDP6ROW_OWNER_PID>(table, row => new NetworkConnection
        {
            Protocol = NetworkProtocol.Udp,
            Local = new NetworkEndpoint(FromV6(row.ucLocalAddr, row.dwLocalScopeId),
                NetworkEndpoint.PortFromNetworkOrder(row.dwLocalPort)),
            Remote = null,
            State = NetworkConnectionState.Datagramm,
            ProcessId = (int)row.dwOwningPid
        });

    /// <summary>DWORD in Netzwerk-Byte-Reihenfolge → IPv4-Adresse.</summary>
    private static IPAddress FromV4(uint address)
    {
        try { return new IPAddress(BitConverter.GetBytes(address)); }
        catch { return IPAddress.Any; }
    }

    /// <summary>16 Bytes + Scope-ID → IPv6-Adresse.</summary>
    private static IPAddress FromV6(byte[]? bytes, uint scopeId)
    {
        try
        {
            if (bytes is null || bytes.Length != 16) return IPAddress.IPv6Any;
            return new IPAddress(bytes, scopeId);
        }
        catch
        {
            return IPAddress.IPv6Any;
        }
    }
}
