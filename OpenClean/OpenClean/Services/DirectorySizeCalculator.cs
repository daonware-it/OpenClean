using System.IO;

namespace OpenClean.Services;

/// <summary>
/// Zentrale Best-Effort-Berechnung der rekursiven Größe eines Ordners
/// (Summe aller Dateilängen). Gesperrte/nicht lesbare Einträge werden
/// übersprungen; ein Zugriffsfehler auf halbem Weg liefert die Teilsumme.
///
/// <para>Ersetzt mehrere zuvor byte-identische Kopien in TempScannerService,
/// LeftoverScannerService, RecycleBinInventory und BackupService.</para>
///
/// <para>Bewusst NICHT hierüber laufen: <c>DeepCleanEnvironment.GetDirectorySize</c>
/// (iterativ + <see cref="CancellationToken"/> gegen Stapelüberlauf bei tief
/// verschachtelten Ordnern wie WinSxS) und <c>AppFolderSize.TryGet</c>
/// (mit Cache und <c>EnumerationOptions</c>, folgt Reparse-Points nicht). Deren
/// abweichende Semantik ist Absicht.</para>
/// </summary>
public static class DirectorySizeCalculator
{
    /// <summary>Rekursive Summe aller Dateilängen unter <paramref name="dir"/>; 0 bei Fehler.</summary>
    public static long Sum(string dir)
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
}
