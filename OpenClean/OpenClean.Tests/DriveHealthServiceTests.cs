using System;
using System.Collections.Generic;
using System.Linq;
using OpenClean.Services.DriveHealth;
using OpenClean.ViewModels;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die Komposition des DriveHealthService ab: die dreistufige Fallback-Kette, die
/// Zuordnung der SMART-Werte zum richtigen physischen Datenträger und das Verhalten, wenn
/// eine Quelle nichts liefert.
///
/// Bewusst NICHT getestet: die echten WMI-Abfragen selbst (WmiSmartQuery). Sie sind die
/// Hardware-Grenze – ein Test dafür bräuchte bestimmte Laufwerke und Administratorrechte und
/// würde auf jedem Rechner etwas anderes messen. Das OS wird hier über ISmartQuery gefaked.
/// </summary>
public class DriveHealthServiceTests
{
    private sealed class FakeSmartQuery : ISmartQuery
    {
        public List<PhysicalDiskRow> PhysicalDisks { get; } = [];
        public List<DiskNumberMapRow> DiskNumberMap { get; } = [];
        public List<SmartDataRow> SmartData { get; } = [];
        public List<FailurePredictRow> FailurePredict { get; } = [];
        public List<ReliabilityRow> Reliability { get; } = [];

        public IReadOnlyList<PhysicalDiskRow> GetPhysicalDisks() => PhysicalDisks;
        public IReadOnlyList<DiskNumberMapRow> GetDiskNumberMap() => DiskNumberMap;
        public IReadOnlyList<SmartDataRow> GetSmartData() => SmartData;
        public IReadOnlyList<FailurePredictRow> GetFailurePredictStatus() => FailurePredict;
        public IReadOnlyList<ReliabilityRow> GetReliabilityCounters() => Reliability;
    }

    private static byte[] BuildBlob(params (byte Id, byte Normalized, long Raw)[] attributes)
    {
        var blob = new byte[362];
        blob[0] = 0x10;

        for (int i = 0; i < attributes.Length; i++)
        {
            int offset = 2 + i * 12;
            (byte id, byte normalized, long raw) = attributes[i];
            blob[offset] = id;
            blob[offset + 3] = normalized;
            blob[offset + 4] = normalized;
            for (int b = 0; b < 6; b++)
                blob[offset + 5 + b] = (byte)((raw >> (8 * b)) & 0xFF);
        }

        return blob;
    }

    private const string PnpDisk0 = @"SCSI\DISK&VEN_A\4&AAAA&0&000000";
    private const string PnpDisk1 = @"SCSI\DISK&VEN_B\4&BBBB&0&000000";

    /// <summary>Ein Datenträger, zu dem Windows selbst nichts meldet – der ATA-Normalfall.</summary>
    private static PhysicalDiskRow Disk(
        uint number, string model, DriveMediaType mediaType, long capacity, string[] letters,
        ReportedHealthStatus reported = ReportedHealthStatus.Unknown,
        DriveBusType busType = DriveBusType.Unknown)
        => new(number, model, mediaType, capacity, letters, reported, busType);

