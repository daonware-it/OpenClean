using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OpenClean.Services;

/// <summary>
/// Ermittelt, ob ein Löschvorgang tatsächlich im Papierkorb landet – anhand der Windows-
/// Richtlinien, der laufwerksbezogenen Papierkorb-Quote und der Volume-/Netzpfad-Erkennung.
/// </summary>
internal static class RecyclePolicy
{
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
    /// wegen <c>FOF_WANTNUKEWARNING</c> immerhin nach.</item>
    /// </list>
    ///
    /// <para>Laufwerke ohne eigenen Papierkorb (Wechselmedien, Netzpfade) haben keinen
    /// Volume-Eintrag; dort wird ebenfalls endgültig gelöscht.</para>
    ///
    /// <para>Lässt sich die Quote nicht bestimmen, kommt <c>true</c> zurück: Dann greift immer noch
    /// die Nuke-Warnung von Windows selbst – lieber nicht warnen als fälschlich warnen.</para>
    /// </summary>
    internal static bool WillGoToRecycleBin(string path, long sizeBytes)
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
}
