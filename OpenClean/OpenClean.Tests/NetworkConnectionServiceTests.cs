using System;
using System.Collections.Generic;
using System.Net;
using OpenClean.Services.Network;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die Teile der Netzwerk-Transparenz ab, die ohne Administratorrechte und ohne
/// Oberfläche prüfbar sind: Puffer-/Fehlerbehandlung der iphlpapi-Abfrage (über die
/// injizierbare <see cref="IExtendedTableApi"/>-Abstraktion), TCP-Zustandsabbildung,
/// Endpunkt-Formatierung, Port-Dekodierung, Loopback-Erkennung und die Filterlogik.
/// </summary>
public class NetworkConnectionServiceTests
{
    // ---- Puffer-Retry (ERROR_INSUFFICIENT_BUFFER) ---------------------------

    /// <summary>
    /// Attrappe der iphlpapi: meldet die ersten <c>InsufficientCalls</c> Aufrufe als
    /// „Puffer zu klein" (und setzt die benötigte Größe), danach Erfolg.
    /// </summary>
    private sealed class FakeApi : IExtendedTableApi
    {
        public int InsufficientCalls { get; init; }
        public int NeededSize { get; init; } = 64;
        public uint FinalCode { get; init; } = TableBufferReader.NoError;

        public int Calls { get; private set; }
        public readonly List<int> SeenSizes = new();

        public uint QueryTcpTable(IntPtr buffer, ref int size, IpVersion version)
        {
            Calls++;
            SeenSizes.Add(size);
            if (Calls <= InsufficientCalls)
            {
                size = NeededSize;
                return TableBufferReader.ErrorInsufficientBuffer;
            }

            return FinalCode;
        }

        public uint QueryUdpTable(IntPtr buffer, ref int size, IpVersion version)
            => QueryTcpTable(buffer, ref size, version);
    }

    [Fact]
    public void PufferRetry_FordertNachInsufficientBufferNeuAnUndLiefertDaten()
    {
        var api = new FakeApi { InsufficientCalls = 1, NeededSize = 128 };

        var result = TableBufferReader.Read<int>(
            (IntPtr b, ref int s) => api.QueryTcpTable(b, ref s, IpVersion.IPv4),
            _ => new[] { 1, 2, 3 },
            out uint error);

        Assert.Equal(TableBufferReader.NoError, error);
        Assert.Equal(new[] { 1, 2, 3 }, result);
        Assert.Equal(2, api.Calls);              // Größe erfragen, dann echt lesen
        Assert.Equal(0, api.SeenSizes[0]);       // erster Aufruf ohne Puffer
        Assert.Equal(128, api.SeenSizes[1]);     // zweiter mit der gemeldeten Größe
    }

    [Fact]
    public void PufferRetry_TabelleWaechstStaendig_BrichtNachMaxAttemptsAb()
    {
        // Meldet IMMER „zu klein" – die Schleife darf nicht endlos drehen.
        var api = new FakeApi { InsufficientCalls = int.MaxValue };

        var result = TableBufferReader.Read<int>(
            (IntPtr b, ref int s) => api.QueryTcpTable(b, ref s, IpVersion.IPv4),
            _ => new[] { 1 },
            out uint error);

        Assert.Empty(result);
        Assert.Equal(TableBufferReader.ErrorInsufficientBuffer, error);
        Assert.Equal(TableBufferReader.MaxAttempts, api.Calls);
    }

    [Fact]
    public void PufferRetry_AndererWin32Fehler_WirdDurchgereichtOhneAusnahme()
    {
        const uint errorAccessDenied = 5;
        var api = new FakeApi { InsufficientCalls = 0, FinalCode = errorAccessDenied };

        var result = TableBufferReader.Read<int>(
            (IntPtr b, ref int s) => api.QueryTcpTable(b, ref s, IpVersion.IPv4),
            _ => new[] { 1 },
            out uint error);

        Assert.Empty(result);
        Assert.Equal(errorAccessDenied, error);
        Assert.Equal(1, api.Calls); // kein sinnloser zweiter Versuch
    }

