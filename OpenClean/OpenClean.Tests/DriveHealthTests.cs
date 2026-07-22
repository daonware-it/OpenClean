using System;
using OpenClean.Services.DriveHealth;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die reine Logik der Datenträger-Gesundheit ab: Dekodierung des rohen SMART-Blobs,
/// die Ampel-Schwellen und die Identitäts-Brücke zwischen WMI-Klassen.
///
/// Bewusst NICHT getestet: dass WMI auf diesem Rechner tatsächlich SMART-Daten liefert oder
/// dass ein konkretes Laufwerk gesund ist. Das hinge an echter Hardware und
/// Administratorrechten – so etwas gehört nicht in eine Unit-Test-Suite. Der Dienst kapselt
/// die Abfrage hinter ISmartQuery; getestet wird die Auswertung, nicht das Betriebssystem.
/// </summary>
public class DriveHealthTests
{
    /// <summary>
    /// Baut einen SMART-Blob im echten ATA-Format: 2 Byte Version, danach 30 Einträge à 12 Byte
    /// (Id, Flags[2], Normalized, Worst, Raw[6, little endian], Reserved).
    /// </summary>
    private static byte[] BuildBlob(params (byte Id, byte Normalized, long Raw)[] attributes)
    {
        var blob = new byte[362];
        blob[0] = 0x10;
        blob[1] = 0x00;

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

    [Fact]
    public void Parse_BekanntesAttribut_LiefertRohwertUndNormalisiertenWert()
    {
        byte[] blob = BuildBlob((SmartAttributeParser.IdReallocated, 100, 7));

        SmartAttributeSet set = SmartAttributeParser.Parse(blob);
        SmartAttribute? attribute = set.Find(SmartAttributeParser.IdReallocated);

        Assert.NotNull(attribute);
        Assert.Equal(7, attribute!.Raw);
        Assert.Equal(100, attribute.Normalized);
        Assert.False(set.IsEmpty);
    }

    [Fact]
    public void Parse_RohwertUeberVierByte_WirdVollstaendigGelesen()
    {
        // TBW-Zähler laufen über 32 Bit hinaus – ein int-Parser würde hier still überlaufen.
        const long large = 5_000_000_000L;
        byte[] blob = BuildBlob((SmartAttributeParser.IdTotalLbaWritten, 99, large));

        SmartAttribute? attribute = SmartAttributeParser.Parse(blob).Find(SmartAttributeParser.IdTotalLbaWritten);

        Assert.Equal(large, attribute!.Raw);
    }

    [Fact]
    public void Parse_UnbekannteId_WirdUebernommenUndStoertNicht()
    {
        byte[] blob = BuildBlob((0xAB, 50, 3), (SmartAttributeParser.IdTemperature, 70, 42));

        SmartAttributeSet set = SmartAttributeParser.Parse(blob);

        Assert.Equal(42, set.Find(SmartAttributeParser.IdTemperature)!.Raw);
        Assert.Equal(3, set.Find(0xAB)!.Raw);
    }

    [Fact]
    public void Parse_LeererOderZuKurzerBlob_LiefertLeeresSetOhneAusnahme()
    {
        Assert.True(SmartAttributeParser.Parse(null).IsEmpty);
        Assert.True(SmartAttributeParser.Parse([]).IsEmpty);
        Assert.True(SmartAttributeParser.Parse([0x10, 0x00, 0x05]).IsEmpty);
    }

    [Fact]
    public void Parse_IdNull_WirdAlsLeererSlotUebersprungen()
    {
        // Nicht belegte Attribut-Slots sind mit 0 gefüllt; sie sind kein Attribut mit Id 0.
        byte[] blob = BuildBlob((SmartAttributeParser.IdTemperature, 70, 40));

        SmartAttributeSet set = SmartAttributeParser.Parse(blob);

        Assert.Null(set.Find(0x00));
    }

    private static SmartReadings Readings(
        bool predictFailure = false, int? temperature = null, int? wear = null,
        long? reallocated = null, long? pending = null)
        => new(predictFailure, temperature, wear, reallocated, pending);

    /// <summary>Wertet aus, ohne dass Windows selbst etwas meldet – der ATA-Normalfall.</summary>
    private static SmartHealthLevel Evaluate(
        SmartReadings r, ReportedHealthStatus reported = ReportedHealthStatus.Unknown)
        => SmartHealthEvaluator.Evaluate(r, reported);

    [Fact]
    public void Evaluate_KeinerleiWerte_LiefertUnbekannt()
    {
        Assert.Equal(SmartHealthLevel.Unknown, Evaluate(Readings()));
    }

    [Fact]
    public void Evaluate_AlleWerteImNormbereich_LiefertGesund()
    {
        SmartReadings r = Readings(temperature: 38, wear: 95, reallocated: 0, pending: 0);

        Assert.Equal(SmartHealthLevel.Ok, Evaluate(r));
    }

    [Fact]
    public void Evaluate_FailureFlagGesetzt_LiefertRotAuchWennAlleWerteGut()
    {
        // Das Laufwerk sagt selbst, dass es ausfällt – das schlägt jede eigene Schwelle.
        SmartReadings r = Readings(predictFailure: true, temperature: 30, wear: 100, reallocated: 0, pending: 0);

        Assert.Equal(SmartHealthLevel.Failing, Evaluate(r));
    }

    [Fact]
    public void Evaluate_WindowsMeldetDefekt_LiefertRotAuchWennAlleWerteGut()
    {
        // Bei NVMe liefern Failure-Flag und Sektorzähler per Konstruktion nichts. HealthStatus
        // ist dann das EINZIGE Ausfallsignal, das uns erreicht. Wer es ignoriert, entwarnt eine
        // sterbende Platte – der teuerste denkbare Fehler dieses Features.
        SmartReadings r = Readings(temperature: 30, wear: 100, reallocated: 0, pending: 0);

        Assert.Equal(SmartHealthLevel.Failing, Evaluate(r, ReportedHealthStatus.Unhealthy));
    }

    [Fact]
    public void Evaluate_WindowsMeldetAuffaelligkeit_LiefertGelbAuchWennAlleWerteGut()
    {
        SmartReadings r = Readings(temperature: 30, wear: 100, reallocated: 0, pending: 0);

        Assert.Equal(SmartHealthLevel.Warning, Evaluate(r, ReportedHealthStatus.Warning));
    }

    [Fact]
    public void Evaluate_RotSchlaegtGelb()
    {
        // Temperatur wäre Gelb, Reallocated ist Rot. Das schlechtere Verdikt muss gewinnen.
        SmartReadings r = Readings(temperature: 60, reallocated: SmartThresholds.ReallocatedCritical);

        Assert.Equal(SmartHealthLevel.Failing, Evaluate(r));
    }

    [Fact]
    public void Evaluate_ReallocatedAnDenGrenzen_StuftKorrektEin()
    {
        Assert.Equal(SmartHealthLevel.Ok,
            Evaluate(Readings(reallocated: SmartThresholds.ReallocatedWarn - 1)));
        Assert.Equal(SmartHealthLevel.Warning,
            Evaluate(Readings(reallocated: SmartThresholds.ReallocatedWarn)));
        Assert.Equal(SmartHealthLevel.Failing,
            Evaluate(Readings(reallocated: SmartThresholds.ReallocatedCritical)));
    }

    [Fact]
    public void Evaluate_WearAnDenGrenzen_StuftKorrektEin()
    {
        Assert.Equal(SmartHealthLevel.Ok,
            Evaluate(Readings(wear: SmartThresholds.WearWarnPercent + 1)));
        Assert.Equal(SmartHealthLevel.Warning,
            Evaluate(Readings(wear: SmartThresholds.WearWarnPercent)));
        Assert.Equal(SmartHealthLevel.Failing,
            Evaluate(Readings(wear: SmartThresholds.WearCriticalPercent)));
    }

    [Fact]
    public void Evaluate_TemperaturAnDerGrenze_StuftKorrektEin()
    {
        Assert.Equal(SmartHealthLevel.Ok,
            Evaluate(Readings(temperature: SmartThresholds.TemperatureWarnCelsius - 1, reallocated: 0)));
        Assert.Equal(SmartHealthLevel.Warning,
            Evaluate(Readings(temperature: SmartThresholds.TemperatureWarnCelsius)));
    }

    [Fact]
    public void Evaluate_PendingAnDenGrenzen_StuftKorrektEin()
    {
        Assert.Equal(SmartHealthLevel.Ok,
            Evaluate(Readings(pending: SmartThresholds.PendingWarn - 1)));
        Assert.Equal(SmartHealthLevel.Warning,
            Evaluate(Readings(pending: SmartThresholds.PendingWarn)));
    }

    [Fact]
    public void Evaluate_NurEineTemperatur_BelegtKeineEntwarnung()
    {
        // Die Temperatur sagt über die Ausfallnähe nichts aus: Sie ist ein Warngrund, aber kein
        // Entwarngrund. Eine Platte, deren Firmware nur sie führt (USB-Bridges), bekäme sonst
        // „Gesund“ – auch mit tausenden neu zugewiesenen Sektoren, die wir nur nicht sehen.
        Assert.Equal(SmartHealthLevel.Unknown, Evaluate(Readings(temperature: 38)));
    }

    [Fact]
    public void Evaluate_NurEineHeisseTemperatur_WarntTrotzdem()
    {
        // Warnen können wir ohne entwarnen zu können: Von 60 °C WISSEN wir, dass sie zu heiß
        // sind – anders als bei 38 °C, wo wir schlicht nichts wissen.
        Assert.Equal(SmartHealthLevel.Warning, Evaluate(Readings(temperature: 60)));
    }

    [Fact]
    public void ReadAmpelValues_LiestTemperaturAusDemUnterstenRohwertByte()
    {
        // Manche Firmware packt Minimum/Maximum in die oberen Bytes – nur das unterste zählt.
        byte[] blob = BuildBlob((SmartAttributeParser.IdTemperature, 70, 0x0A_0028));

        SmartReadings r = SmartHealthEvaluator.ReadAmpelValues(SmartAttributeParser.Parse(blob), false);

        Assert.Equal(0x28, r.TemperatureCelsius);
    }

    [Fact]
    public void ReadAmpelValues_SsdLifeLeftGewinntGegenWearLeveling()
    {
        byte[] blob = BuildBlob(
            (SmartAttributeParser.IdWearLeveling, 90, 0),
            (SmartAttributeParser.IdSsdLifeLeft, 42, 0));

        SmartReadings r = SmartHealthEvaluator.ReadAmpelValues(SmartAttributeParser.Parse(blob), false);

        Assert.Equal(42, r.WearRemainingPercent);
    }

    [Fact]
    public void ReadAmpelValues_LeeresSet_LiefertNurDasFailureFlag()
    {
        SmartReadings r = SmartHealthEvaluator.ReadAmpelValues(SmartAttributeParser.Parse(null), true);

        Assert.True(r.PredictFailure);
        Assert.Null(r.TemperatureCelsius);
        Assert.Null(r.WearRemainingPercent);
        Assert.Null(r.ReallocatedSectors);
        Assert.Null(r.PendingSectors);
    }

    [Fact]
    public void ToPnpDeviceId_EchterInstanceName_EntferntDasNullSuffix()
    {
        // So sieht ein InstanceName aus MSStorageDriver_ATAPISmartData wirklich aus.
        const string instanceName = @"SCSI\Disk&Ven_&Prod_Samsung_SSD_980\4&2ab2b0d1&0&000000_0";

        string? pnp = SmartIdentity.ToPnpDeviceId(instanceName);

        Assert.Equal(@"SCSI\DISK&VEN_&PROD_SAMSUNG_SSD_980\4&2AB2B0D1&0&000000", pnp);
    }

    [Fact]
    public void ToPnpDeviceId_PasstZurNormalisiertenPnpDeviceIdVonWin32DiskDrive()
    {
        // Der eigentliche Zweck: beide Seiten müssen sich treffen. Win32_DiskDrive liefert die
        // ID in abweichender Groß-/Kleinschreibung – der Vergleich muss trotzdem greifen.
        const string instanceName = @"SCSI\Disk&Ven_NVMe&Prod_WDC\5&35b0c2f&0&000000_0";
        const string pnpFromDiskDrive = @"scsi\disk&ven_nvme&prod_wdc\5&35b0c2f&0&000000";

        Assert.Equal(SmartIdentity.Normalize(pnpFromDiskDrive), SmartIdentity.ToPnpDeviceId(instanceName));
    }

    [Fact]
    public void ToPnpDeviceId_OhneSuffix_BleibtUnveraendert()
    {
        Assert.Equal(@"SCSI\DISK&VEN_X", SmartIdentity.ToPnpDeviceId(@"SCSI\Disk&Ven_X"));
    }

    [Fact]
    public void ToPnpDeviceId_LeerOderNull_LiefertNull()
    {
        Assert.Null(SmartIdentity.ToPnpDeviceId(null));
        Assert.Null(SmartIdentity.ToPnpDeviceId(""));
        Assert.Null(SmartIdentity.ToPnpDeviceId("   "));
        Assert.Null(SmartIdentity.ToPnpDeviceId("_0"));
    }
}
