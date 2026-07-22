namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Liest den rohen NVMe SMART/Health Information Log (Log-Page 0x02, 512 Byte) eines
/// Datenträgers über einen <c>DeviceIoControl</c>-Passthrough.
///
/// <para>Existiert als Interface aus demselben Grund wie <see cref="ISmartQuery"/>: Die
/// Kompositionslogik im <c>DriveHealthService</c> ist damit ohne echte Hardware, ohne bestimmte
/// SSD und ohne den P/Invoke-Aufruf testbar.</para>
///
/// <para><b>Vertrag:</b> blockierend, wirft nie nach außen. Liefert <c>null</c>, wenn der
/// Controller den NVMe-Passthrough nicht durchreicht (USB-Gehäuse, RAID/VMD), keine Rechte
/// bestehen oder kein NVMe-Laufwerk vorliegt – ein Anzeigefall, kein Fehlerfall.</para>
/// </summary>
public interface INvmeHealthReader
{
    /// <summary>
    /// Liefert die 512-Byte-Nutzlast des NVMe-Health-Logs für den Datenträger mit der Nummer
    /// <paramref name="diskNumber"/> (entspricht <c>MSFT_PhysicalDisk.DeviceId</c> bzw.
    /// <c>\\.\PhysicalDrive{n}</c>) oder <c>null</c>, wenn nichts Verwertbares gelesen werden konnte.
    /// </summary>
    byte[]? ReadHealthLog(uint diskNumber);
}