    [Fact]
    public void PufferRetry_ErfolgOhnePuffer_LiefertLeerStattZuParsen()
    {
        // NO_ERROR beim allerersten Aufruf: es gibt keinen Puffer, den man parsen könnte.
        var api = new FakeApi { InsufficientCalls = 0 };

        var result = TableBufferReader.Read<int>(
            (IntPtr b, ref int s) => api.QueryTcpTable(b, ref s, IpVersion.IPv4),
            _ => throw new InvalidOperationException("darf nicht geparst werden"),
            out uint error);

        Assert.Empty(result);
        Assert.Equal(TableBufferReader.NoError, error);
    }

    [Fact]
    public void PufferRetry_ParseFehler_WirdAufgefangen()
    {
        var api = new FakeApi { InsufficientCalls = 1 };

        var result = TableBufferReader.Read<int>(
            (IntPtr b, ref int s) => api.QueryTcpTable(b, ref s, IpVersion.IPv4),
            _ => throw new InvalidOperationException("kaputte Zeile"),
            out uint error);

        Assert.Empty(result);
        Assert.NotEqual(TableBufferReader.NoError, error);
    }

    // ---- TCP-Zustandsabbildung ---------------------------------------------

    [Theory]
    [InlineData(1u, NetworkConnectionState.Geschlossen)]   // CLOSED
    [InlineData(2u, NetworkConnectionState.Lauschend)]     // LISTEN
    [InlineData(3u, NetworkConnectionState.Aufbau)]        // SYN_SENT
    [InlineData(4u, NetworkConnectionState.Aufbau)]        // SYN_RCVD
    [InlineData(5u, NetworkConnectionState.Aktiv)]         // ESTABLISHED
    [InlineData(6u, NetworkConnectionState.Wartend)]       // FIN_WAIT1
    [InlineData(7u, NetworkConnectionState.Wartend)]       // FIN_WAIT2
    [InlineData(8u, NetworkConnectionState.Wartend)]       // CLOSE_WAIT
    [InlineData(9u, NetworkConnectionState.Wartend)]       // CLOSING
    [InlineData(10u, NetworkConnectionState.Wartend)]      // LAST_ACK
    [InlineData(11u, NetworkConnectionState.Wartend)]      // TIME_WAIT
    [InlineData(12u, NetworkConnectionState.Geschlossen)]  // DELETE_TCB
    [InlineData(0u, NetworkConnectionState.Unbekannt)]
    [InlineData(99u, NetworkConnectionState.Unbekannt)]
    public void TcpZustand_WirdAufGrobzustandAbgebildet(uint raw, NetworkConnectionState expected)
        => Assert.Equal(expected, TcpStateMapper.Map(raw));

    // ---- Endpunkt-Formatierung ----------------------------------------------

    [Fact]
    public void Endpunkt_IPv4_WirdOhneKlammernFormatiert()
        => Assert.Equal("93.184.216.34:443",
            NetworkEndpoint.Format(IPAddress.Parse("93.184.216.34"), 443));

