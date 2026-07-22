using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Liest eine exklusiv gesperrte Datei, ohne sie zu öffnen: Statt der Datei wird das
/// ROHE VOLUME gelesen und die Cluster der Datei werden direkt zusammengesetzt.
///
/// Hintergrund: Chromium hält <c>Cookies</c> mit <c>FileShare.None</c>, solange der Browser
/// läuft – ein normales <c>File.Copy</c> scheitert. Der frühere Weg über eine VSS-Schattenkopie
/// funktionierte zwar, hatte aber die inakzeptable Nebenwirkung, alle Windows-Wiederherstellungs-
/// punkte zu verwerfen. Diese Klasse erzeugt und löscht daher KEINE Schattenkopie.
///
/// Verfahren (empirisch verifiziert):
/// <list type="number">
///   <item>Datei-Handle nur mit <c>FILE_READ_ATTRIBUTES</c> öffnen – das umgeht die Sharing-Sperre.</item>
///   <item><c>FSCTL_GET_RETRIEVAL_POINTERS</c> liefert die Extents (VCN→LCN-Zuordnung) der Datei.</item>
///   <item>Clustergröße über <c>GetDiskFreeSpaceW</c> ermitteln.</item>
///   <item>Rohvolume <c>\\.\X:</c> öffnen, je Extent an <c>LCN * Clustergröße</c> seeken und lesen.</item>
///   <item>Zieldatei am Ende auf die echte Dateigröße kürzen (letzter Cluster ist aufgefüllt).</item>
/// </list>
///
/// Voraussetzung: Adminrechte (Rohzugriff aufs Volume) und ein lokales NTFS-Laufwerk.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RawVolumeReader
{
    private const string LogPrefix = "[OpenClean] RawRead: ";

    // ---- Win32-Konstanten ---------------------------------------------------

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    private const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;

    private const int ERROR_MORE_DATA = 234;

    private const uint FILE_BEGIN = 0;

    /// <summary>Blockgröße beim Lesen vom Volume (1 MiB, Vielfaches jeder Clustergröße).</summary>
    private const int BlockSize = 1024 * 1024;

    // ---- Öffentliche API ----------------------------------------------------

    /// <summary>
    /// Ob der Rohzugriff grundsätzlich möglich ist: Der Prozess muss erhöht laufen
    /// (Adminrechte), sonst verweigert Windows das Öffnen von <c>\\.\X:</c>.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"{LogPrefix}Verfügbarkeitsprüfung fehlgeschlagen: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Kopiert eine (auch exklusiv gesperrte) Datei per Rohzugriff aufs Volume nach
    /// <paramref name="destPath"/>. Liefert <c>true</c> bei Erfolg. Wirft NIE eine Ausnahme;
    /// bei Fehlschlag wird eine bereits teilweise geschriebene Zieldatei wieder entfernt.
    /// </summary>
    public static bool TryCopyRaw(string sourcePath, string destPath)
    {
        bool destCreated = false;
        try
        {
            Trace.WriteLine($"{LogPrefix}Start für '{sourcePath}' -> '{destPath}'.");

            // 1) Pfade validieren und die Volume-Pfade des Quelllaufwerks ableiten.
            if (!TryResolveVolumePaths(sourcePath, destPath,
                    out string fullSource, out string rootPath, out string volumePath))
                return false; // Fehler wurde bereits protokolliert.

            // 2) Datei-Handle NUR mit FILE_READ_ATTRIBUTES – umgeht die Sharing-Sperre.
            using var fileHandle = OpenSourceAttributeHandle(fullSource);
            if (fileHandle.IsInvalid)
            {
                Trace.WriteLine($"{LogPrefix}Fehler: Attribut-Handle für '{fullSource}' nicht erhalten. Win32={Marshal.GetLastWin32Error()}.");
                return false;
            }

            // 3) Dateigröße bestimmen.
            if (!TryGetFileSize(fileHandle, out long fileSize))
                return false; // Fehler wurde bereits protokolliert.

            // 4) Extents der Datei ermitteln.
            var extents = ReadRetrievalPointers(fileHandle, out long startingVcn);
            if (extents is null)
                return false; // Fehler wurde bereits protokolliert.

            if (extents.Count == 0)
            {
                Trace.WriteLine($"{LogPrefix}Fehler: keine Extents gefunden (resident/komprimiert?).");
                return false;
            }

            // 5) Clustergröße bestimmen.
            if (!DetermineClusterSize(rootPath, out long clusterSize))
                return false; // Fehler wurde bereits protokolliert.

            Trace.WriteLine($"{LogPrefix}{extents.Count} Extent(s), Dateigröße {fileSize} Byte, Clustergröße {clusterSize} Byte.");

            // 6) Rohvolume öffnen.
            using var volumeHandle = OpenRawVolume(volumePath);
            if (volumeHandle.IsInvalid)
            {
                Trace.WriteLine($"{LogPrefix}Fehler: Rohvolume '{volumePath}' nicht zu öffnen (Adminrechte?). Win32={Marshal.GetLastWin32Error()}.");
                return false;
            }

            // 7) Zielverzeichnis sicherstellen.
            string? destDir = Path.GetDirectoryName(Path.GetFullPath(destPath));
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            long written;
            using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                destCreated = true;

                // 8) Extents der Reihe nach einsammeln (inkl. Sparse-Behandlung).
                if (!CopyExtents(volumeHandle, extents, startingVcn, clusterSize, dst, out written))
                    return false; // Fehler wurde bereits protokolliert.

                // 9) Auf die echte Dateigröße kürzen – der letzte Cluster ist überschüssig aufgefüllt.
                if (written < fileSize)
                {
                    Trace.WriteLine($"{LogPrefix}Fehler: nur {written} von {fileSize} Byte rekonstruiert.");
                    return false;
                }

                dst.Flush();
                dst.SetLength(fileSize);
            }

            Trace.WriteLine($"{LogPrefix}Erfolg – {fileSize} Byte kopiert ({written} Byte roh gelesen).");
            destCreated = false; // Ziel bleibt bestehen.
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"{LogPrefix}Fehler: unerwartete Ausnahme: {ex.Message}. Win32={Marshal.GetLastWin32Error()}.");
            return false;
        }
        finally
        {
            // Kein halbes Ergebnis stehen lassen.
            if (destCreated)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); }
                catch { /* Aufräumen best effort */ }
            }
        }
    }

    // ---- Schritte von TryCopyRaw -------------------------------------------

    /// <summary>
    /// Validiert Quell-/Zielpfad und leitet aus dem Quelllaufwerk den absoluten Quellpfad,
    /// den Wurzelpfad (<c>X:\</c>) und den Rohvolume-Pfad (<c>\\.\X:</c>) ab.
    /// Liefert <c>false</c> (samt Protokoll) bei leeren Pfaden oder nicht lokalem Laufwerk.
    /// </summary>
    private static bool TryResolveVolumePaths(
        string sourcePath, string destPath,
        out string fullSource, out string rootPath, out string volumePath)
    {
        fullSource = string.Empty;
        rootPath = string.Empty;
        volumePath = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
        {
            Trace.WriteLine($"{LogPrefix}Fehler: Quell- oder Zielpfad ist leer.");
            return false;
        }

        fullSource = Path.GetFullPath(sourcePath);
        string? root = Path.GetPathRoot(fullSource);
        if (root is null || root.Length < 2 || root[1] != ':')
        {
            Trace.WriteLine($"{LogPrefix}Fehler: '{fullSource}' liegt nicht auf einem lokalen Laufwerk (Root='{root}').");
            return false;
        }

        char driveLetter = char.ToUpperInvariant(root[0]);
        rootPath = $"{driveLetter}:\\";
        volumePath = $"\\\\.\\{driveLetter}:";
        return true;
    }

    /// <summary>
    /// Öffnet ein Handle auf die Quelldatei NUR mit <c>FILE_READ_ATTRIBUTES</c>. Dieser
    /// minimale Zugriff umgeht die exklusive Sharing-Sperre des Browsers. Das zurückgegebene
    /// Handle kann ungültig sein (<c>IsInvalid</c>); der Aufrufer prüft das.
    /// </summary>
    private static SafeFileHandle OpenSourceAttributeHandle(string fullSource)
        => CreateFileW(
            fullSource,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

    /// <summary>
    /// Ermittelt die echte Dateigröße über das Attribut-Handle. Liefert <c>false</c> (samt
    /// Protokoll) bei fehlgeschlagenem <c>GetFileSizeEx</c> oder nicht positiver Größe.
    /// </summary>
    private static bool TryGetFileSize(SafeFileHandle fileHandle, out long fileSize)
    {
        if (!GetFileSizeEx(fileHandle, out fileSize))
        {
            Trace.WriteLine($"{LogPrefix}Fehler: GetFileSizeEx fehlgeschlagen. Win32={Marshal.GetLastWin32Error()}.");
            return false;
        }

        if (fileSize <= 0)
        {
            Trace.WriteLine($"{LogPrefix}Fehler: Dateigröße {fileSize} – nichts zu kopieren.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Bestimmt die Clustergröße des Laufwerks (<c>SectorsPerCluster * BytesPerSector</c>) über
    /// <c>GetDiskFreeSpaceW</c>. Liefert <c>false</c> (samt Protokoll) bei Fehler oder unplausibler Größe.
    /// </summary>
    private static bool DetermineClusterSize(string rootPath, out long clusterSize)
    {
        clusterSize = 0;

        if (!GetDiskFreeSpaceW(rootPath, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
        {
            Trace.WriteLine($"{LogPrefix}Fehler: GetDiskFreeSpaceW('{rootPath}') fehlgeschlagen. Win32={Marshal.GetLastWin32Error()}.");
            return false;
        }

        clusterSize = (long)sectorsPerCluster * bytesPerSector;
        if (clusterSize <= 0)
        {
            Trace.WriteLine($"{LogPrefix}Fehler: unplausible Clustergröße {clusterSize}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Öffnet das Rohvolume <c>\\.\X:</c> mit <c>GENERIC_READ</c> für den Cluster-Rohzugriff.
    /// Das zurückgegebene Handle kann ungültig sein (<c>IsInvalid</c>, z. B. ohne Adminrechte);
    /// der Aufrufer prüft das.
    /// </summary>
    private static SafeFileHandle OpenRawVolume(string volumePath)
        => CreateFileW(
            volumePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

    /// <summary>
    /// Kopiert alle Extents der Reihe nach in <paramref name="dst"/> und meldet über
    /// <paramref name="written"/> die roh gelesene bzw. geschriebene Byte-Menge. Liefert
    /// <c>false</c> (samt Protokoll), sobald ein Extent nicht vollständig gelesen werden kann.
    /// </summary>
    private static bool CopyExtents(
        SafeFileHandle volumeHandle, List<Extent> extents, long startingVcn, long clusterSize,
        FileStream dst, out long written)
    {
        written = 0;
        var buffer = new byte[BlockSize];
        var zeros = new byte[BlockSize];

        long previousVcn = startingVcn;
        foreach (var extent in extents)
        {
            long clusterCount = extent.NextVcn - previousVcn;
            previousVcn = extent.NextVcn;
            if (clusterCount <= 0) continue;

            long remaining = clusterCount * clusterSize;

            if (!CopyOneExtent(volumeHandle, extent, remaining, clusterSize, dst, buffer, zeros, ref written))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Kopiert einen einzelnen Extent nach <paramref name="dst"/>. Ist der Extent nicht allokiert
    /// (<c>Lcn &lt; 0</c>, sparse), werden Nullbytes geschrieben und NICHT vom Volume gelesen.
    /// Andernfalls wird blockweise vom Rohvolume gelesen; Offsets und Längen bleiben cluster- und
    /// damit sektor-ausgerichtet, wie es der Rohzugriff verlangt. Liefert <c>false</c> (samt
    /// Protokoll) bei Lese-/Positionierungsfehlern oder unerwartetem Volume-Ende.
    /// </summary>
    private static bool CopyOneExtent(
        SafeFileHandle volumeHandle, Extent extent, long remaining, long clusterSize,
        FileStream dst, byte[] buffer, byte[] zeros, ref long written)
    {
        // Lcn == -1: nicht allokiert (sparse) -> Nullbytes schreiben, NICHT vom Volume lesen.
        if (extent.Lcn < 0)
        {
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, BlockSize);
                dst.Write(zeros, 0, chunk);
                remaining -= chunk;
                written += chunk;
            }
            return true;
        }

        long volumeOffset = extent.Lcn * clusterSize;

        // Blockweise lesen – Offsets und Längen bleiben cluster- und damit
        // sektor-ausgerichtet, wie es der Rohzugriff verlangt.
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, BlockSize);

            if (!SetFilePointerEx(volumeHandle, volumeOffset, IntPtr.Zero, FILE_BEGIN))
            {
                Trace.WriteLine($"{LogPrefix}Fehler: SetFilePointerEx auf Offset {volumeOffset} fehlgeschlagen. Win32={Marshal.GetLastWin32Error()}.");
                return false;
            }

            if (!ReadFile(volumeHandle, buffer, (uint)chunk, out uint read, IntPtr.Zero))
            {
                Trace.WriteLine($"{LogPrefix}Fehler: ReadFile({chunk} Byte @ {volumeOffset}) fehlgeschlagen. Win32={Marshal.GetLastWin32Error()}.");
                return false;
            }

            if (read == 0)
            {
                Trace.WriteLine($"{LogPrefix}Fehler: 0 Byte gelesen @ {volumeOffset} (unerwartetes Volume-Ende). Win32={Marshal.GetLastWin32Error()}.");
                return false;
            }

            dst.Write(buffer, 0, (int)read);
            volumeOffset += read;
            remaining -= read;
            written += read;
        }

        return true;
    }

    // ---- Extents ------------------------------------------------------------

    /// <summary>Ein Extent der Datei: Cluster-Bereich (VCN) und dessen Lage auf dem Volume (LCN).</summary>
    private readonly struct Extent
    {
        public Extent(long nextVcn, long lcn)
        {
            NextVcn = nextVcn;
            Lcn = lcn;
        }

        /// <summary>Erste VCN NACH diesem Extent (exklusives Ende).</summary>
        public long NextVcn { get; }

        /// <summary>Start-Cluster auf dem Volume; -1 = nicht allokiert (sparse).</summary>
        public long Lcn { get; }
    }

    /// <summary>
    /// Ruft <c>FSCTL_GET_RETRIEVAL_POINTERS</c> so oft auf, bis alle Extents eingesammelt sind.
    /// Behandelt <c>ERROR_MORE_DATA</c> sowohl als „Puffer zu klein“ (Puffer vergrößern) als auch
    /// als „weitere Extents folgen“ (erneuter Aufruf mit der letzten NextVcn).
    /// Liefert null bei Fehler (bereits protokolliert).
    /// </summary>
    private static List<Extent>? ReadRetrievalPointers(SafeFileHandle fileHandle, out long firstStartingVcn)
    {
        firstStartingVcn = 0;
        var result = new List<Extent>();

        // Header: int ExtentCount, int Padding, long StartingVcn -> 16 Byte, danach je 16 Byte pro Extent.
        const int HeaderSize = 16;
        const int ExtentSize = 16;

        int capacity = 64;
        long startingVcn = 0;
        bool first = true;

        IntPtr inBuffer = IntPtr.Zero;
        IntPtr outBuffer = IntPtr.Zero;
        try
        {
            inBuffer = Marshal.AllocHGlobal(sizeof(long)); // STARTING_VCN_INPUT_BUFFER

            while (true)
            {
                int outSize = HeaderSize + capacity * ExtentSize;
                if (outBuffer != IntPtr.Zero) Marshal.FreeHGlobal(outBuffer);
                outBuffer = Marshal.AllocHGlobal(outSize);

                Marshal.WriteInt64(inBuffer, startingVcn);

                bool ok = DeviceIoControl(
                    fileHandle,
                    FSCTL_GET_RETRIEVAL_POINTERS,
                    inBuffer, sizeof(long),
                    outBuffer, outSize,
                    out _, IntPtr.Zero);

                int error = ok ? 0 : Marshal.GetLastWin32Error();

                if (!ok && error != ERROR_MORE_DATA)
                {
                    Trace.WriteLine($"{LogPrefix}Fehler: FSCTL_GET_RETRIEVAL_POINTERS fehlgeschlagen. Win32={error}.");
                    return null;
                }

                int extentCount = Marshal.ReadInt32(outBuffer, 0);
                long bufferStartingVcn = Marshal.ReadInt64(outBuffer, 8);

                // Bei ERROR_MORE_DATA kann ExtentCount die Puffer-Kapazität überschreiten –
                // gültig sind nur die tatsächlich hineinpassenden Einträge.
                int usable = Math.Min(extentCount, capacity);

                if (!ok && usable <= 0)
                {
                    // Puffer war zu klein für auch nur einen Extent -> vergrößern und erneut versuchen.
                    if (capacity >= 1 << 20)
                    {
                        Trace.WriteLine($"{LogPrefix}Fehler: Extent-Puffer wächst unplausibel (Kapazität {capacity}).");
                        return null;
                    }
                    capacity *= 4;
                    continue;
                }

                if (first)
                {
                    firstStartingVcn = bufferStartingVcn;
                    first = false;
                }

                long lastNextVcn = bufferStartingVcn;
                for (int i = 0; i < usable; i++)
                {
                    int offset = HeaderSize + i * ExtentSize;
                    long nextVcn = Marshal.ReadInt64(outBuffer, offset);
                    long lcn = Marshal.ReadInt64(outBuffer, offset + 8);
                    result.Add(new Extent(nextVcn, lcn));
                    lastNextVcn = nextVcn;
                }

                if (ok) break; // Alle Extents gelesen.

                if (lastNextVcn <= startingVcn)
                {
                    Trace.WriteLine($"{LogPrefix}Fehler: kein Fortschritt bei den Extents (VCN {startingVcn}).");
                    return null;
                }

                startingVcn = lastNextVcn;
            }

            return result;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"{LogPrefix}Fehler: Extent-Ermittlung abgebrochen: {ex.Message}. Win32={Marshal.GetLastWin32Error()}.");
            return null;
        }
        finally
        {
            if (inBuffer != IntPtr.Zero) Marshal.FreeHGlobal(inBuffer);
            if (outBuffer != IntPtr.Zero) Marshal.FreeHGlobal(outBuffer);
        }
    }

    // ---- P/Invoke -----------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        IntPtr lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);
}
