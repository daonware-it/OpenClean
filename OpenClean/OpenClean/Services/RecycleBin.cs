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

    // ---- In den Papierkorb verschieben (Große-Dateien-Finder) ---------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;        // => Papierkorb statt hartem Löschen
    private const ushort FOF_NOCONFIRMATION = 0x0010;   // eigene Bestätigung ist schon erfolgt
    private const ushort FOF_NOERRORUI = 0x0400;        // Fehler melden wir selbst, kein Windows-Dialog

    // FOF_WANTNUKEWARNING hebt FOF_NOCONFIRMATION gezielt für den Fall auf, dass eine Datei
    // größer ist als die für das Laufwerk konfigurierte Papierkorb-Quote: Windows fragt dann
    // trotzdem nach ("Datei ist zu groß für den Papierkorb – endgültig löschen?"), statt
    // FOF_NOCONFIRMATION greifen zu lassen und die Datei still-endgültig zu löschen.
    // FOF_SILENT (nur Fortschrittsanzeige unterdrücken) wird deshalb bewusst NICHT gesetzt:
    // MSDN dokumentiert FOF_SILENT nur als "kein Fortschrittsdialog", in der Praxis blenden
    // manche Windows-Versionen mit FOF_SILENT aber auch Rückfragen wie die Nuke-Warnung aus –
    // das darf hier nicht passieren, sonst wäre FOF_WANTNUKEWARNING wirkungslos.
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    private const int MoveTimeoutMs = 60_000;

    /// <summary>
    /// Verschiebt die angegebenen Dateien in den Papierkorb (nicht hart löschen –
    /// ein Fehlgriff bleibt so umkehrbar).
    ///
    /// <para>Jede Datei wird einzeln übergeben: So scheitert nicht der ganze Stapel, wenn
    /// eine Datei gesperrt ist. Zurück kommen genau die Pfade, die NICHT verschoben werden
    /// konnten – die Oberfläche zeigt sie mit Fehlermarkierung weiter an.</para>
    ///
    /// <para><b>Zu große Dateien für den Papierkorb:</b> Ist eine Datei größer als die für das
    /// Laufwerk konfigurierte Papierkorb-Quote, fragt Windows wegen <see cref="FOF_WANTNUKEWARNING"/>
    /// trotz <see cref="FOF_NOCONFIRMATION"/> nach, ob sie endgültig gelöscht werden soll. Deshalb
    /// läuft die Operation je Datei auf einem eigenen STA-Thread mit Zeitlimit (gleiches Muster wie
    /// <see cref="Empty"/>): SHFileOperation pumpt darin Nachrichten und kann den Dialog anzeigen.
    /// Bricht der Nutzer ab, meldet <c>fAnyOperationsAborted</c> das korrekt als Fehlschlag; reagiert
    /// er nicht innerhalb des Zeitlimits, gilt der Pfad ebenfalls als fehlgeschlagen (fail-safe:
    /// kein Zeitüberschreiten in einen stillen Erfolg ummünzen).</para>
    ///
    /// <para><b>Pfadlänge:</b> <c>SHFileOperation</c> unterstützt keine Pfade über <c>MAX_PATH</c>
    /// (260 Zeichen). Solche Pfade schlagen bei der API fehl und landen dadurch automatisch in der
    /// <c>failed</c>-Liste – das ist beabsichtigtes Fail-safe-Verhalten, kein Bug.</para>
    /// </summary>
    public static IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths)
    {
        var failed = new List<string>();

        foreach (string path in paths)
        {
            try
            {
                int result = -1;
                bool aborted = true;

                // pFrom muss doppelt null-terminiert sein (die API erwartet eine Liste).
                string capturedPath = path;
                bool completed = RunSta(() =>
                {
                    var op = new SHFILEOPSTRUCT
                    {
                        wFunc = FO_DELETE,
                        pFrom = capturedPath + "\0\0",
                        fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_WANTNUKEWARNING
                    };

                    result = SHFileOperation(ref op);
                    aborted = op.fAnyOperationsAborted;
                }, MoveTimeoutMs);

                if (!completed || result != 0 || aborted)
                    failed.Add(path);
            }
            catch
            {
                failed.Add(path);
            }
        }

        return failed;
    }

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
