using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

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

    // ---- Papierkorb-Quote je Laufwerk ---------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint, System.Text.StringBuilder lpszVolumeName, uint cchBufferLength);

    /// <summary>
    /// Sagt vorher, ob ein Objekt dieser Größe wirklich im Papierkorb landet – oder ob Windows es
    /// endgültig löscht. Drei Fälle führen zum Nuke:
    ///
    /// <list type="bullet">
    /// <item>Die Richtlinie <c>NoRecycleFiles</c> ist gesetzt (papierkorbloses Löschen erzwungen).</item>
    /// <item>Für das Laufwerk ist <c>NukeOnDelete</c> aktiv (Windows-Option „Dateien sofort löschen“) –
    /// das trifft JEDE Datei und passiert OHNE Rückfrage.</item>
    /// <item>Das Objekt ist größer als die Quote (<c>MaxCapacity</c>) des Laufwerks – dann fragt Windows
    /// wegen <see cref="FOF_WANTNUKEWARNING"/> immerhin nach.</item>
    /// </list>
    ///
    /// <para>Laufwerke ohne eigenen Papierkorb (Wechselmedien, Netzpfade) haben keinen
    /// Volume-Eintrag; dort wird ebenfalls endgültig gelöscht.</para>
    ///
    /// <para>Lässt sich die Quote nicht bestimmen, kommt <c>true</c> zurück: Dann greift immer noch
    /// die Nuke-Warnung von Windows selbst – lieber nicht warnen als fälschlich warnen.</para>
    /// </summary>
    public static bool WillGoToRecycleBin(string path, long sizeBytes)
    {
        try
        {
            // Richtlinie schlägt alles andere.
            using (RegistryKey? policy = Registry.CurrentUser.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
            {
                if (policy?.GetValue("NoRecycleFiles") is int nrf && nrf == 1) return false;
            }

            // UNC/Netzpfade führen keinen Papierkorb -> sicher endgültig. Das ist etwas anderes als
            // "Volume nicht ermittelbar" weiter unten und muss deshalb VOR der GUID-Suche stehen.
            if (IsNetworkPath(path)) return false;

            string? guid = TryGetVolumeGuid(path);
            if (guid is null) return true; // Volume nicht ermittelbar -> Windows entscheiden lassen.

            using RegistryKey? vol = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid);

            // Kein Volume-Eintrag -> Laufwerk führt keinen Papierkorb (z. B. Wechselmedium/Netzpfad).
            if (vol is null) return false;

            if (vol.GetValue("NukeOnDelete") is int nuke && nuke == 1) return false;

            // MaxCapacity steht in MB. Fehlt der Wert, ist die Quote unbestimmt -> nicht warnen.
            if (vol.GetValue("MaxCapacity") is not int maxMb) return true;

            return sizeBytes <= maxMb * 1024L * 1024L;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True für UNC-/Netzpfade – die führen keinen Papierkorb.</summary>
    private static bool IsNetworkPath(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;

            // "\\server\share\" -> UNC. Die Win32-Präfixe \\?\ und \\.\ sind KEINE Netzpfade.
            if (!root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (root.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                root.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ermittelt die Volume-GUID ("{…}") des Laufwerks, auf dem <paramref name="path"/> liegt.</summary>
    private static string? TryGetVolumeGuid(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;

            var sb = new System.Text.StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root, sb, (uint)sb.Capacity)) return null;

            // Ergebnis: "\\?\Volume{GUID}\" -> die Registry führt nur "{GUID}".
            string name = sb.ToString();
            int start = name.IndexOf('{');
            int end = name.IndexOf('}');
            if (start < 0 || end < start) return null;

            return name[start..(end + 1)];
        }
        catch
        {
            return null;
        }
    }

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

    // Kein Zeitlimit für das Verschieben in den Papierkorb (siehe MoveToRecycleBin):
    // FOF_WANTNUKEWARNING kann auf eine MENSCHLICHE Antwort warten (Nuke-Warnung), und
    // einen Menschen kann man nicht per Timeout "abbrechen" -- der Dialog bliebe offen,
    // der Nutzer könnte Minuten später trotzdem auf "Ja" klicken und die Datei würde dann
    // wirklich endgültig gelöscht, obwohl die App den Vorgang längst als fehlgeschlagen
    // gemeldet hätte (gemeldeter Status weicht von der Realität ab). Bei Empty()/
    // SHEmptyRecycleBin ist das anders: das kann OHNE jede Nutzerinteraktion blockieren,
    // dort ist ein Zeitlimit also weiterhin richtig -- NICHT vereinheitlichen.
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

    // Stapelgrößen für MoveToRecycleBin. pFrom ist eine doppelt null-terminierte LISTE, ein
    // einziger SHFileOperation-Aufruf kann also viele Pfade auf einmal verarbeiten. Das ist der
    // entscheidende Hebel: der Aufruf kostet einen eigenen STA-Thread plus Shell-Initialisierung,
    // pro Datei wären das bei tausenden Temp-Dateien Minuten.
    // Grenzen: SHFILEOPSTRUCT hat kein Längenfeld, für pFrom gilt praktisch ein Puffer-Limit –
    // deshalb zusätzlich nach Zeichen begrenzen. 128 Objekte halten außerdem die Fortschritts-
    // anzeige feingranular genug (die Meldung erfolgt je Stapel, nicht je Datei).
    private const int RecycleBatchMaxItems = 128;
    private const int RecycleBatchMaxChars = 30_000;

    /// <summary>
    /// Verschiebt die angegebenen Dateien in den Papierkorb (nicht hart löschen –
    /// ein Fehlgriff bleibt so umkehrbar).
    ///
    /// <para><b>Stapelverarbeitung:</b> Die Pfade werden in Stapeln an <see cref="SHFileOperation"/>
    /// übergeben (siehe <see cref="RecycleBatchMaxItems"/>). Scheitert ein Stapel, wird genau dieser
    /// Stapel einzeln nachgearbeitet, um die schuldigen Pfade exakt zu bestimmen – der Rückgabewert
    /// bleibt damit wie bisher „genau die Pfade, die NICHT verschoben werden konnten“, die Oberfläche
    /// zeigt sie weiterhin mit Fehlermarkierung an.</para>
    ///
    /// <para><b>Zu große Dateien für den Papierkorb:</b> Ist eine Datei größer als die für das
    /// Laufwerk konfigurierte Papierkorb-Quote, fragt Windows wegen <see cref="FOF_WANTNUKEWARNING"/>
    /// trotz <see cref="FOF_NOCONFIRMATION"/> nach, ob sie endgültig gelöscht werden soll. Deshalb
    /// läuft jeder Aufruf auf einem eigenen STA-Thread (gleiches Muster wie <see cref="Empty"/>,
    /// aber bewusst OHNE Zeitlimit – siehe Kommentar bei <see cref="FO_DELETE"/>): SHFileOperation pumpt
    /// darin Nachrichten und kann den Dialog anzeigen. Bricht der Nutzer ab, meldet
    /// <c>fAnyOperationsAborted</c> das korrekt als Fehlschlag – dann gilt der ganze Stapel zunächst als
    /// gescheitert und wird einzeln nachgearbeitet, sodass nur die tatsächlich betroffene Datei erneut
    /// nachfragt und die übrigen normal in den Papierkorb wandern.</para>
    ///
    /// <para><b>Eigentümerfenster:</b> <paramref name="ownerWindow"/> wird als <c>hwnd</c> an
    /// <c>SHFILEOPSTRUCT</c> durchgereicht, damit ein eventueller Nuke-Warnungs-Dialog modal zum
    /// Hauptfenster erscheint und nicht dahinter verschwinden kann. <see cref="RecycleBin"/> ist ein
    /// reiner Service ohne WPF-Abhängigkeit – das Handle muss der Aufrufer (z. B. per
    /// <c>WindowInteropHelper</c>) beschaffen und übergeben. Der Default <c>default(IntPtr)</c>
    /// (kein Eigentümerfenster) funktioniert weiterhin.</para>
    ///
    /// <para><b>Pfadlänge:</b> <c>SHFileOperation</c> unterstützt keine Pfade über <c>MAX_PATH</c>
    /// (260 Zeichen). Solche Pfade schlagen bei der API fehl und landen dadurch automatisch in der
    /// <c>failed</c>-Liste – das ist beabsichtigtes Fail-safe-Verhalten, kein Bug.</para>
    /// </summary>
    public static IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths, IntPtr ownerWindow = default)
    {
        var failed = new List<string>();
        var batch = new List<string>(RecycleBatchMaxItems);
        int chars = 0;

        foreach (string path in paths)
        {
            int cost = path.Length + 1; // + Trenner-NUL des Eintrags
            if (batch.Count > 0 && (batch.Count >= RecycleBatchMaxItems || chars + cost > RecycleBatchMaxChars))
            {
                FlushRecycleBatch(batch, ownerWindow, failed);
                batch.Clear();
                chars = 0;
            }

            batch.Add(path);
            chars += cost;
        }

        if (batch.Count > 0)
            FlushRecycleBatch(batch, ownerWindow, failed);

        return failed;
    }

    /// <summary>
    /// Verschiebt einen Stapel auf einmal; scheitert er, wird er einzeln nachgearbeitet, damit
    /// nur die wirklich betroffenen Pfade in <paramref name="failed"/> landen (eine gesperrte Datei
    /// darf nicht den ganzen Stapel als fehlgeschlagen melden).
    /// </summary>
    private static void FlushRecycleBatch(List<string> batch, IntPtr ownerWindow, List<string> failed)
    {
        if (batch.Count == 1)
        {
            if (!RunRecycleOperation(batch[0] + "\0\0", ownerWindow))
                failed.Add(batch[0]);
            return;
        }

        if (RunRecycleOperation(string.Join('\0', batch) + "\0\0", ownerWindow))
            return;

        // Stapel gescheitert -> einzeln nachfassen. WICHTIG: SHFileOperation arbeitet den Stapel bis
        // zum Fehler ab, ein Teil liegt also unter Umständen schon im Papierkorb. Ein blindes
        // Wiederholen würde für diese Pfade fehlschlagen (Datei ist weg) und sie fälschlich als
        // Fehlschlag melden – im Bereinigungslauf hieße das: erfolgreich gesicherte Dateien gelten
        // als übersprungen und fehlen im Undo-Manifest. Was nicht mehr auf der Platte liegt, wurde
        // verschoben und ist erledigt.
        foreach (string path in batch)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;

            if (!RunRecycleOperation(path + "\0\0", ownerWindow))
                failed.Add(path);
        }
    }

    /// <summary>
    /// Führt eine FO_DELETE-Operation für die (doppelt null-terminierte) Pfadliste aus.
    /// Gibt <c>true</c> zurück, wenn ALLE darin enthaltenen Objekte verschoben wurden.
    /// </summary>
    private static bool RunRecycleOperation(string pFrom, IntPtr ownerWindow)
    {
        try
        {
            int result = -1;
            bool aborted = true;

            RunSta(() =>
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = ownerWindow,
                    wFunc = FO_DELETE,
                    pFrom = pFrom,
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_WANTNUKEWARNING
                };

                result = SHFileOperation(ref op);
                aborted = op.fAnyOperationsAborted;
            });

            return result == 0 && !aborted;
        }
        catch
        {
            return false;
        }
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

    // ---- Wiederherstellen (für das App-Undo) --------------------------------

    /// <summary>
    /// Spielt einen zuvor in den Papierkorb verschobenen Eintrag an seinen Originalort zurück:
    /// verschiebt die $R-Daten nach <paramref name="originalPath"/> und entfernt die $I-Metadatei.
    /// Vorhandene Dateien am Ziel werden NICHT überschrieben (dann false). True bei Erfolg.
    /// </summary>
    public static bool RestoreEntry(string? dataPath, string? metaPath, string originalPath)
    {
        if (string.IsNullOrEmpty(dataPath) || string.IsNullOrEmpty(originalPath)) return false;

        try
        {
            bool isDir = Directory.Exists(dataPath);
            bool isFile = File.Exists(dataPath);
            if (!isDir && !isFile) return false;

            // Ziel darf nicht existieren (kein Überschreiben beim Undo).
            if (File.Exists(originalPath) || Directory.Exists(originalPath)) return false;

            string? parent = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            if (isDir) Directory.Move(dataPath, originalPath);
            else File.Move(dataPath, originalPath);
        }
        catch
        {
            return false;
        }

        // $I-Metadatei entfernen; scheitert das, ist der Restore trotzdem erfolgt.
        try
        {
            if (!string.IsNullOrEmpty(metaPath) && File.Exists(metaPath))
                File.Delete(metaPath);
        }
        catch { /* verwaiste $I-Datei ist unkritisch */ }

        return true;
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

    /// <summary>
    /// Führt eine Shell-Aktion auf einem dedizierten STA-Thread aus und wartet UNBEGRENZT auf
    /// deren Ende. Bewusst ohne Zeitlimit: wird für Operationen benutzt, die auf eine menschliche
    /// Reaktion auf einen Windows-Dialog warten können (z. B. die Nuke-Warnung bei
    /// <see cref="MoveToRecycleBin"/>) – ein Timeout würde hier nur den gemeldeten Status
    /// verfälschen, ohne den Dialog tatsächlich zu schließen. Eine im Thread aufgetretene
    /// Ausnahme wird im Aufrufer erneut geworfen.
    /// </summary>
    private static void RunSta(Action action)
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
        thread.Join();

        if (captured is not null)
            throw captured;
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
