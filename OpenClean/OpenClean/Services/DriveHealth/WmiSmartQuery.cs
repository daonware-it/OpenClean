using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.Versioning;
using OpenClean.Services;

namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Die echte WMI-Abfrage. Drei Namensräume, weil keiner allein reicht:
/// <list type="bullet">
/// <item><c>root\Microsoft\Windows\Storage</c> – Modell, Typ, Größe, Buchstaben (auch NVMe).</item>
/// <item><c>root\wmi</c> – die rohen SMART-Attribute und das Failure-Flag (nur ATA/SATA).</item>
/// <item><c>root\cimv2</c> – <c>Win32_DiskDrive</c> als Brücke zwischen beiden Welten.</item>
/// </list>
///
/// <para><b>Zeitlimit:</b> Jede Abfrage läuft auf einem eigenen Thread mit hartem
/// <c>Join</c>-Limit. Ein WMI-Aufruf kann bei hängender Storage-Firmware blockieren – ohne
/// Limit würde der Hintergrund-Task nie zurückkehren (dasselbe Muster wie
/// <c>RestorePointService.TryCreate</c>, das denselben Fall bei belegtem VSS abfängt).</para>
///
/// <para><b>Fehler:</b> werden geschluckt und als leere Liste gemeldet. Kein Laufwerk darf ein
/// anderes mitreißen.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiSmartQuery : ISmartQuery
{
    private const int QueryTimeoutMs = 20_000;

    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";
    private const string WmiScope = @"\\.\root\wmi";
    private const string CimScope = @"\\.\root\cimv2";

    // MSFT_PhysicalDisk.MediaType: 3 = HDD, 4 = SSD. 0/5 = unbekannt/SCM.
    private const ushort MediaTypeHdd = 3;
    private const ushort MediaTypeSsd = 4;

    // MSFT_PhysicalDisk.BusType: 7 = USB, 11 = SATA, 17 = NVMe (die für uns relevanten Fälle).
    private const ushort BusTypeUsb = 7;
    private const ushort BusTypeSata = 11;
    private const ushort BusTypeNvme = 17;

    // MSFT_PhysicalDisk.HealthStatus: 0 = Healthy, 1 = Warning, 2 = Unhealthy.
    private const ushort HealthStatusHealthy = 0;
    private const ushort HealthStatusWarning = 1;
    private const ushort HealthStatusUnhealthy = 2;

    public IReadOnlyList<PhysicalDiskRow> GetPhysicalDisks()
        => RunGuarded("MSFT_PhysicalDisk", QueryPhysicalDisks);

    public IReadOnlyList<DiskNumberMapRow> GetDiskNumberMap()
        => RunGuarded("Win32_DiskDrive", QueryDiskNumberMap);

    public IReadOnlyList<SmartDataRow> GetSmartData()
        => RunGuarded("MSStorageDriver_ATAPISmartData", QuerySmartData);

    public IReadOnlyList<FailurePredictRow> GetFailurePredictStatus()
        => RunGuarded("MSStorageDriver_FailurePredictStatus", QueryFailurePredict);

    public IReadOnlyList<ReliabilityRow> GetReliabilityCounters()
        => RunGuarded("MSFT_StorageReliabilityCounter", QueryReliability);

    /// <summary>
    /// Führt eine Abfrage auf eigenem Thread mit Zeitlimit aus. Läuft sie in die Zeit oder wirft
    /// sie, ist das Ergebnis eine leere Liste – der Aufrufer bekommt nie eine Ausnahme.
    /// </summary>
    private static IReadOnlyList<T> RunGuarded<T>(string source, Func<IReadOnlyList<T>> query)
    {
        IReadOnlyList<T> result = [];
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try { result = query(); }
            catch (Exception ex) { error = ex; result = []; }   // Nicht unterstützt / keine Rechte / defekte Firmware.
        })
        {
            IsBackground = true,
            Name = "OpenCleanSmartQuery"
        };
        // MTA reicht für WMI; kein STA nötig.
        thread.Start();

        if (!thread.Join(QueryTimeoutMs))
        {
            LogDiag(source, new TimeoutException($"WMI-Abfrage überschritt das Zeitlimit von {QueryTimeoutMs} ms."));
            return [];
        }

        // Der Join ist eine Speicherbarriere: das im Thread gesetzte `error` ist hier sichtbar.
        if (error is not null) LogDiag(source, error);
        return result;
    }

    /// <summary>
    /// Hängt eine Diagnosezeile an <c>smart-diagnostics.log</c> im Datenverzeichnis an (Muster:
    /// <see cref="UpdateLogService"/>). Zuvor wurden WMI-Fehler vollständig verschluckt – dadurch
    /// blieb etwa der <c>GetRelated</c>-Fehler bei reinen NVMe-Systemen unsichtbar und die
    /// Detailansicht meldete pauschal „keine Detailwerte“. Selbst gekapselt: Protokollieren darf
    /// die Abfrage nie stören.
    /// </summary>
    private static void LogDiag(string source, Exception ex)
    {
        try
        {
            string path = AppPaths.GetDataFile("smart-diagnostics.log");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.AppendAllText(path,
                $"{stamp}  [{source}]  {ex.GetType().Name}: {ex.Message.Trim()}{Environment.NewLine}");
        }
        catch
        {
            // Protokoll ist optional (fehlende Rechte/Datenträgerfehler) – hier nichts weiter tun.
        }
    }

    private static IReadOnlyList<PhysicalDiskRow> QueryPhysicalDisks()
    {
        Dictionary<uint, List<string>> letters = QueryLettersByDiskNumber();
        var rows = new List<PhysicalDiskRow>();

        var scope = new ManagementScope(StorageScope);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"));

        foreach (ManagementBaseObject mo in searcher.Get())
        {
            using (mo)
            {
                try
                {
                    uint number = Convert.ToUInt32(mo["DeviceId"], CultureInfo.InvariantCulture);
                    rows.Add(new PhysicalDiskRow(
                        DiskNumber: number,
                        Model: (mo["FriendlyName"] as string ?? "").Trim(),
                        MediaType: Convert.ToUInt16(mo["MediaType"], CultureInfo.InvariantCulture) switch
                        {
                            MediaTypeSsd => DriveMediaType.Ssd,
                            MediaTypeHdd => DriveMediaType.Hdd,
                            _ => DriveMediaType.Unknown
                        },
                        CapacityBytes: Convert.ToInt64(mo["Size"], CultureInfo.InvariantCulture),
                        Letters: letters.TryGetValue(number, out List<string>? l) ? l : [],
                        ReportedHealth: ReadHealthStatus(mo),
                        BusType: ReadBusType(mo)));
                }
                catch
                {
                    // Ein Datenträger mit unerwarteten Werten darf die übrigen nicht mitreißen.
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Liest <c>MSFT_PhysicalDisk.HealthStatus</c>. Ein unerwarteter Wert oder ein Lesefehler
    /// ergibt <see cref="ReportedHealthStatus.Unknown"/> – der Datenträger darf deswegen nicht
    /// aus der Liste kippen, genauso fehlertolerant wie die übrigen Felder hier.
    /// </summary>
    private static ReportedHealthStatus ReadHealthStatus(ManagementBaseObject mo)
    {
        try
        {
            object? raw = mo["HealthStatus"];
            if (raw is null) return ReportedHealthStatus.Unknown;

            return Convert.ToUInt16(raw, CultureInfo.InvariantCulture) switch
            {
                HealthStatusHealthy => ReportedHealthStatus.Healthy,
                HealthStatusWarning => ReportedHealthStatus.Warning,
                HealthStatusUnhealthy => ReportedHealthStatus.Unhealthy,
                _ => ReportedHealthStatus.Unknown
            };
        }
        catch
        {
            return ReportedHealthStatus.Unknown;
        }
    }

    /// <summary>
    /// Liest <c>MSFT_PhysicalDisk.BusType</c>. Steuert, ob der NVMe-Passthrough versucht wird.
    /// Unerwarteter Wert oder Lesefehler ergibt <see cref="DriveBusType.Unknown"/>.
    /// </summary>
    private static DriveBusType ReadBusType(ManagementBaseObject mo)
    {
        try
        {
            object? raw = mo["BusType"];
            if (raw is null) return DriveBusType.Unknown;

            return Convert.ToUInt16(raw, CultureInfo.InvariantCulture) switch
            {
                BusTypeNvme => DriveBusType.Nvme,
                BusTypeSata => DriveBusType.Sata,
                BusTypeUsb => DriveBusType.Usb,
                _ => DriveBusType.Other
            };
        }
        catch
        {
            return DriveBusType.Unknown;
        }
    }

    /// <summary>Buchstaben je Disk-Nummer über MSFT_Partition (Partitionen ohne Buchstaben fallen raus).</summary>
    private static Dictionary<uint, List<string>> QueryLettersByDiskNumber()
    {
        var map = new Dictionary<uint, List<string>>();

        var scope = new ManagementScope(StorageScope);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"));

        foreach (ManagementBaseObject mo in searcher.Get())
        {
            using (mo)
            {
                try
                {
                    // DriveLetter ist ein char; 0 heißt „kein Buchstabe zugewiesen“.
                    object? raw = mo["DriveLetter"];
                    if (raw is null) continue;

                    char letter = Convert.ToChar(raw, CultureInfo.InvariantCulture);
                    if (letter == '\0' || !char.IsLetter(letter)) continue;

                    uint number = Convert.ToUInt32(mo["DiskNumber"], CultureInfo.InvariantCulture);
                    if (!map.TryGetValue(number, out List<string>? list))
                        map[number] = list = [];

                    list.Add($"{char.ToUpperInvariant(letter)}:");
                }
                catch
                {
                }
            }
        }

        return map;
    }

    private static IReadOnlyList<DiskNumberMapRow> QueryDiskNumberMap()
    {
        var rows = new List<DiskNumberMapRow>();

        var scope = new ManagementScope(CimScope);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT Index, PNPDeviceID FROM Win32_DiskDrive"));

        foreach (ManagementBaseObject mo in searcher.Get())
        {
            using (mo)
            {
                try
                {
                    string? pnp = SmartIdentity.Normalize(mo["PNPDeviceID"] as string);
                    if (pnp is null) continue;

                    rows.Add(new DiskNumberMapRow(
                        Convert.ToUInt32(mo["Index"], CultureInfo.InvariantCulture), pnp));
                }
                catch
                {
                }
            }
        }

        return rows;
    }

    private static IReadOnlyList<SmartDataRow> QuerySmartData()
    {
        var rows = new List<SmartDataRow>();

        var scope = new ManagementScope(WmiScope);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData"));

        foreach (ManagementBaseObject mo in searcher.Get())
        {
            using (mo)
            {
                try
                {
                    string? pnp = SmartIdentity.ToPnpDeviceId(mo["InstanceName"] as string);
                    if (pnp is null) continue;
                    if (mo["VendorSpecific"] is not byte[] blob) continue;

                    rows.Add(new SmartDataRow(pnp, blob));
                }
                catch
                {
                }
            }
        }

        return rows;
    }

    private static IReadOnlyList<FailurePredictRow> QueryFailurePredict()
    {
        var rows = new List<FailurePredictRow>();

        var scope = new ManagementScope(WmiScope);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"));

        foreach (ManagementBaseObject mo in searcher.Get())
        {
            using (mo)
            {
                try
                {
                    string? pnp = SmartIdentity.ToPnpDeviceId(mo["InstanceName"] as string);
                    if (pnp is null) continue;

                    rows.Add(new FailurePredictRow(pnp,
                        Convert.ToBoolean(mo["PredictFailure"], CultureInfo.InvariantCulture)));
                }
                catch
                {
                }
            }
        }

        return rows;
    }

    private static IReadOnlyList<ReliabilityRow> QueryReliability()
    {
        var rows = new List<ReliabilityRow>();

        // WICHTIG: Der Scope muss VERBUNDEN sein und die Datenträger per `SELECT *` (vollständiger
        // Objektpfad) geladen werden – sonst wirft die Assoziations-Traversierung `GetRelated`
        // je Datenträger „Operation is not valid due to the current state of the object“
        // (InvalidOperationException). Auf reinen NVMe-Systemen ist der Zuverlässigkeitszähler die
        // EINZIGE Detailquelle (kein ATA-SMART-Blob); der Fehler ließ dort früher alle Detailwerte
        // ausfallen, obwohl Temperatur/Wear vorhanden sind (verifiziert gegen Get-StorageReliabilityCounter).
        var scope = new ManagementScope(StorageScope);
        scope.Connect();

        using var disks = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));

        foreach (ManagementBaseObject disk in disks.Get())
        {
            using (disk)
            {
                try
                {
                    if (disk is not ManagementObject diskObject) continue;

                    uint number = Convert.ToUInt32(disk["DeviceId"], CultureInfo.InvariantCulture);

                    // GetRelated kennt den (verbundenen) Objektpfad bereits – kein WQL-String,
                    // kein Backslash-Escaping wie bei einer selbst gebauten ASSOCIATORS-Abfrage.
                    using ManagementObjectCollection counters = diskObject.GetRelated("MSFT_StorageReliabilityCounter");

                    foreach (ManagementBaseObject counter in counters)
                    {
                        using (counter)
                        {
                            rows.Add(new ReliabilityRow(
                                number,
                                ReadInt(counter, "Temperature"),
                                ReadLong(counter, "PowerOnHours"),
                                // Rohwert: VERBRAUCHTE Prozent, unverändert. Die Umrechnung auf
                                // die verbleibenden macht DriveHealthService.Compose – getestet.
                                ReadInt(counter, "Wear"),
                                // Start/Stop-Zyklen als Einschaltzyklen – bei USB-Platten ohne
                                // ATA-SMART/NVMe-Log die einzige Quelle dafür.
                                ReadLong(counter, "StartStopCycleCount")));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Ein einzelner Datenträger darf die übrigen nicht mitreißen – aber der Grund
                    // wird jetzt protokolliert (früher stiller catch, dadurch war dieser Bug unsichtbar).
                    LogDiag("MSFT_StorageReliabilityCounter", ex);
                }
            }
        }

        return rows;
    }

    private static int? ReadInt(ManagementBaseObject mo, string property)
    {
        try
        {
            object? raw = mo[property];
            return raw is null ? null : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static long? ReadLong(ManagementBaseObject mo, string property)
    {
        try
        {
            object? raw = mo[property];
            return raw is null ? null : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}