    [Fact]
    public void Endpunkt_IPv6_WirdGeklammert_DamitDerPortEindeutigBleibt()
        => Assert.Equal("[2606:2800:220:1:248:1893:25c8:1946]:443",
            NetworkEndpoint.Format(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), 443));

    [Fact]
    public void Endpunkt_IPv6Kurzform_BleibtGeklammert()
        => Assert.Equal("[::1]:80", NetworkEndpoint.Format(IPAddress.IPv6Loopback, 80));

    [Fact]
    public void Endpunkt_OhneAdresse_LiefertLeerStattAusnahme()
        => Assert.Equal("", NetworkEndpoint.Format(null, 443));

    [Fact]
    public void Endpunkt_DisplayNutztDieselbeFormatierung()
    {
        var ep = new NetworkEndpoint(IPAddress.Parse("2001:db8::1"), 8080);
        Assert.Equal("[2001:db8::1]:8080", ep.Display);
        Assert.Equal("2001:db8::1", ep.AddressText);
    }

    // ---- Port-Dekodierung ---------------------------------------------------

    [Theory]
    [InlineData(0x0000BB01u, 443)]   // 0x01BB = 443, byte-vertauscht abgelegt
    [InlineData(0x00005000u, 80)]    // 0x0050 = 80
    [InlineData(0u, 0)]
    [InlineData(0xFFFF0000u, 0)]     // obere Bytes sind unbenutzt und werden ignoriert
    public void Port_WirdAusNetzwerkByteReihenfolgeDekodiert(uint raw, int expected)
        => Assert.Equal(expected, NetworkEndpoint.PortFromNetworkOrder(raw));

    [Fact]
    public void Port_HoechsterWert_WirdKorrektDekodiert()
        => Assert.Equal(65535, NetworkEndpoint.PortFromNetworkOrder(0x0000FFFFu));

    // ---- Loopback-/Lokalerkennung ------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.6.7", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]  // IPv4-gemappte Loopback-Adresse
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("169.254.10.20", true)]     // Link-Local (APIPA)
    [InlineData("fe80::1", true)]           // IPv6 Link-Local
    [InlineData("93.184.216.34", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("192.168.0.10", false)]     // LAN verlässt den Rechner sehr wohl
    [InlineData("2606:2800:220::1", false)]
    public void LokaleAdressen_WerdenErkannt(string address, bool expected)
        => Assert.Equal(expected, NetworkAddressInfo.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void LokaleAdresse_Null_GiltAlsLokal()
        => Assert.True(NetworkAddressInfo.IsLocal(null));

    [Fact]
    public void Verbindung_OhneGegenstelle_IstNurLokal()
    {
        var udp = new NetworkConnection
        {
            Protocol = NetworkProtocol.Udp,
            Local = new NetworkEndpoint(IPAddress.Any, 53),
            Remote = null,
            State = NetworkConnectionState.Datagramm
        };

        Assert.True(udp.IsLocalOnly);
    }

    [Fact]
    public void Verbindung_NachAussen_IstNichtNurLokal()
    {
        var tcp = Connection("chrome", IPAddress.Parse("93.184.216.34"), 443);
        Assert.False(tcp.IsLocalOnly);
    }

    [Fact]
    public void Verbindung_MitPortNull_GiltAlsLokal_DennEsGibtKeineEchteGegenstelle()
    {
        // Lauschsockets tragen 0.0.0.0:0 als „Gegenstelle" – das ist keine Verbindung nach außen.
        var listener = Connection("svchost", IPAddress.Parse("93.184.216.34"), 0);
        Assert.True(listener.IsLocalOnly);
    }

    // ---- Identität (inkrementelles Aktualisieren) --------------------------

    [Fact]
    public void Schluessel_IstGleichWennSichNurDerZustandAendert()
    {
        var a = Connection("chrome", IPAddress.Parse("1.2.3.4"), 443, NetworkConnectionState.Aufbau);
        var b = Connection("chrome", IPAddress.Parse("1.2.3.4"), 443, NetworkConnectionState.Aktiv);

        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void Schluessel_UnterscheidetGegenstelleUndProzess()
    {
        var a = Connection("chrome", IPAddress.Parse("1.2.3.4"), 443);
        var b = Connection("chrome", IPAddress.Parse("5.6.7.8"), 443);
        var c = Connection("chrome", IPAddress.Parse("1.2.3.4"), 443, pid: 999);

        Assert.NotEqual(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
    }

    // ---- Filterlogik --------------------------------------------------------

    [Fact]
    public void Filter_ExternalOnly_BlendetLokaleVerbindungenAus()
    {
        Assert.False(NetworkFilter.Matches("chrome", @"C:\chrome.exe", "127.0.0.1:1234", "",
            isLocalOnly: true, searchText: "", externalOnly: true));

        Assert.True(NetworkFilter.Matches("chrome", @"C:\chrome.exe", "1.2.3.4:443", "",
            isLocalOnly: false, searchText: "", externalOnly: true));
    }

    [Fact]
    public void Filter_OhneExternalOnly_ZeigtAuchLokales()
        => Assert.True(NetworkFilter.Matches("chrome", @"C:\chrome.exe", "127.0.0.1:1234", "",
            isLocalOnly: true, searchText: "", externalOnly: false));

    [Fact]
    public void Filter_LeererText_ZeigtAlles()
    {
        Assert.True(NetworkFilter.Matches("chrome", "", "1.2.3.4:443", "", false, "", false));
        Assert.True(NetworkFilter.Matches("chrome", "", "1.2.3.4:443", "", false, "   ", false));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("CHROME")]      // Groß-/Kleinschreibung egal
    [InlineData("hrom")]        // Teiltreffer
    [InlineData("program")]     // trifft den Pfad
    [InlineData("93.184")]      // trifft die Zieladresse
    [InlineData("example.com")] // trifft den aufgelösten Hostnamen
    public void Filter_TextTrifftProzessPfadZielUndHost(string query)
        => Assert.True(NetworkFilter.Matches(
            "chrome", @"C:\Program Files\chrome.exe", "93.184.216.34:443", "www.example.com",
            isLocalOnly: false, searchText: query, externalOnly: true));

    [Fact]
    public void Filter_TextOhneTreffer_BlendetAus()
        => Assert.False(NetworkFilter.Matches(
            "chrome", @"C:\Program Files\chrome.exe", "93.184.216.34:443", "www.example.com",
            isLocalOnly: false, searchText: "firefox", externalOnly: true));

    [Fact]
    public void Filter_ExternalOnlyGewinntGegenTextTreffer()
        => Assert.False(NetworkFilter.Matches(
            "chrome", @"C:\chrome.exe", "127.0.0.1:9222", "",
            isLocalOnly: true, searchText: "chrome", externalOnly: true));

    // ---- Prozesspfad --------------------------------------------------------

    [Fact]
    public void ProzessPfad_UnbekanntePid_LiefertLeereIdentitaetOhneAusnahme()
    {
        var cache = new ProcessPathCache();
        var id = cache.Get(0x7FFFFFFF); // mit an Sicherheit grenzender Wahrscheinlichkeit tot

        Assert.NotNull(id);
        Assert.Equal("", id.Path); // kein Pfad -> die Ansicht zeigt „unbekannt"
    }

    [Fact]
    public void ProzessPfad_KernelPseudoPids_HabenNamenAberKeinenPfad()
    {
        var cache = new ProcessPathCache();

        Assert.Equal("System", cache.Get(4).Name);
        Assert.Equal("", cache.Get(4).Path);
        Assert.Equal("", cache.Get(0).Path);
    }

    [Fact]
    public void ProzessPfad_EigenerProzess_WirdAufgeloestUndGecacht()
    {
        var cache = new ProcessPathCache();
        int pid = Environment.ProcessId;

        var first = cache.Get(pid);
        var second = cache.Get(pid);

        Assert.NotEqual("", first.Name);
        Assert.Same(first, second); // zweiter Zugriff kommt aus dem Cache
    }

    [Fact]
    public void ProzessPfad_Trim_EntferntVerschwundenePids()
    {
        var cache = new ProcessPathCache();
        int pid = Environment.ProcessId;

        var first = cache.Get(pid);
        cache.Trim(new HashSet<int>()); // keine PID mehr „lebendig"
        var second = cache.Get(pid);

        // Nach dem Trim wird frisch aufgelöst – sonst zeigte eine wiederverwendete
        // PID weiterhin den Namen des alten Programms.
        Assert.NotSame(first, second);
        Assert.Equal(first.Name, second.Name);
    }

    // ---- Zusammenspiel (ohne Administratorrechte lauffähig) ----------------

    [Fact]
    public void Abfrage_GegenDasEchteSystem_LiefertPlausibleZeilenOhneAusnahme()
    {
        var connections = new NetworkConnectionService().GetConnections();

        Assert.NotNull(connections);
        // Auf jedem laufenden Windows existieren TCP-/UDP-Endpunkte.
        Assert.NotEmpty(connections);
        Assert.All(connections, c =>
        {
            Assert.NotNull(c.Local);
            Assert.InRange(c.Local.Port, 0, 65535);
            Assert.True(c.ProcessId >= 0);
            Assert.NotNull(c.Key);
            if (c.Protocol == NetworkProtocol.Udp) Assert.Null(c.Remote);
        });
    }

    [Fact]
    public void Abfrage_MitFehlerhafterApi_LiefertLeerStattZuWerfen()
    {
        var api = new FakeApi { InsufficientCalls = 0, FinalCode = 5 /* ERROR_ACCESS_DENIED */ };
        var service = new NetworkConnectionService(api);

        var connections = service.GetConnections();

        Assert.Empty(connections);
        Assert.Equal(5u, service.LastError);
    }

    private static NetworkConnection Connection(
        string process, IPAddress remote, int remotePort,
        NetworkConnectionState state = NetworkConnectionState.Aktiv, int pid = 1234)
        => new()
        {
            Protocol = NetworkProtocol.Tcp,
            Local = new NetworkEndpoint(IPAddress.Parse("192.168.0.5"), 51000),
            Remote = new NetworkEndpoint(remote, remotePort),
            State = state,
            ProcessId = pid,
            ProcessName = process
        };
}
