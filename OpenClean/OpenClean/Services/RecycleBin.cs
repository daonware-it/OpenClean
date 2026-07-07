using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OpenClean.Services;

/// <summary>Ein einzelner Eintrag im Papierkorb (aus den <c>$I</c>-Metadaten gelesen).</summary>
public sealed record RecycleEntry(
    string OriginalPath, long Size, bool IsDirectory, string DataPath, string MetaPath);

/// <summary>
/// Papierkorb-Zugriff über die Windows-Shell bzw. direkt über die <c>$Recycle.Bin</c>-Struktur.
///
/// <para><see cref="Enumerate"/> listet die tatsächlich enthaltenen Objekte (Name, Originalpfad,
/// Größe) auf – gelesen aus den <c>$I…</c>-Metadateien je Laufwerk/Benutzer. So zeigt die
/// Vorschau den echten aktuellen Inhalt statt eines Sammel-Eintrags.</para>
///
/// <para><see cref="Empty"/> (komplett leeren) läuft auf einem eigenen STA-Thread MIT Zeitlimit:
/// <see cref="SHEmptyRecycleBin"/> kann aus einem Hintergrund-/MTA-Thread und bei gefülltem
/// Papierkorb blockieren; das Zeitlimit verhindert, dass ein Reinigungslauf einfriert.</para>
/// </summary>
public static class RecycleBin
{
    private const int QueryTimeoutMs = 8_000;
    private const int EmptyTimeoutMs = 30_000;

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

    // ---- Inhalt auflisten ---------------------------------------------------

