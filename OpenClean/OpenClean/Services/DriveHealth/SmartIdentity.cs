namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Brücke zwischen den WMI-Welten. <c>MSStorageDriver_*</c> (root\wmi) identifiziert Laufwerke
/// über <c>InstanceName</c>, <c>MSFT_PhysicalDisk</c> (root\Microsoft\Windows\Storage) über die
/// Disk-Nummer. Verbindendes Glied ist <c>Win32_DiskDrive</c>: dessen <c>PNPDeviceID</c>
/// entspricht dem <c>InstanceName</c> ohne das angehängte <c>_0</c>, und dessen <c>Index</c>
/// ist die Disk-Nummer (= <c>MSFT_PhysicalDisk.DeviceId</c>).
///
/// <para><b>Warum eigene Klasse:</b> Ohne korrekte Zuordnung erscheinen die Messwerte am
/// falschen Datenträger – falscher Alarm oder, schlimmer, falsche Entwarnung. Deshalb reine
/// Funktion mit eigenen Tests statt eingebettet im WMI-Code.</para>
/// </summary>
public static class SmartIdentity
{
    private const string InstanceSuffix = "_0";

    /// <summary>
    /// Wandelt einen <c>InstanceName</c> in die vergleichbare Form der <c>PNPDeviceID</c>.
    /// Liefert <c>null</c>, wenn nichts Vergleichbares übrig bleibt.
    /// </summary>
    public static string? ToPnpDeviceId(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;

        string trimmed = instanceName.Trim();
        if (trimmed.EndsWith(InstanceSuffix, StringComparison.Ordinal))
            trimmed = trimmed[..^InstanceSuffix.Length];

        return Normalize(trimmed);
    }

    /// <summary>
    /// Bringt eine <c>PNPDeviceID</c> auf die Vergleichsform. Windows liefert dieselbe ID je
    /// nach Quelle unterschiedlich geschrieben – ohne Normalisierung schlägt der Abgleich fehl.
    /// </summary>
    public static string? Normalize(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return null;
        return pnpDeviceId.Trim().ToUpperInvariant();
    }
}
