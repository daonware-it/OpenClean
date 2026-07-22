using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenClean.Services;

/// <summary>
/// Liest den Papierkorb direkt über die <c>$Recycle.Bin</c>-Struktur je Laufwerk/Benutzer:
/// die <c>$I</c>-Metadateien (Originalpfad, Größe) und die zugehörigen <c>$R</c>-Datendateien.
/// Damit lassen sich einzelne Einträge auflisten, endgültig löschen und (für das App-Undo)
/// wiederherstellen.
/// </summary>
internal static class RecycleBinInventory
{
    /// <summary>
    /// Listet die aktuell im Papierkorb liegenden Objekte auf (über alle Laufwerke). Best effort:
    /// nicht lesbare Bereiche werden übersprungen. Erfordert für andere Benutzer Adminrechte
    /// (die OpenClean besitzt).
    /// </summary>
    internal static IReadOnlyList<RecycleEntry> Enumerate()
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
            if (isDir) size = DirectorySizeCalculator.Sum(dataPath);

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
    internal static bool DeleteEntry(string? dataPath, string? metaPath)
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

    /// <summary>
    /// Spielt einen zuvor in den Papierkorb verschobenen Eintrag an seinen Originalort zurück:
    /// verschiebt die $R-Daten nach <paramref name="originalPath"/> und entfernt die $I-Metadatei.
    /// Vorhandene Dateien am Ziel werden NICHT überschrieben (dann false). True bei Erfolg.
    /// </summary>
    internal static bool RestoreEntry(string? dataPath, string? metaPath, string originalPath)
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
