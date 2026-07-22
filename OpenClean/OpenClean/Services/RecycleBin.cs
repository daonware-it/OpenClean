using System.Collections.Generic;

namespace OpenClean.Services;

/// <summary>Ein einzelner Eintrag im Papierkorb (aus den <c>$I</c>-Metadaten gelesen).</summary>
public sealed record RecycleEntry(
    string OriginalPath, long Size, bool IsDirectory, string DataPath, string MetaPath);

/// <summary>
/// Papierkorb-Zugriff über die Windows-Shell bzw. direkt über die <c>$Recycle.Bin</c>-Struktur.
///
/// <para>Öffentliche statische Fassade: die Methoden delegieren an spezialisierte, interne
/// Hilfsklassen, ohne die öffentliche Oberfläche zu verändern:</para>
///
/// <list type="bullet">
/// <item><see cref="RecyclePolicy"/> – Vorhersage, ob wirklich in den Papierkorb gelöscht wird
/// (Richtlinie, Laufwerks-Quote, Netzpfad-Erkennung).</item>
/// <item><see cref="RecycleShellOperations"/> – Shell-Aufrufe: verschieben, Gesamtgröße, leeren.</item>
/// <item><see cref="RecycleBinInventory"/> – <c>$I</c>/<c>$R</c>-Einträge auflisten, löschen,
/// wiederherstellen.</item>
/// <item><see cref="StaThreadRunner"/> – Ausführung auf einem dedizierten STA-Thread.</item>
/// </list>
///
/// <para><see cref="Enumerate"/> listet die tatsächlich enthaltenen Objekte (Name, Originalpfad,
/// Größe) auf – gelesen aus den <c>$I…</c>-Metadateien je Laufwerk/Benutzer. So zeigt die
/// Vorschau den echten aktuellen Inhalt statt eines Sammel-Eintrags.</para>
///
/// <para><see cref="Empty"/> (komplett leeren) läuft auf einem eigenen STA-Thread MIT Zeitlimit:
/// <c>SHEmptyRecycleBin</c> kann aus einem Hintergrund-/MTA-Thread und bei gefülltem
/// Papierkorb blockieren; das Zeitlimit verhindert, dass ein Reinigungslauf einfriert.</para>
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// Sagt vorher, ob ein Objekt dieser Größe wirklich im Papierkorb landet – oder ob Windows es
    /// endgültig löscht. Siehe <see cref="RecyclePolicy.WillGoToRecycleBin"/>.
    /// </summary>
    public static bool WillGoToRecycleBin(string path, long sizeBytes)
        => RecyclePolicy.WillGoToRecycleBin(path, sizeBytes);

    /// <summary>
    /// Verschiebt die angegebenen Dateien in den Papierkorb (nicht hart löschen –
    /// ein Fehlgriff bleibt so umkehrbar).
    ///
    /// <para><b>Stapelverarbeitung:</b> Die Pfade werden in Stapeln an <c>SHFileOperation</c>
    /// übergeben. Scheitert ein Stapel, wird genau dieser Stapel einzeln nachgearbeitet, um die
    /// schuldigen Pfade exakt zu bestimmen – der Rückgabewert bleibt damit wie bisher „genau die
    /// Pfade, die NICHT verschoben werden konnten“, die Oberfläche zeigt sie weiterhin mit
    /// Fehlermarkierung an.</para>
    ///
    /// <para><b>Zu große Dateien für den Papierkorb:</b> Ist eine Datei größer als die für das
    /// Laufwerk konfigurierte Papierkorb-Quote, fragt Windows wegen <c>FOF_WANTNUKEWARNING</c>
    /// trotz <c>FOF_NOCONFIRMATION</c> nach, ob sie endgültig gelöscht werden soll. Deshalb
    /// läuft jeder Aufruf auf einem eigenen STA-Thread (gleiches Muster wie <see cref="Empty"/>,
    /// aber bewusst OHNE Zeitlimit): SHFileOperation pumpt darin Nachrichten und kann den Dialog
    /// anzeigen. Bricht der Nutzer ab, meldet <c>fAnyOperationsAborted</c> das korrekt als
    /// Fehlschlag – dann gilt der ganze Stapel zunächst als gescheitert und wird einzeln
    /// nachgearbeitet, sodass nur die tatsächlich betroffene Datei erneut nachfragt und die
    /// übrigen normal in den Papierkorb wandern.</para>
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
        => RecycleShellOperations.MoveToRecycleBin(paths, ownerWindow);

    /// <summary>
    /// Listet die aktuell im Papierkorb liegenden Objekte auf (über alle Laufwerke). Best effort:
    /// nicht lesbare Bereiche werden übersprungen. Siehe <see cref="RecycleBinInventory.Enumerate"/>.
    /// </summary>
    public static IReadOnlyList<RecycleEntry> Enumerate()
        => RecycleBinInventory.Enumerate();

    /// <summary>
    /// Löscht einen einzelnen Papierkorb-Eintrag endgültig, indem Daten- (<c>$R</c>) und
    /// Metadatei (<c>$I</c>) entfernt werden. True bei Erfolg.
    /// </summary>
    public static bool DeleteEntry(string? dataPath, string? metaPath)
        => RecycleBinInventory.DeleteEntry(dataPath, metaPath);

    /// <summary>
    /// Spielt einen zuvor in den Papierkorb verschobenen Eintrag an seinen Originalort zurück:
    /// verschiebt die $R-Daten nach <paramref name="originalPath"/> und entfernt die $I-Metadatei.
    /// Vorhandene Dateien am Ziel werden NICHT überschrieben (dann false). True bei Erfolg.
    /// </summary>
    public static bool RestoreEntry(string? dataPath, string? metaPath, string originalPath)
        => RecycleBinInventory.RestoreEntry(dataPath, metaPath, originalPath);

    /// <summary>Gesamtgröße des Papierkorbs (alle Laufwerke) in Bytes, 0 bei Fehler/Zeitüberschreitung.</summary>
    public static long GetSize()
        => RecycleShellOperations.GetSize();

    /// <summary>
    /// Leert den Papierkorb komplett ohne Rückfrage. Wirft bei Fehler ODER wenn die Shell
    /// nicht innerhalb des Zeitlimits antwortet (dann behandelt der Aufrufer den Papierkorb
    /// als „übersprungen", statt den ganzen Lauf hängen zu lassen).
    /// </summary>
    public static void Empty()
        => RecycleShellOperations.Empty();
}
