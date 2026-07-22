using System.IO;
using Microsoft.Win32;
using OpenClean.Models;
using OpenClean.Services.Integrity;
using OpenClean.Services.Safety;

namespace OpenClean.Services;

/// <summary>Ergebnis einer Reste-Löschung.</summary>
public sealed class LeftoverCleanupResult
{
    public int DeletedCount { get; set; }
    public long FreedBytes { get; set; }
}

/// <summary>
/// Agent (Reste-Finder): sucht nach einer Deinstallation nach zurückgebliebenen
/// Ordnern und Registry-Schlüsseln des Programms und entfernt sie – aber erst nach
/// ausdrücklicher Bestätigung des Nutzers (Vorschau in der UI).
///
/// Bewusst KONSERVATIV: es werden nur eindeutig zuordenbare Reste vorgeschlagen
/// (Namensabgleich mit Schutz vor generischen/System-Namen). Beim Löschen greifen
/// zusätzliche Sicherheits-Guards, damit niemals System- oder Wurzelpfade getroffen werden.
/// </summary>
public sealed class LeftoverScannerService
{
    // Ordnernamen, die niemals als „App-Rest" vorgeschlagen werden (zu generisch/System).
    private static readonly HashSet<string> BlockedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "microsoftcorporation", "windows", "windowsapps", "common", "commonfiles",
        "temp", "tmp", "cache", "packages", "google", "googlellc", "mozilla", "apple", "appleinc",
        "intel", "nvidia", "amd", "comms", "programs", "startmenu", "adobe", "oracle"
    };

    /// <summary>Registry-Basispfade unter denen App-spezifische Schlüssel liegen.</summary>
    private static readonly (RegistryHive Hive, string Prefix, string SubPath)[] RegistryBases =
    {
        (RegistryHive.CurrentUser, "HKCU", @"Software"),
        (RegistryHive.LocalMachine, "HKLM", @"Software"),
        (RegistryHive.LocalMachine, "HKLM", @"Software\WOW6432Node")
    };

    /// <summary>Sucht Reste eines gerade deinstallierten Programms (rein lesend).</summary>
    public IReadOnlyList<LeftoverItem> Scan(InstalledApp app)
    {
        var results = new List<LeftoverItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanFolders(app, results, seen);
        ScanRegistry(app, results, seen);

        return results;
    }

    // ---- Ordner-Reste -------------------------------------------------------

    private void ScanFolders(InstalledApp app, List<LeftoverItem> results, HashSet<string> seen)
    {
        // 1) Der Installationsordner selbst, falls er die Deinstallation „überlebt" hat.
        if (!string.IsNullOrWhiteSpace(app.InstallLocation) &&
            Directory.Exists(app.InstallLocation) &&
            IsSafeFolder(app.InstallLocation) &&
            seen.Add(NormalizePath(app.InstallLocation)))
        {
            results.Add(new LeftoverItem
            {
                Path = app.InstallLocation,
                Kind = LeftoverKind.Folder,
                SizeBytes = DirectorySizeCalculator.Sum(app.InstallLocation)
            });
        }

        // 2) App-/Herstellerordner unter den bekannten Datenverzeichnissen.
        foreach (var baseDir in DataRoots())
        {
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;

            foreach (var child in SafeDirs(baseDir))
            {
                string folderName = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar));
                if (!Matches(folderName, app)) continue;
                if (!IsSafeFolder(child)) continue;
                if (!seen.Add(NormalizePath(child))) continue;

                results.Add(new LeftoverItem
                {
                    Path = child,
                    Kind = LeftoverKind.Folder,
                    SizeBytes = DirectorySizeCalculator.Sum(child)
                });
            }
        }
    }

    private static IEnumerable<string> DataRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),        // Roaming
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),   // Local
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),  // ProgramData
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
    };

    // ---- Registry-Reste -----------------------------------------------------

    private void ScanRegistry(InstalledApp app, List<LeftoverItem> results, HashSet<string> seen)
    {
        var candidates = new List<string>();
        if (IsUsableToken(app.Name)) candidates.Add(app.Name.Trim());
        if (IsUsableToken(app.Publisher)) candidates.Add(app.Publisher.Trim());

        foreach (var (hive, prefix, subPath) in RegistryBases)
        {
            RegistryKey? baseKey = null;
            RegistryKey? parent = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                parent = baseKey.OpenSubKey(subPath);
                if (parent is null) continue;

                foreach (var candidate in candidates)
                {
                    using var sub = parent.OpenSubKey(candidate);
                    if (sub is null) continue;

                    string full = $@"{prefix}\{subPath}\{candidate}";
                    if (seen.Add(full))
                    {
                        results.Add(new LeftoverItem
                        {
                            Path = full,
                            Kind = LeftoverKind.RegistryKey
                        });
                    }
                }
            }
            catch { /* Zugriff verweigert -> überspringen */ }
            finally
            {
                parent?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    // ---- Löschen ------------------------------------------------------------

    /// <summary>Löscht die ausgewählten Reste (mit Sicherheits-Guards) und meldet das Ergebnis.</summary>
    public LeftoverCleanupResult DeleteSelected(IEnumerable<LeftoverItem> items)
        => DeleteSelected(items, session: null);

    /// <summary>
    /// Löscht die ausgewählten Reste (mit Sicherheits-Guards) und meldet das Ergebnis. Ist
    /// <paramref name="session"/> gesetzt, werden Ordner-Reste (Nutzerdaten) zuvor in die
    /// Backup-Sitzung gesichert, sodass sie über Undo wiederhergestellt werden können.
    /// Registry-Schlüssel bleiben davon unberührt (die deckt der Wiederherstellungspunkt ab).
    /// </summary>
    public LeftoverCleanupResult DeleteSelected(IEnumerable<LeftoverItem> items, BackupSession? session)
    {
        var result = new LeftoverCleanupResult();

        // Sperre bei erkannter Manipulation (OPCL-20): nichts löschen, leeres Ergebnis melden.
        if (IntegrityState.IsBlocked) return result;

        foreach (var item in items.Where(i => i.IsSelected))
        {
            try
            {
                if (item.Kind == LeftoverKind.Folder)
                {
                    if (!IsSafeFolder(item.Path) || !Directory.Exists(item.Path)) continue;

                    if (session is not null)
                    {
                        // Nutzerdaten zuerst sichern, dann löschen (Undo-fähig).
                        if (session.TryDelete(item.Path, isDirectory: true, item.SizeBytes,
                                SafeDeleteStrategy.PreferBackup) == SafeDeleteOutcome.Deleted)
                        {
                            result.DeletedCount++;
                            result.FreedBytes += item.SizeBytes;
                        }
                    }
                    else
                    {
                        Directory.Delete(item.Path, recursive: true);
                        result.DeletedCount++;
                        result.FreedBytes += item.SizeBytes;
                    }
                }
                else if (DeleteRegistryKey(item.Path))
                {
                    result.DeletedCount++;
                }
            }
            catch { /* in Benutzung / kein Zugriff -> überspringen */ }
        }

        return result;
    }

    private static bool DeleteRegistryKey(string fullPath)
    {
        // Erwartetes Format: "HKCU\Software\..." bzw. "HKLM\Software\..." (ggf. WOW6432Node).
        int firstSep = fullPath.IndexOf('\\');
        if (firstSep <= 0) return false;

        string hiveToken = fullPath[..firstSep];
        string subPath = fullPath[(firstSep + 1)..];

        // Guard: nur unterhalb von Software, mindestens ein Schlüssel tief, und der
        // Blatt-Name nicht generisch/System.
        if (!subPath.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase)) return false;
        string leaf = subPath[(subPath.LastIndexOf('\\') + 1)..];
        if (subPath.Equals(@"Software\WOW6432Node", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsBlockedName(leaf)) return false;

        RegistryHive hive = hiveToken.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.LocalMachine
            : RegistryHive.CurrentUser;

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        // Existiert der Schlüssel? Sonst nichts tun.
        using (var check = baseKey.OpenSubKey(subPath)) { if (check is null) return false; }

        baseKey.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
        return true;
    }

    // ---- Namensabgleich / Guards -------------------------------------------

    /// <summary>True, wenn ein Ordnername eindeutig zum Programm/Hersteller passt.</summary>
    private static bool Matches(string folderName, InstalledApp app)
    {
        string folder = Normalize(folderName);
        if (folder.Length < 4 || IsBlockedName(folderName)) return false;

        string name = Normalize(app.Name);
        string publisher = Normalize(app.Publisher);

        // Hersteller: nur bei exakter Übereinstimmung und ausreichend spezifisch.
        if (publisher.Length >= 5 && folder == publisher) return true;

        if (name.Length >= 4)
        {
            if (folder == name) return true;
            if (folder.Length >= 5 && name.StartsWith(folder, StringComparison.Ordinal)) return true;
            if (name.Length >= 5 && folder.StartsWith(name, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool IsUsableToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Normalize(value).Length >= 4 && !IsBlockedName(value);
    }

    private static bool IsBlockedName(string name) => BlockedNames.Contains(Normalize(name));

    /// <summary>Nur Buchstaben/Ziffern, klein – für robusten Namensvergleich.</summary>
    private static string Normalize(string text)
        => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Schutz-Filter für Ordner: voll qualifiziert, kein Laufwerksstamm, nicht Windows/
    /// System/ProgramFiles-Wurzel selbst und nicht direkt eine der Daten-Wurzeln.
    /// </summary>
    private static bool IsSafeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;

        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return false; }

        // Muss einen übergeordneten Ordner haben (kein "C:\").
        string? parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent)) return false;

        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var f in forbidden)
        {
            if (string.IsNullOrEmpty(f)) continue;
            string norm = f.TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(norm, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static string NormalizePath(string path) => PathScope.Normalize(path);

    private static IEnumerable<string> SafeDirs(string parent)
    {
        try { return Directory.GetDirectories(parent); }
        catch { return Array.Empty<string>(); }
    }

}
