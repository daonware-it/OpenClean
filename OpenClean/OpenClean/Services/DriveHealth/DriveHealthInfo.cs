namespace OpenClean.Services.DriveHealth;

/// <summary>Art des Speichermediums, wie von MSFT_PhysicalDisk.MediaType gemeldet.</summary>
public enum DriveMediaType
{
    /// <summary>Halbleiterlaufwerk (SSD/NVMe).</summary>
    Ssd,
    /// <summary>Magnetplatte.</summary>
    Hdd,
    /// <summary>Nicht ermittelbar (z. B. USB-Gehäuse, virtueller Datenträger).</summary>
    Unknown
}

/// <summary>
/// Der von Windows selbst gemeldete Zustand aus <c>MSFT_PhysicalDisk.HealthStatus</c>.
///
/// <para>Wichtig für NVMe: Failure-Flag und Sektorzähler stammen aus ATA-gebundenen
/// WMI-Klassen und liefern dort per Konstruktion nichts. Dieser Wert ist damit das
/// einzige Ausfallsignal, das die Fallback-Kette bei NVMe überhaupt erreichen kann –
/// Windows speist ihn aus den NVMe-Critical-Warning-Bits.</para>
/// </summary>
public enum ReportedHealthStatus
{
    /// <summary>Windows meldet den Datenträger als gesund (HealthStatus 0).</summary>
    Healthy,
    /// <summary>Windows meldet eine Auffälligkeit (HealthStatus 1).</summary>
    Warning,
    /// <summary>Windows meldet den Datenträger als defekt (HealthStatus 2).</summary>
    Unhealthy,
    /// <summary>Nicht gemeldet, unerwarteter Wert oder Lesefehler.</summary>
    Unknown
}

/// <summary>Ampelstufe der Datenträger-Gesundheit.</summary>
public enum SmartHealthLevel
{
    /// <summary>Gesund – keine auffälligen Attribute.</summary>
    Ok,
    /// <summary>Beobachten – einzelne Werte grenzwertig.</summary>
    Warning,
    /// <summary>Ausfall droht – Backup machen.</summary>
    Failing,
    /// <summary>Keine Quelle lieferte verwertbare Daten.</summary>
    Unknown
}

/// <summary>
/// Gesundheitszustand EINES physischen Datenträgers.
///
/// <para><b>Nullable heißt „nicht geliefert“</b>, nicht „null gemessen“. Ein USB-Gehäuse oder
/// ein virtueller Datenträger reicht SMART nicht durch – solche Felder bleiben leer und die
/// UI blendet sie still aus, statt eine 0 zu behaupten.</para>
///
/// <para>Die hier enthaltenen Werte sind die, die der offene Kern für die AMPEL ohnehin
/// dekodieren muss. Die vollständige Detailauswertung (Betriebsstunden, Einschaltzyklen,
/// TBW) liefert das Premium-Modul über <see cref="ISmartDetailProvider"/>.</para>
/// </summary>
public sealed class DriveHealthInfo
{
    /// <summary>Modellbezeichnung, z. B. „Samsung SSD 980 PRO 1TB“.</summary>
    public required string Model { get; init; }

    public DriveMediaType MediaType { get; init; }

    public long CapacityBytes { get; init; }

    public SmartHealthLevel Health { get; init; }

    /// <summary>Zugeordnete Laufwerksbuchstaben inkl. Doppelpunkt, z. B. ["C:", "D:"].</summary>
    public IReadOnlyList<string> Letters { get; init; } = [];

    /// <summary>Das echte SMART-Failure-Flag (MSStorageDriver_FailurePredictStatus).</summary>
    public bool PredictFailure { get; init; }

    public int? TemperatureCelsius { get; init; }

    /// <summary>Verbleibende Lebensdauer in Prozent (100 = neu, 0 = verbraucht).</summary>
    public int? WearRemainingPercent { get; init; }

    public long? ReallocatedSectors { get; init; }

    public long? PendingSectors { get; init; }

    /// <summary>Betriebsstunden, sofern eine Quelle sie geliefert hat.</summary>
    public long? PowerOnHours { get; init; }

    /// <summary>
    /// Ein-/Ausschaltzyklen aus dem Zuverlässigkeitszähler (<c>StartStopCycleCount</c>). Für
    /// Laufwerke ohne ATA-SMART/NVMe-Log (z. B. USB-Platten) die einzige Zyklen-Quelle; sonst
    /// liefert das Premium-Modul den genaueren Wert.
    /// </summary>
    public long? PowerCycles { get; init; }

    /// <summary>
    /// Roher SMART-Blob zur Übergabe an das Premium-Modul. Bewusst <c>internal</c>: außerhalb
    /// der Detailauswertung hat niemand ein Interesse an 512 undekodierten Bytes.
    /// </summary>
    internal byte[]? RawSmartBlob { get; init; }

    /// <summary>
    /// Roher NVMe-Health-Log (512 Byte, Log-Page 0x02) zur Übergabe an das Premium-Modul
    /// (<see cref="Services.DriveHealth.INvmeDetailProvider"/>). Nur bei NVMe gesetzt; bewusst
    /// <c>internal</c> wie <see cref="RawSmartBlob"/>.
    /// </summary>
    internal byte[]? RawNvmeBlob { get; init; }

    // ---- Anzeige-Helfer (Muster: Models/DriveUsage.cs) ----

    public string CapacityDisplay => ByteFormatter.Format(CapacityBytes);

    /// <summary>Buchstaben als „C: · D:“; leer, wenn dem Datenträger keiner zugeordnet ist.</summary>
    public string LettersDisplay => string.Join(" · ", Letters);

    public bool HasLetters => Letters.Count > 0;

    /// <summary>Klartext der Ampel – das EINZIGE, was die freie Fassung zeigt.</summary>
    public string HealthText => Health switch
    {
        SmartHealthLevel.Ok => Loc.T("system.health.ok"),
        SmartHealthLevel.Warning => Loc.T("system.health.warning"),
        SmartHealthLevel.Failing => Loc.T("system.health.failing"),
        _ => Loc.T("system.health.unknown")
    };

    public string MediaTypeText => MediaType switch
    {
        DriveMediaType.Ssd => Loc.T("system.health.ssd"),
        DriveMediaType.Hdd => Loc.T("system.health.hdd"),
        _ => ""
    };

    public bool HasMediaType => MediaType != DriveMediaType.Unknown;
}
