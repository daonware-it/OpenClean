using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Liest den NVMe SMART/Health Information Log (Log-Page 0x02, 512 Byte) über
/// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> mit <c>StorageDeviceProtocolSpecificProperty</c>.
/// Der Zugriff wurde gegen echte NVMe-Hardware verifiziert (PoH/Cycles/TBW).
///
/// <para><b>Wirft nie.</b> Reicht der Controller den Passthrough nicht durch (USB-NVMe-Gehäuse,
/// RAID/VMD) oder fehlen Rechte, ist das Ergebnis <c>null</c> – der Aufrufer fällt dann auf die
/// Zuverlässigkeitszähler-Werte zurück.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvmeHealthReader : INvmeHealthReader
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;
    private const int StorageDeviceProtocolSpecificProperty = 50; // STORAGE_PROPERTY_ID
    private const int PropertyStandardQuery = 0;                   // STORAGE_QUERY_TYPE
    private const int ProtocolTypeNvme = 3;                        // STORAGE_PROTOCOL_TYPE
    private const uint NVMeDataTypeLogPage = 2;                    // STORAGE_PROTOCOL_NVME_DATA_TYPE
    private const uint HealthLogPage = 0x02;                       // NVMe: SMART/Health Information

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;

    // STORAGE_PROPERTY_QUERY(8) + STORAGE_PROTOCOL_SPECIFIC_DATA(40) + Nutzlast(512).
    private const int HeaderSize = 8 + 40;
    private const int PayloadLength = 512;
    private const int BufferSize = HeaderSize + PayloadLength;

    // Feld-Offsets innerhalb STORAGE_PROTOCOL_SPECIFIC_DATA (beginnt bei Puffer-Offset 8).
    private const int ProtocolTypeOffset = 8 + 0;
    private const int DataTypeOffset = 8 + 4;
    private const int RequestValueOffset = 8 + 8;
    private const int DataOffsetField = 8 + 16;   // ProtocolDataOffset (rel. zur Specific-Data)
    private const int DataLengthField = 8 + 20;   // ProtocolDataLength

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);

    public byte[]? ReadHealthLog(uint diskNumber)
    {
        try
        {
            using SafeFileHandle handle = Open(diskNumber);
            if (handle.IsInvalid) return null;

            var buffer = new byte[BufferSize];
            BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(buffer, 0);
            BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buffer, 4);
            BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buffer, ProtocolTypeOffset);
            BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(buffer, DataTypeOffset);
            BitConverter.GetBytes(HealthLogPage).CopyTo(buffer, RequestValueOffset);
            BitConverter.GetBytes((uint)40).CopyTo(buffer, DataOffsetField);
            BitConverter.GetBytes((uint)PayloadLength).CopyTo(buffer, DataLengthField);

            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, buffer, BufferSize,
                    buffer, BufferSize, out _, IntPtr.Zero))
                return null;

            uint payloadOffset = BitConverter.ToUInt32(buffer, DataOffsetField); // vom Treiber gesetzt
            int start = 8 + (int)payloadOffset;
            if (start < 0 || start + PayloadLength > buffer.Length) return null;

            var payload = new byte[PayloadLength];
            Array.Copy(buffer, start, payload, 0, PayloadLength);
            return payload;
        }
        catch
        {
            // Nicht unterstützt / keine Rechte / Passthrough blockiert -> Anzeigefall, kein Fehler.
            return null;
        }
    }

    private static SafeFileHandle Open(uint diskNumber)
    {
        string path = $@"\\.\PhysicalDrive{diskNumber}";
        // Query-Property funktioniert i. d. R. mit Zugriff 0; sonst mit R/W (App läuft als Admin).
        SafeFileHandle handle = CreateFileW(path, 0, FileShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        handle.Dispose();
        return CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
    }
}
