using System.IO;
using System.Runtime.InteropServices;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Momentaufnahme des Arbeitsspeichers – ausschließlich echte Messwerte von
/// GlobalMemoryStatusEx (dwMemoryLoad = tatsächliche RAM-Auslastung in Prozent).
/// </summary>
public sealed class MemoryInfo
{
    public long TotalBytes { get; init; }
    public long AvailableBytes { get; init; }
    public long UsedBytes { get; init; }

    /// <summary>Vom Windows-Kernel gemeldete Auslastung (dwMemoryLoad), 0–100.</summary>
    public int LoadPercent { get; init; }

    public bool IsValid => TotalBytes > 0;
}

/// <summary>
/// Liefert reale Systeminformationen für das Dashboard: RAM-Auslastung (P/Invoke)
/// und Laufwerksbelegung (DriveInfo). Beide Quellen sind robust gekapselt.
/// </summary>
public sealed class SystemInfoService
{
    // ---- RAM (GlobalMemoryStatusEx) ----------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Liest die aktuelle physische RAM-Belegung. Bei einem Fehler wird ein
    /// ungültiges (leeres) Ergebnis zurückgegeben (IsValid == false).
    /// </summary>
    public MemoryInfo GetMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
                return new MemoryInfo();

            long total = ToLong(status.ullTotalPhys);
            long avail = ToLong(status.ullAvailPhys);
            long used = Math.Max(0, total - avail);

            return new MemoryInfo
            {
                TotalBytes = total,
                AvailableBytes = avail,
                UsedBytes = used,
                LoadPercent = (int)Math.Clamp(status.dwMemoryLoad, 0u, 100u)
            };
        }
        catch
        {
            return new MemoryInfo();
        }
    }

    // ---- Laufwerke (DriveInfo) ---------------------------------------------

    /// <summary>
    /// Alle bereiten, festen Laufwerke mit echter Belegung. Jedes Laufwerk ist
    /// einzeln gekapselt, sodass ein unlesbares die übrigen nicht verschluckt.
    /// </summary>
    public IReadOnlyList<DriveUsage> GetFixedDrives()
    {
        var result = new List<DriveUsage>();

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return result; }

        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    continue;

                long total = drive.TotalSize;
                long free = drive.TotalFreeSpace;
                long used = Math.Max(0, total - free);

                string label = "";
                try { label = drive.VolumeLabel ?? ""; } catch { /* ignorieren */ }

                result.Add(new DriveUsage
                {
                    Name = drive.Name,
                    VolumeLabel = label,
                    TotalBytes = total,
                    FreeBytes = free,
                    UsedBytes = used
                });
            }
            catch { /* einzelnes Laufwerk nicht lesbar -> überspringen */ }
        }

        return result;
    }

    private static long ToLong(ulong value)
        => value > long.MaxValue ? long.MaxValue : (long)value;
}
