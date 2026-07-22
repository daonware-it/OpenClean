namespace OpenClean.Services.DriveHealth;

/// <summary>Anbindungsart des Datenträgers (aus <c>MSFT_PhysicalDisk.BusType</c>). Entscheidet,
/// ob der NVMe-Health-Log-Passthrough (<see cref="INvmeHealthReader"/>) versucht wird.</summary>
public enum DriveBusType
{
    /// <summary>NVMe – Health-Log 0x02 via DeviceIoControl auslesbar.</summary>
    Nvme,
    /// <summary>SATA/ATA – SMART über die <c>MSStorageDriver_*</c>-Klassen.</summary>
    Sata,
    /// <summary>USB-Gehäuse – reicht NVMe-/ATA-Passthrough i. d. R. nicht durch.</summary>
    Usb,
    /// <summary>Sonstige bekannte Anbindung (RAID, SAS, virtuell …).</summary>
    Other,
    /// <summary>Nicht ermittelbar oder Lesefehler.</summary>
    Unknown
}

/// <summary>Ein physischer Datenträger aus <c>MSFT_PhysicalDisk</c> inkl. Buchstaben-Zuordnung.</summary>
/// <param name="ReportedHealth">
/// Der von Windows gemeldete Zustand (<c>HealthStatus</c>). Bei NVMe das einzige Ausfallsignal,
/// das die Fallback-Kette erreicht – die ATA-gebundenen Quellen liefern dort nichts.
/// </param>
/// <param name="BusType">Anbindungsart; steuert den NVMe-Passthrough in <c>DriveHealthService</c>.</param>
public sealed record PhysicalDiskRow(
    uint DiskNumber,
    string Model,
    DriveMediaType MediaType,
    long CapacityBytes,
    IReadOnlyList<string> Letters,
    ReportedHealthStatus ReportedHealth,
    DriveBusType BusType);

/// <summary>Roher SMART-Blob aus <c>MSStorageDriver_ATAPISmartData</c>.</summary>
public sealed record SmartDataRow(string PnpDeviceId, byte[] VendorSpecific);

/// <summary>Das SMART-Failure-Flag aus <c>MSStorageDriver_FailurePredictStatus</c>.</summary>
public sealed record FailurePredictRow(string PnpDeviceId, bool PredictFailure);

/// <summary>Fallback-Werte aus <c>MSFT_StorageReliabilityCounter</c> (NVMe, kein ATA-SMART).</summary>
/// <param name="WearUsedPercent">
/// Die VERBRAUCHTEN Prozent der Lebensdauer, genau so, wie WMI sie in <c>Wear</c> meldet –
/// unverändert und ungeclamped. Die Umrechnung auf die verbleibenden Prozent, die die UI
/// zeigt, geschieht bewusst erst in <c>DriveHealthService.Compose</c>: dort ist sie getestet.
/// </param>
/// <param name="PowerCycles">
/// Ein-/Ausschaltzyklen aus <c>StartStopCycleCount</c> – die einzige Zyklen-Quelle, die der
/// Zuverlässigkeitszähler kennt. Für Laufwerke ohne ATA-SMART/NVMe-Log (z. B. USB-Platten) die
/// einzige Möglichkeit, Einschaltzyklen zu zeigen. <c>null</c>, wenn nicht gemeldet.
/// </param>
public sealed record ReliabilityRow(
    uint DiskNumber,
    int? TemperatureCelsius,
    long? PowerOnHours,
    int? WearUsedPercent,
    long? PowerCycles = null);

/// <summary>Zuordnung Disk-Nummer ↔ PNPDeviceID aus <c>Win32_DiskDrive</c>.</summary>
public sealed record DiskNumberMapRow(uint DiskNumber, string PnpDeviceId);

/// <summary>
/// Zugriff auf die WMI-Rohdaten. Existiert als Interface, damit die gesamte Auswerte- und
/// Zuordnungslogik ohne echtes WMI, ohne Administratorrechte und ohne bestimmte Hardware
/// testbar ist (dieselbe Disziplin wie <c>IProcessRunner</c> in der Tiefenreinigung).
///
/// <para><b>Vertrag jeder Methode:</b> blockierend, nie eine Ausnahme nach außen, im Zweifel
/// eine leere Liste. Ein Laufwerk, das SMART nicht durchreicht (USB-Gehäuse, VM), ist ein
/// Anzeigefall – kein Fehlerfall.</para>
/// </summary>
public interface ISmartQuery
{
    IReadOnlyList<PhysicalDiskRow> GetPhysicalDisks();
    IReadOnlyList<DiskNumberMapRow> GetDiskNumberMap();
    IReadOnlyList<SmartDataRow> GetSmartData();
    IReadOnlyList<FailurePredictRow> GetFailurePredictStatus();
    IReadOnlyList<ReliabilityRow> GetReliabilityCounters();
}