    private static FakeSmartQuery TwoDisks()
    {
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "Disk Null", DriveMediaType.Ssd, 1_000_000, ["C:"]));
        fake.PhysicalDisks.Add(Disk(1, "Disk Eins", DriveMediaType.Hdd, 2_000_000, ["D:"]));
        fake.DiskNumberMap.Add(new DiskNumberMapRow(0, PnpDisk0));
        fake.DiskNumberMap.Add(new DiskNumberMapRow(1, PnpDisk1));
        return fake;
    }

    [Fact]
    public void GetDriveHealth_OhneJedeQuelle_LiefertLeereListe()
    {
        var service = new DriveHealthService(new FakeSmartQuery());

        Assert.Empty(service.GetDriveHealth());
    }

    [Fact]
    public void GetDriveHealth_DatentraegerOhneSmart_LiefertUnbekanntStattAusnahme()
    {
        // Der USB-Gehäuse-/VM-Fall: Basisdaten ja, SMART nein.
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "USB Stick", DriveMediaType.Unknown, 8_000_000, ["E:"]));

        DriveHealthInfo info = service(fake).GetDriveHealth().Single();

        Assert.Equal(SmartHealthLevel.Unknown, info.Health);
        Assert.Null(info.TemperatureCelsius);
        Assert.Equal("USB Stick", info.Model);
        Assert.Equal(["E:"], info.Letters);
    }

    [Fact]
    public void GetDriveHealth_SmartWerte_LandenAmRichtigenDatentraeger()
    {
        // DER kritische Test: Disk 1 ist auffällig, Disk 0 ist gesund. Eine vertauschte
        // Zuordnung würde falschen Alarm bzw. falsche Entwarnung erzeugen.
        FakeSmartQuery fake = TwoDisks();
        fake.SmartData.Add(new SmartDataRow(PnpDisk0,
            BuildBlob((SmartAttributeParser.IdTemperature, 100, 35),
                      (SmartAttributeParser.IdReallocated, 100, 0))));
        fake.SmartData.Add(new SmartDataRow(PnpDisk1,
            BuildBlob((SmartAttributeParser.IdReallocated, 50, SmartThresholds.ReallocatedCritical))));

        IReadOnlyList<DriveHealthInfo> result = service(fake).GetDriveHealth();

        DriveHealthInfo disk0 = result.Single(d => d.Model == "Disk Null");
        DriveHealthInfo disk1 = result.Single(d => d.Model == "Disk Eins");

        Assert.Equal(SmartHealthLevel.Ok, disk0.Health);
        Assert.Equal(35, disk0.TemperatureCelsius);
        Assert.Equal(SmartHealthLevel.Failing, disk1.Health);
        Assert.Equal(SmartThresholds.ReallocatedCritical, disk1.ReallocatedSectors);
    }

    [Fact]
    public void GetDriveHealth_FailureFlag_LandetAmRichtigenDatentraeger()
    {
        FakeSmartQuery fake = TwoDisks();
        fake.SmartData.Add(new SmartDataRow(PnpDisk0,
            BuildBlob((SmartAttributeParser.IdTemperature, 100, 30), (SmartAttributeParser.IdReallocated, 100, 0))));
        fake.SmartData.Add(new SmartDataRow(PnpDisk1,
            BuildBlob((SmartAttributeParser.IdTemperature, 100, 30), (SmartAttributeParser.IdReallocated, 100, 0))));
        fake.FailurePredict.Add(new FailurePredictRow(PnpDisk1, true));

        IReadOnlyList<DriveHealthInfo> result = service(fake).GetDriveHealth();

        Assert.Equal(SmartHealthLevel.Ok, result.Single(d => d.Model == "Disk Null").Health);
        Assert.Equal(SmartHealthLevel.Failing, result.Single(d => d.Model == "Disk Eins").Health);
        Assert.True(result.Single(d => d.Model == "Disk Eins").PredictFailure);
    }

    [Fact]
    public void GetDriveHealth_OhneAtaSmart_NutztDenZuverlaessigkeitsZaehler()
    {
        // Der NVMe-Fall: MSStorageDriver_* liefert nichts, der Fallback springt ein.
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "NVMe", DriveMediaType.Ssd, 500_000, ["C:"]));
        fake.Reliability.Add(new ReliabilityRow(0, TemperatureCelsius: 44, PowerOnHours: 900,
            WearUsedPercent: 12));

        DriveHealthInfo info = service(fake).GetDriveHealth().Single();

        Assert.Equal(44, info.TemperatureCelsius);
        Assert.Equal(88, info.WearRemainingPercent);
        Assert.Equal(SmartHealthLevel.Ok, info.Health);
    }

    [Fact]
    public void GetDriveHealth_NvmeMitVerbrauchterLebensdauer_LiefertRot()
    {
        // Der komplette NVMe-Rot-Pfad: kein ATA-Blob, kein Failure-Flag, keine Sektorzähler –
        // nur der Zuverlässigkeitszähler. Genau der Fall, für den der Fallback existiert.
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "NVMe", DriveMediaType.Ssd, 500_000, ["C:"]));
        fake.Reliability.Add(new ReliabilityRow(0, TemperatureCelsius: 40, PowerOnHours: 30_000,
            WearUsedPercent: 100 - SmartThresholds.WearCriticalPercent));

        DriveHealthInfo info = service(fake).GetDriveHealth().Single();

        Assert.Equal(SmartThresholds.WearCriticalPercent, info.WearRemainingPercent);
        Assert.Equal(SmartHealthLevel.Failing, info.Health);
    }

    [Fact]
    public void GetDriveHealth_Zuverlaessigkeitszaehler_RechnetVerbrauchInVerbleibendUm()
    {
        // Der Zähler meldet VERBRAUCHTE Prozent, die App zeigt die VERBLEIBENDEN. Fällt das
        // „100 - “ weg, meldet eine zu 97 % verbrauchte SSD 97 % Restlebensdauer.
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "NVMe", DriveMediaType.Ssd, 500_000, ["C:"]));
        fake.Reliability.Add(new ReliabilityRow(0, TemperatureCelsius: null, PowerOnHours: null,
            WearUsedPercent: 97));

        DriveHealthInfo info = service(fake).GetDriveHealth().Single();

        Assert.Equal(3, info.WearRemainingPercent);
    }

    [Fact]
    public void GetDriveHealth_BetriebsstundenAusDemZaehler_LandenAmDatentraeger()
    {
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "NVMe", DriveMediaType.Ssd, 500_000, ["C:"]));
        fake.Reliability.Add(new ReliabilityRow(0, TemperatureCelsius: 40, PowerOnHours: 4321,
            WearUsedPercent: 5));

        Assert.Equal(4321, service(fake).GetDriveHealth().Single().PowerOnHours);
    }

    [Fact]
    public void GetDriveHealth_EinschaltzyklenAusDemZaehler_LandenAmDatentraeger()
    {
        // USB-Platte ohne SMART/NVMe-Log: StartStopCycleCount ist die einzige Zyklen-Quelle.
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "USB HDD", DriveMediaType.Unknown, 1_000_000, ["D:"],
            busType: DriveBusType.Usb));
        fake.Reliability.Add(new ReliabilityRow(0, TemperatureCelsius: 34, PowerOnHours: 5159,
            WearUsedPercent: 0, PowerCycles: 6155));

        Assert.Equal(6155, service(fake).GetDriveHealth().Single().PowerCycles);
    }

    [Fact]
    public void ResolveDetails_ZaehlerwerteMitZyklen_ZeigtZyklenAberKeinTbw()
    {
        // Der USB-HDD-Fall: Einschaltzyklen kommen aus dem Zähler; TBW bleibt korrekt leer,
        // weil eine mechanische Platte keine geschriebene Gesamtdatenmenge meldet.
        var info = new DriveHealthInfo
        {
            Model = "USB HDD", MediaType = DriveMediaType.Unknown, Health = SmartHealthLevel.Ok,
            TemperatureCelsius = 34, PowerOnHours = 5159, PowerCycles = 6155, RawSmartBlob = null
        };

        SmartDetails? d = DriveHealthViewModel.ResolveDetails(info, provider: null);

        Assert.NotNull(d);
        Assert.Equal(6155, d!.PowerCycles);
        Assert.Null(d.TotalBytesWritten);
    }

    [Fact]
    public void GetDriveHealth_AtaSmartVorhanden_SchlaegtDenFallback()
    {
        // Beide Quellen liefern – die genauere (rohe Attribute) muss gewinnen.
        FakeSmartQuery fake = TwoDisks();
        fake.SmartData.Add(new SmartDataRow(PnpDisk0, BuildBlob((SmartAttributeParser.IdTemperature, 100, 33))));
        fake.Reliability.Add(new ReliabilityRow(0, TemperatureCelsius: 99, PowerOnHours: 5, WearUsedPercent: 90));

        DriveHealthInfo info = service(fake).GetDriveHealth().Single(d => d.Model == "Disk Null");

        Assert.Equal(33, info.TemperatureCelsius);
    }

    [Fact]
    public void GetDriveHealth_UnbekanntePnpId_WirdIgnoriertOhneAndereZuStoeren()
    {
        FakeSmartQuery fake = TwoDisks();
        fake.SmartData.Add(new SmartDataRow(@"SCSI\DISK&VEN_GEIST\0&0&0",
            BuildBlob((SmartAttributeParser.IdReallocated, 1, 999))));
        fake.SmartData.Add(new SmartDataRow(PnpDisk0,
            BuildBlob((SmartAttributeParser.IdTemperature, 100, 30), (SmartAttributeParser.IdReallocated, 100, 0))));

        IReadOnlyList<DriveHealthInfo> result = service(fake).GetDriveHealth();

        Assert.Equal(2, result.Count);
        Assert.Equal(SmartHealthLevel.Ok, result.Single(d => d.Model == "Disk Null").Health);
        Assert.Equal(SmartHealthLevel.Unknown, result.Single(d => d.Model == "Disk Eins").Health);
    }

    private static DriveHealthService service(FakeSmartQuery fake) => new(fake);

    [Fact]
    public void ResolveDetails_OhneModul_LiefertNullUndLaesstDieAmpelUnberuehrt()
    {
        // Der Bestandskunden-Fall: Lizenz da, aber ein älteres Modul ohne diese Fähigkeit.
        var info = new DriveHealthInfo
        {
            Model = "Disk", Health = SmartHealthLevel.Warning, RawSmartBlob = BuildBlob((0x05, 100, 3))
        };

        SmartDetails? details = DriveHealthViewModel.ResolveDetails(info, provider: null);

        Assert.Null(details);
        Assert.Equal(SmartHealthLevel.Warning, info.Health);
    }

    [Fact]
    public void ResolveDetails_OhneBlob_FragtDasModulErstGarNicht()
    {
        var provider = new ThrowingDetailProvider();
        var info = new DriveHealthInfo { Model = "USB", Health = SmartHealthLevel.Unknown, RawSmartBlob = null };

        Assert.Null(DriveHealthViewModel.ResolveDetails(info, provider));
        Assert.False(provider.WasCalled);
    }

    [Fact]
    public void ResolveDetails_MitModulUndBlob_ReichtDenBlobDurch()
    {
        var provider = new StubDetailProvider(new SmartDetails(40, 90, 1234, 56, 0, 0, 7_000_000));
        byte[] blob = BuildBlob((SmartAttributeParser.IdPowerOnHours, 100, 1234));
        var info = new DriveHealthInfo
        {
            Model = "SSD", MediaType = DriveMediaType.Ssd, Health = SmartHealthLevel.Ok,
            RawSmartBlob = blob
        };

        SmartDetails? details = DriveHealthViewModel.ResolveDetails(info, provider);

        Assert.Equal(1234, details!.PowerOnHours);
        Assert.Equal("SSD", provider.LastModel);
        Assert.Equal(DriveMediaType.Ssd, provider.LastMediaType);
        // Ohne diese Zusicherung bliebe der Test grün, wenn der Blob des FALSCHEN Laufwerks
        // durchgereicht würde – die Details gehörten dann zu einer anderen Platte.
        Assert.Same(blob, provider.LastBlob);
    }

    [Fact]
    public void ResolveDetails_OhneBlobAberMitZaehlerwerten_LiefertDieBekanntenWerte()
    {
        // Der NVMe-Fall: Es gibt keinen Blob zu dekodieren, das Modul wird gar nicht gefragt.
        // Die Werte liegen aber bereits vor – sie zu verschweigen hieße, dem Pro-Nutzer auf
        // seinem einzigen Laufwerk drei Striche zu zeigen.
        var info = new DriveHealthInfo
        {
            Model = "NVMe", MediaType = DriveMediaType.Ssd, Health = SmartHealthLevel.Ok,
            TemperatureCelsius = 44, WearRemainingPercent = 88, PowerOnHours = 900,
            RawSmartBlob = null
        };

        SmartDetails? details = DriveHealthViewModel.ResolveDetails(info, provider: null);

        Assert.NotNull(details);
        Assert.Equal(44, details!.TemperatureCelsius);
        Assert.Equal(88, details.WearRemainingPercent);
        Assert.Equal(900, details.PowerOnHours);
        // Was ohne Blob niemand wissen kann, bleibt leer – statt eine 0 zu behaupten.
        Assert.Null(details.PowerCycles);
        Assert.Null(details.TotalBytesWritten);
    }

    // ---- NVMe-Passthrough (Reader-Verdrahtung + NVMe-Detail-Interface) ----

    private sealed class FakeNvmeReader : INvmeHealthReader
    {
        private readonly uint _forDisk;
        private readonly byte[] _log;
        public int Calls { get; private set; }

        public FakeNvmeReader(uint forDisk, byte[] log) { _forDisk = forDisk; _log = log; }

        public byte[]? ReadHealthLog(uint diskNumber)
        {
            Calls++;
            return diskNumber == _forDisk ? _log : null;
        }
    }

    [Fact]
    public void GetDriveHealth_NvmeLaufwerk_HaengtDenHealthLogAn()
    {
        var log = new byte[512];
        log[0] = 0xAB;
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(2, "NVMe", DriveMediaType.Ssd, 1_000_000, ["C:"],
            busType: DriveBusType.Nvme));
        var reader = new FakeNvmeReader(2, log);

        DriveHealthInfo info = new DriveHealthService(fake, reader).GetDriveHealth().Single();

        Assert.Same(log, info.RawNvmeBlob);
    }

    [Fact]
    public void GetDriveHealth_NichtNvme_FragtDenReaderNicht()
    {
        var fake = new FakeSmartQuery();
        fake.PhysicalDisks.Add(Disk(0, "SATA SSD", DriveMediaType.Ssd, 1_000_000, ["C:"],
            busType: DriveBusType.Sata));
        var reader = new FakeNvmeReader(0, new byte[512]);

        DriveHealthInfo info = new DriveHealthService(fake, reader).GetDriveHealth().Single();

        Assert.Null(info.RawNvmeBlob);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public void ResolveDetails_NvmeBlobUndModul_DekodiertUeberDasNvmeInterface()
    {
        var details = new SmartDetails(48, 94, 1618, 664, null, null, 65_000_000_000_000);
        var nvme = new StubNvmeProvider(details);
        var log = new byte[512];
        var info = new DriveHealthInfo
        {
            Model = "Samsung", MediaType = DriveMediaType.Ssd, Health = SmartHealthLevel.Ok,
            RawNvmeBlob = log
        };

        SmartDetails? result = DriveHealthViewModel.ResolveDetails(info, provider: null, nvmeProvider: nvme);

        Assert.Equal(664, result!.PowerCycles);
        Assert.Equal(65_000_000_000_000, result.TotalBytesWritten);
        Assert.Same(log, nvme.LastLog);
        Assert.Equal("Samsung", nvme.LastModel);
    }

    [Fact]
    public void ResolveDetails_NvmeBlobOhneModul_FaelltAufZaehlerwerteZurueck()
    {
        // Älteres Modul ohne NVMe-Fähigkeit: statt „nichts" die frei bekannten Werte zeigen.
        var info = new DriveHealthInfo
        {
            Model = "NVMe", MediaType = DriveMediaType.Ssd, Health = SmartHealthLevel.Ok,
            TemperatureCelsius = 48, WearRemainingPercent = 94, RawNvmeBlob = new byte[512]
        };

        SmartDetails? result = DriveHealthViewModel.ResolveDetails(info, provider: null, nvmeProvider: null);

        Assert.NotNull(result);
        Assert.Equal(48, result!.TemperatureCelsius);
        Assert.Null(result.PowerCycles); // ohne Modul kein exklusiver Wert
    }

    [Fact]
    public void ResolveDetails_NvmeHatVorrangVorAta()
    {
        // Liegt (theoretisch) beides vor, entscheidet der NVMe-Blob; das ATA-Modul wird nicht gefragt.
        var nvme = new StubNvmeProvider(new SmartDetails(50, 90, 10, 20, null, null, 1));
        var ata = new ThrowingDetailProvider();
        var info = new DriveHealthInfo
        {
            Model = "X", MediaType = DriveMediaType.Ssd, Health = SmartHealthLevel.Ok,
            RawNvmeBlob = new byte[512], RawSmartBlob = new byte[362]
        };

        SmartDetails? result = DriveHealthViewModel.ResolveDetails(info, ata, nvme);

        Assert.Equal(20, result!.PowerCycles);
        Assert.False(ata.WasCalled);
    }

    private sealed class StubNvmeProvider : INvmeDetailProvider
    {
        private readonly SmartDetails? _details;
        public byte[]? LastLog { get; private set; }
        public string? LastModel { get; private set; }

        public StubNvmeProvider(SmartDetails? details) => _details = details;

        public SmartDetails? GetNvmeDetails(byte[] nvmeHealthLog, string model)
        {
            LastLog = nvmeHealthLog;
            LastModel = model;
            return _details;
        }
    }

    private sealed class StubDetailProvider : ISmartDetailProvider
    {
        private readonly SmartDetails _details;
        public string? LastModel { get; private set; }
        public DriveMediaType LastMediaType { get; private set; }
        public byte[]? LastBlob { get; private set; }

        public StubDetailProvider(SmartDetails details) => _details = details;

        public SmartDetails? GetDetails(byte[] rawSmartBlob, string model, DriveMediaType mediaType)
        {
            LastModel = model;
            LastMediaType = mediaType;
            LastBlob = rawSmartBlob;
            return _details;
        }
    }

    private sealed class ThrowingDetailProvider : ISmartDetailProvider
    {
        public bool WasCalled { get; private set; }

        public SmartDetails? GetDetails(byte[] rawSmartBlob, string model, DriveMediaType mediaType)
        {
            WasCalled = true;
            throw new InvalidOperationException("darf nicht aufgerufen werden");
        }
    }
}
