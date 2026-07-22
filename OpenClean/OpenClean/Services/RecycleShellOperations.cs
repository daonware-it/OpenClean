using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenClean.Services;

/// <summary>
/// Kapselt die Shell-Aufrufe des Papierkorbs: das (stapelweise) Verschieben in den Papierkorb
/// (<c>SHFileOperation</c>/FO_DELETE) sowie Gesamtgröße abfragen (<c>SHQueryRecycleBin</c>) und
/// komplett leeren (<c>SHEmptyRecycleBin</c>). Alle Aufrufe laufen über <see cref="StaThreadRunner"/>
/// auf einem eigenen STA-Thread.
/// </summary>
internal static class RecycleShellOperations
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
    /// Verschiebt die angegebenen Dateien stapelweise in den Papierkorb und liefert die Pfade
    /// zurück, die NICHT verschoben werden konnten. Implementierung hinter
    /// <see cref="RecycleBin.MoveToRecycleBin"/>.
    /// </summary>
    internal static IReadOnlyList<string> MoveToRecycleBin(IReadOnlyList<string> paths, IntPtr ownerWindow)
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

            StaThreadRunner.RunSta(() =>
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

    // ---- Gesamtgröße / komplett leeren --------------------------------------

    /// <summary>Gesamtgröße des Papierkorbs (alle Laufwerke) in Bytes, 0 bei Fehler/Zeitüberschreitung.</summary>
    internal static long GetSize()
    {
        try
        {
            long size = 0;
            bool completed = StaThreadRunner.RunSta(() =>
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
    internal static void Empty()
    {
        int hr = 0;
        bool completed = StaThreadRunner.RunSta(() =>
        {
            hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                RecycleFlags.SHERB_NOCONFIRMATION | RecycleFlags.SHERB_NOPROGRESSUI | RecycleFlags.SHERB_NOSOUND);
        }, EmptyTimeoutMs);

        if (!completed)
            throw new TimeoutException("Der Papierkorb konnte nicht innerhalb des Zeitlimits geleert werden.");

        if (hr != 0 && hr != unchecked((int)0x8000FFFF))
            Marshal.ThrowExceptionForHR(hr);
    }
}
