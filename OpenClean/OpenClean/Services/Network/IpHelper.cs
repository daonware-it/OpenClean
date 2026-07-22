using System.Runtime.InteropServices;

namespace OpenClean.Services.Network;

/// <summary>IP-Version für die iphlpapi-Tabellenabfragen (AF_INET / AF_INET6).</summary>
public enum IpVersion
{
    /// <summary>AF_INET.</summary>
    IPv4 = 2,

    /// <summary>AF_INET6.</summary>
    IPv6 = 23
}

/// <summary>
/// Eine Tabellenabfrage: füllt <paramref name="buffer"/> und gibt einen Win32-Code zurück.
/// Bei zu kleinem Puffer wird <see cref="TableBufferReader.ErrorInsufficientBuffer"/> geliefert
/// und <paramref name="size"/> auf die benötigte Größe gesetzt.
/// </summary>
public delegate uint TableQuery(IntPtr buffer, ref int size);

/// <summary>
/// Abstraktion über die beiden iphlpapi-Funktionen. Existiert, damit die Puffer- und
/// Fehlerbehandlung ohne echte Win32-Aufrufe getestet werden kann.
/// </summary>
public interface IExtendedTableApi
{
    /// <summary>GetExtendedTcpTable mit TCP_TABLE_OWNER_PID_ALL.</summary>
    uint QueryTcpTable(IntPtr buffer, ref int size, IpVersion version);

    /// <summary>GetExtendedUdpTable mit UDP_TABLE_OWNER_PID.</summary>
    uint QueryUdpTable(IntPtr buffer, ref int size, IpVersion version);
}

/// <summary>
/// Echte iphlpapi-Anbindung. Reine Leseoperationen – es wird nichts am System verändert.
/// </summary>
public sealed class IpHelperApi : IExtendedTableApi
{
    /// <summary>TCP_TABLE_OWNER_PID_ALL: alle TCP-Verbindungen samt besitzender PID.</summary>
    private const int TcpTableOwnerPidAll = 5;

    /// <summary>UDP_TABLE_OWNER_PID: alle UDP-Endpunkte samt besitzender PID.</summary>
    private const int UdpTableOwnerPid = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion, int tblClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwOutBufLen,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion, int tblClass, int reserved);

    public uint QueryTcpTable(IntPtr buffer, ref int size, IpVersion version)
        => GetExtendedTcpTable(buffer, ref size, false, (int)version, TcpTableOwnerPidAll, 0);

    public uint QueryUdpTable(IntPtr buffer, ref int size, IpVersion version)
        => GetExtendedUdpTable(buffer, ref size, false, (int)version, UdpTableOwnerPid, 0);
}

/// <summary>
/// Kapselt das „erst Größe erfragen, dann Puffer anfordern"-Muster der iphlpapi-Tabellen.
/// Die Tabelle kann zwischen zwei Aufrufen wachsen, deshalb wird der Puffer bei erneutem
/// ERROR_INSUFFICIENT_BUFFER neu angefordert (begrenzt auf <see cref="MaxAttempts"/> Versuche,
/// damit ein dauerhaft wachsendes System keine Endlosschleife auslöst).
/// Jeder <see cref="Marshal.AllocHGlobal(int)"/> wird in einem finally wieder freigegeben.
/// </summary>
public static class TableBufferReader
{
    /// <summary>NO_ERROR.</summary>
    public const uint NoError = 0;

    /// <summary>ERROR_INSUFFICIENT_BUFFER.</summary>
    public const uint ErrorInsufficientBuffer = 122;

    /// <summary>Obergrenze der Puffer-Anläufe.</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Führt <paramref name="query"/> mit passend dimensioniertem Puffer aus und übergibt
    /// den gefüllten Puffer an <paramref name="parse"/>. Bei jedem Fehler wird eine leere
    /// Liste geliefert und <paramref name="error"/> gesetzt – es wird nie geworfen.
    /// </summary>
    public static IReadOnlyList<T> Read<T>(
        TableQuery query, Func<IntPtr, IReadOnlyList<T>> parse, out uint error)
    {
        IntPtr buffer = IntPtr.Zero;
        int size = 0;
        error = NoError;

        try
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                uint code = query(buffer, ref size);

                if (code == NoError)
                {
                    // Ohne Puffer kann nichts geparst werden (Tabelle war leer bzw. der
                    // erste Aufruf meldete unerwartet Erfolg).
                    return buffer == IntPtr.Zero ? Array.Empty<T>() : parse(buffer);
                }

                if (code != ErrorInsufficientBuffer)
                {
                    error = code;
                    return Array.Empty<T>();
                }

                // Puffer zu klein: alten freigeben und in der gemeldeten Größe neu anfordern.
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                }

                if (size <= 0)
                {
                    error = code;
                    return Array.Empty<T>();
                }

                buffer = Marshal.AllocHGlobal(size);
            }

            // Puffer wuchs bei jedem Versuch weiter – aufgeben statt endlos zu drehen.
            error = ErrorInsufficientBuffer;
            return Array.Empty<T>();
        }
        catch
        {
            // Parse-/Marshal-Fehler dürfen die Ansicht nicht abschießen.
            error = uint.MaxValue;
            return Array.Empty<T>();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }
}
