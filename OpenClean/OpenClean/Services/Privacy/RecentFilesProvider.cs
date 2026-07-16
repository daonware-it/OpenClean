using System.IO;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Safety;

namespace OpenClean.Services.Privacy;

/// <summary>
/// Task 5 (Agent 2): Zuletzt verwendete Dateien löschen.
///
/// Quelle: <c>%AppData%\Microsoft\Windows\Recent</c>
///   • Direkte <c>.lnk</c>-Verknüpfungen auf zuletzt geöffnete Dateien/Ordner.
///   • Jump-List-Daten in <c>AutomaticDestinations</c> und <c>CustomDestinations</c>.
///
/// Robustheit: Gesperrte/nicht löschbare Dateien werden übersprungen; Scan/Clean
/// lassen NIE eine Ausnahme durch (im Zweifel leere Liste bzw. 0).
/// </summary>
public sealed class RecentFilesProvider : IPrivacyProvider
{
    public string Name => Loc.T("privacy.recentFiles.name");
    public string Description => Loc.T("privacy.recentFiles.description");

    // Es werden persistente Spuren gelöscht -> Bestätigung anfordern.
    public bool RequiresConfirmation => true;

    /// <summary>Opaker Lösch-Handle: vollständiger Pfad der zu löschenden Datei.</summary>
    private sealed record RecentFileHandle(string FullPath);

    private static string RecentFolder
        => Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    private static string AutomaticDestinations
        => Path.Combine(RecentFolder, "AutomaticDestinations");

    private static string CustomDestinations
        => Path.Combine(RecentFolder, "CustomDestinations");

    public bool IsAvailable
    {
        get
        {
            try
            {
                string folder = RecentFolder;
                return !string.IsNullOrEmpty(folder) && Directory.Exists(folder);
            }
            catch { return false; }
        }
    }

    public Task<IReadOnlyList<PrivacyItem>> ScanAsync()
    {
        var items = new List<PrivacyItem>();
        try
        {
            string folder = RecentFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);

            // Direkte .lnk-Verknüpfungen im Recent-Ordner (nicht rekursiv).
            AddFiles(folder, "*.lnk", stripLnk: true, quelle: "Zuletzt geöffnet", items);

            // Jump-Lists: alle Dateien in den beiden Destinations-Ordnern.
            AddFiles(AutomaticDestinations, "*", stripLnk: false, quelle: "Sprungliste (automatisch)", items);
            AddFiles(CustomDestinations, "*", stripLnk: false, quelle: "Sprungliste (angeheftet)", items);
        }
        catch { /* im Zweifel: bis hierhin Gesammeltes zurückgeben */ }

        return Task.FromResult<IReadOnlyList<PrivacyItem>>(items);
    }

    private static void AddFiles(string folder, string pattern, bool stripLnk, string quelle, List<PrivacyItem> items)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    string display = stripLnk ? Path.GetFileNameWithoutExtension(fileName) : fileName;

                    items.Add(new PrivacyItem
                    {
                        Name = display,
                        Detail = quelle,
                        Tag = new RecentFileHandle(file)
                    });
                }
                catch { /* einzelne Datei defekt -> überspringen */ }
            }
        }
        catch { /* Ordner nicht lesbar -> überspringen */ }
    }

    public Task<int> CleanAsync(IEnumerable<PrivacyItem> selected)
    {
        int deleted = 0;

        // Nutzerdateien (.lnk / Jump-Lists): wenn das Datei-Backup aktiv ist, über eine
        // Backup-Session löschen, damit ein Undo möglich bleibt. Sonst direkter Löschpfad.
        var session = SettingsService.Instance.Current.Safety.BackupBeforeDelete
            ? BackupService.Instance.BeginSession("privacy.recent")
            : null;

        try
        {
            foreach (var item in selected)
            {
                try
                {
                    if (item.Tag is not RecentFileHandle handle) continue;
                    if (!File.Exists(handle.FullPath)) continue;

                    if (session is not null)
                    {
                        // Dateigröße defensiv ermitteln (für Backup-Buchführung).
                        long size;
                        try { size = new FileInfo(handle.FullPath).Length; }
                        catch { size = 0; }

                        var outcome = session.TryDelete(handle.FullPath, false, size, SafeDeleteStrategy.PreferBackup);
                        if (outcome == SafeDeleteOutcome.Deleted)
                            deleted++;
                    }
                    else
                    {
                        // Schreibschutz aufheben, damit auch attributierte Dateien löschbar sind.
                        try { File.SetAttributes(handle.FullPath, FileAttributes.Normal); }
                        catch { /* nicht kritisch */ }

                        File.Delete(handle.FullPath);
                        deleted++;
                    }
                }
                catch { /* gesperrte/nicht löschbare Datei -> überspringen */ }
            }
        }
        catch { /* unerwarteter Fehler -> gelöschte Anzahl zurückgeben */ }
        finally
        {
            // Backup-Session immer abschließen (auch im Fehlerfall).
            session?.Commit();
        }

        return Task.FromResult(deleted);
    }
}
