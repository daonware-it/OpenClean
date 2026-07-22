using System.Runtime.Versioning;

namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Liest den Gesundheitszustand aller physischen Datenträger über WMI.
///
/// <para><b>Fallback-Kette:</b> <c>MSFT_PhysicalDisk</c> liefert immer die Basis (Modell, Typ,
/// Größe, Buchstaben). Die Messwerte kommen bevorzugt aus den rohen ATA-SMART-Attributen
/// (<c>MSStorageDriver_*</c>); liefert die Quelle nichts – typisch für NVMe –, springt
/// <c>MSFT_StorageReliabilityCounter</c> ein. Was keine Quelle liefert, bleibt <c>null</c> und
/// wird als „nicht verfügbar“ angezeigt, nie als 0.</para>
///
/// <para><b>Zuordnung:</b> läuft über <see cref="SmartIdentity"/> (InstanceName ↔ PNPDeviceID ↔
/// Disk-Nummer). Eine falsche Zuordnung wäre der schlimmste Fehler dieses Dienstes: sie
/// erzeugte falschen Alarm oder falsche Entwarnung.</para>
///
/// <para><b>Blockierend</b> – vom Aufrufer per <c>Task.Run</c> außerhalb des UI-Threads nutzen
/// (WMI ist langsam). Wirft nie; im Zweifel eine leere Liste.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveHealthService
{
    /// <summary>Prozessweiter Singleton, wie die übrigen Dienste.</summary>
    public static DriveHealthService Instance { get; } = new();

    private readonly ISmartQuery _query;
    private readonly INvmeHealthReader? _nvme;

    /// <summary>Erzeugt den Dienst gegen das echte WMI und den echten NVMe-Passthrough.</summary>
    public DriveHealthService() : this(new WmiSmartQuery(), new NvmeHealthReader()) { }

    /// <summary>
    /// Test-Konstruktor: erlaubt eine WMI-Attrappe und optional eine NVMe-Reader-Attrappe.
    /// Ohne Reader (<c>null</c>) wird bei NVMe kein Health-Log geholt – das entspricht dem
    /// Verhalten der bestehenden Tests, die den NVMe-Pfad nicht betreffen.
    /// </summary>
    internal DriveHealthService(ISmartQuery query, INvmeHealthReader? nvme = null)
    {
        _query = query;
        _nvme = nvme;
    }

    /// <summary>
    /// Liefert einen Eintrag je physischem Datenträger. Fehler werden PRO Datenträger gefangen –
    /// ein Laufwerk mit defekter Firmware darf die übrigen nicht aus der Liste kippen.
    /// </summary>
    public IReadOnlyList<DriveHealthInfo> GetDriveHealth()
    {
        IReadOnlyList<PhysicalDiskRow> disks = _query.GetPhysicalDisks();
        if (disks.Count == 0) return [];

        Dictionary<uint, string> pnpByDisk = _query.GetDiskNumberMap()
            .GroupBy(m => m.DiskNumber)
            .ToDictionary(g => g.Key, g => g.First().PnpDeviceId);

        Dictionary<string, byte[]> blobByPnp = _query.GetSmartData()
            .GroupBy(s => s.PnpDeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().VendorSpecific, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, bool> failureByPnp = _query.GetFailurePredictStatus()
            .GroupBy(f => f.PnpDeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(f => f.PredictFailure), StringComparer.OrdinalIgnoreCase);

        Dictionary<uint, ReliabilityRow> reliabilityByDisk = _query.GetReliabilityCounters()
            .GroupBy(r => r.DiskNumber)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<DriveHealthInfo>(disks.Count);

        foreach (PhysicalDiskRow disk in disks)
        {
            try
            {
                // NVMe-Health-Log nur bei NVMe holen – andere Anbindungen (SATA, USB-Gehäuse)
                // reichen den Passthrough nicht durch. Ohne Reader (Tests) bleibt es null.
                byte[]? nvmeBlob = disk.BusType == DriveBusType.Nvme
                    ? _nvme?.ReadHealthLog(disk.DiskNumber)
                    : null;

                result.Add(Compose(disk, pnpByDisk, blobByPnp, failureByPnp, reliabilityByDisk, nvmeBlob));
            }
            catch
            {
                // Unerwartete Daten dieses Datenträgers: als „unbekannt“ zeigen, nicht verschweigen.
                result.Add(new DriveHealthInfo
                {
                    Model = disk.Model,
                    MediaType = disk.MediaType,
                    CapacityBytes = disk.CapacityBytes,
                    Letters = disk.Letters,
                    Health = SmartHealthLevel.Unknown
                });
            }
        }

        return result;
    }

    private static DriveHealthInfo Compose(
        PhysicalDiskRow disk,
        Dictionary<uint, string> pnpByDisk,
        Dictionary<string, byte[]> blobByPnp,
        Dictionary<string, bool> failureByPnp,
        Dictionary<uint, ReliabilityRow> reliabilityByDisk,
        byte[]? nvmeBlob)
    {
        byte[]? blob = null;
        bool predictFailure = false;

        if (pnpByDisk.TryGetValue(disk.DiskNumber, out string? pnp))
        {
            blobByPnp.TryGetValue(pnp, out blob);
            failureByPnp.TryGetValue(pnp, out predictFailure);
        }

        SmartAttributeSet attributes = SmartAttributeParser.Parse(blob);
        SmartReadings readings = SmartHealthEvaluator.ReadAmpelValues(attributes, predictFailure);

        long? powerOnHours = null;
        long? powerCycles = null;

        // Fallback nur für das, was ATA-SMART NICHT geliefert hat. Die rohen Attribute sind
        // genauer; der Zuverlässigkeitszähler ist die Notlösung für NVMe.
        if (reliabilityByDisk.TryGetValue(disk.DiskNumber, out ReliabilityRow? reliability))
        {
            // Der Zähler meldet VERBRAUCHTE Prozent, die App zeigt die VERBLEIBENDEN. Die
            // Umkehr steht bewusst hier statt in WmiSmartQuery: Sie ist die einzige Stelle im
            // Feature, an der eine Semantik gedreht wird – sie gehört in die getestete Schicht.
            int? wearRemaining = reliability.WearUsedPercent is int used
                ? Math.Clamp(100 - used, 0, 100)
                : null;

            readings = readings with
            {
                TemperatureCelsius = readings.TemperatureCelsius ?? reliability.TemperatureCelsius,
                WearRemainingPercent = readings.WearRemainingPercent ?? wearRemaining
            };

            powerOnHours = reliability.PowerOnHours;
            powerCycles = reliability.PowerCycles;
        }

        return new DriveHealthInfo
        {
            Model = disk.Model,
            MediaType = disk.MediaType,
            CapacityBytes = disk.CapacityBytes,
            Letters = disk.Letters,
            Health = SmartHealthEvaluator.Evaluate(readings, disk.ReportedHealth),
            PredictFailure = readings.PredictFailure,
            TemperatureCelsius = readings.TemperatureCelsius,
            WearRemainingPercent = readings.WearRemainingPercent,
            ReallocatedSectors = readings.ReallocatedSectors,
            PendingSectors = readings.PendingSectors,
            PowerOnHours = powerOnHours,
            PowerCycles = powerCycles,
            RawSmartBlob = blob,
            RawNvmeBlob = nvmeBlob
        };
    }
}