    /// <summary>
    /// Listet die aktuell im Papierkorb liegenden Objekte auf (über alle Laufwerke). Best effort:
    /// nicht lesbare Bereiche werden übersprungen. Erfordert für andere Benutzer Adminrechte
    /// (die OpenClean besitzt).
    /// </summary>
    public static IReadOnlyList<RecycleEntry> Enumerate()
    {
        var entries = new List<RecycleEntry>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string root;
                try
                {
                    if (!drive.IsReady) continue;
                    root = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                }
                catch { continue; }

                if (!Directory.Exists(root)) continue;

                foreach (var sidDir in SafeDirs(root))
                    foreach (var meta in SafeFiles(sidDir, "$I*"))
                    {
                        var entry = TryParseMeta(meta);
                        if (entry is not null) entries.Add(entry);
                    }
            }
        }
        catch { /* best effort */ }
        return entries;
    }

    /// <summary>
    /// Liest eine <c>$I</c>-Metadatei (Originalpfad, Größe) und paart sie mit der zugehörigen
    /// <c>$R</c>-Datendatei. Gibt null zurück, wenn die Datei nicht lesbar/gültig ist.
    /// </summary>
    private static RecycleEntry? TryParseMeta(string metaFile)
    {
        try
        {
            byte[] data = File.ReadAllBytes(metaFile);
            if (data.Length < 24) return null;

            long version = BitConverter.ToInt64(data, 0);
            long size = BitConverter.ToInt64(data, 8);

            string originalPath;
            if (version >= 2)
            {
                // Windows 10/11: 4-Byte-Längenfeld (Zeichen inkl. Nullterminator) + UTF-16-Pfad.
                if (data.Length < 28) return null;
                int chars = BitConverter.ToInt32(data, 24);
                int byteLen = chars * 2;
                if (chars <= 0 || 28 + byteLen > data.Length) return null;
                originalPath = Encoding.Unicode.GetString(data, 28, byteLen);
            }
            else
            {
                // Vista–8.1: fester 260-WCHAR-Pfad ab Offset 24.
                int avail = Math.Min(520, data.Length - 24);
                if (avail <= 0) return null;
                originalPath = Encoding.Unicode.GetString(data, 24, avail);
            }

            int nul = originalPath.IndexOf('\0');
            if (nul >= 0) originalPath = originalPath.Substring(0, nul);
            if (string.IsNullOrEmpty(originalPath)) return null;

            // $R-Datendatei: gleicher Ordner, Dateiname mit "$R" statt "$I".
            string dir = Path.GetDirectoryName(metaFile) ?? "";
            string name = Path.GetFileName(metaFile);
            if (!name.StartsWith("$I", StringComparison.Ordinal)) return null;
            string dataPath = Path.Combine(dir, "$R" + name.Substring(2));

            // Verwaiste $I-Metadatei ohne zugehörige $R-Daten überspringen: das sind Reste
            // bereits entfernter Objekte, die Windows selbst NICHT (mehr) zählt – sie würden
            // Anzahl und Gesamtgröße fälschlich aufblähen.
            bool isDir = Directory.Exists(dataPath);
            bool isFile = File.Exists(dataPath);
            if (!isDir && !isFile) return null;

            // Für Ordner ist das Größenfeld oft 0 -> tatsächliche Größe rekursiv ermitteln.
            if (isDir) size = DirectorySize(dataPath);

            return new RecycleEntry(originalPath, size, isDir, dataPath, metaFile);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Löscht einen einzelnen Papierkorb-Eintrag endgültig, indem Daten- (<c>$R</c>) und
    /// Metadatei (<c>$I</c>) entfernt werden. True bei Erfolg.
    /// </summary>
    public static bool DeleteEntry(string? dataPath, string? metaPath)
    {
        bool ok = true;
        try
        {
            if (!string.IsNullOrEmpty(dataPath))
            {
                if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
                else if (File.Exists(dataPath))
                {
                    var fi = new FileInfo(dataPath) { Attributes = FileAttributes.Normal };
                    fi.Delete();
                }
            }
        }
        catch { ok = false; }

        try
        {
            if (!string.IsNullOrEmpty(metaPath) && File.Exists(metaPath))
                File.Delete(metaPath);
        }
        catch { ok = false; }

        return ok;
    }

    // ---- Gesamtgröße / komplett leeren --------------------------------------

    /// <summary>Gesamtgröße des Papierkorbs (alle Laufwerke) in Bytes, 0 bei Fehler/Zeitüberschreitung.</summary>
    public static long GetSize()
    {
        try
        {
            long size = 0;
            bool completed = RunSta(() =>
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                int hr = SHQueryRecycleBin(null, ref info);
                if (hr == 0) size = info.i64Size;
            }, QueryTimeoutMs);
            return completed ? size : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Leert den Papierkorb komplett ohne Rückfrage. Wirft bei Fehler ODER wenn die Shell
    /// nicht innerhalb des Zeitlimits antwortet (dann behandelt der Aufrufer den Papierkorb
    /// als „übersprungen", statt den ganzen Lauf hängen zu lassen).
    /// </summary>
    public static void Empty()
    {
        int hr = 0;
        bool completed = RunSta(() =>
        {
            hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                RecycleFlags.SHERB_NOCONFIRMATION | RecycleFlags.SHERB_NOPROGRESSUI | RecycleFlags.SHERB_NOSOUND);
        }, EmptyTimeoutMs);

        if (!completed)
            throw new TimeoutException("Der Papierkorb konnte nicht innerhalb des Zeitlimits geleert werden.");

        if (hr != 0 && hr != unchecked((int)0x8000FFFF))
            Marshal.ThrowExceptionForHR(hr);
    }

    // ---- Hilfsfunktionen ----------------------------------------------------

    /// <summary>
    /// Führt eine Shell-Aktion auf einem dedizierten STA-Thread aus und wartet höchstens
    /// <paramref name="timeoutMs"/> ms. Liefert false bei Zeitüberschreitung. Eine im Thread
    /// aufgetretene Ausnahme wird im Aufrufer erneut geworfen.
    /// </summary>
    private static bool RunSta(Action action, int timeoutMs)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        })
        {
            IsBackground = true,
            Name = "RecycleBinShell"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeoutMs))
            return false;

        if (captured is not null)
            throw captured;
        return true;
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* gesperrt/nicht lesbar -> überspringen */ }
            }
        }
        catch { /* Zugriff verweigert -> Teilsumme */ }
        return total;
    }

    private static IEnumerable<string> SafeDirs(string parent)
    {
        try { return Directory.GetDirectories(parent); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string parent, string pattern)
    {
        try { return Directory.GetFiles(parent, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
