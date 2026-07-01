using System.Runtime.InteropServices;

namespace OpenClean.Services;

/// <summary>
/// Papierkorb-Zugriff über die Windows-Shell (kein direktes Löschen von Dateien).
/// </summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [Flags]
    private enum RecycleFlags : uint
    {
        SHERB_NOCONFIRMATION = 0x01,
        SHERB_NOPROGRESSUI = 0x02,
        SHERB_NOSOUND = 0x04
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, RecycleFlags dwFlags);

    /// <summary>Gesamtgröße des Papierkorbs (alle Laufwerke) in Bytes, 0 bei Fehler.</summary>
    public static long GetSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        try
        {
            int hr = SHQueryRecycleBin(null, ref info);
            return hr == 0 ? info.i64Size : 0;
        }
        catch { return 0; }
    }

    /// <summary>Leert den Papierkorb ohne weitere Rückfrage. Wirft bei Fehler.</summary>
    public static void Empty()
    {
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null,
            RecycleFlags.SHERB_NOCONFIRMATION | RecycleFlags.SHERB_NOPROGRESSUI | RecycleFlags.SHERB_NOSOUND);
        // hr == 0 (S_OK) oder -2147418113 (leerer Papierkorb) gelten als Erfolg.
        if (hr != 0 && hr != unchecked((int)0x8000FFFF))
            Marshal.ThrowExceptionForHR(hr);
    }
}
