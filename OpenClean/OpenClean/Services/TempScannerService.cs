using System.IO;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Agent 1 + 2: Scannt bekannte Temp-/Cache-Ziele und liefert eine Vorschau-Liste.
/// Diese Klasse LÖSCHT NICHTS – sie berechnet nur, was gelöscht würde.
/// </summary>
public sealed class TempScannerService
{
    /// <summary>
    /// Ein zu scannender Wurzelordner. Pattern == null => direkte Kinder auflisten.
    /// SelfAsItem == true => der Ordner SELBST wird ein Item (mit rekursiver Gesamtgröße),
    /// nicht seine Kinder (für gezielt erkannte Cache-Ordner).
    /// </summary>
    private sealed record Root(string Path, string? Pattern, bool SelfAsItem = false);

    private sealed record CategoryDef(
        string Name,
        string Description,
        CleanupKind Kind,
        Func<IEnumerable<Root>> RootProvider,
        Func<bool>? IsAvailable = null);

    /// <summary>
    /// Baut leere Kategorien in fester Reihenfolge. Roots werden erst beim Scan aufgelöst.
    /// Install-gated Kategorien (z. B. je Browser) erscheinen nur, wenn <c>IsAvailable</c>
    /// true liefert – nicht installierte Browser werden gar nicht erst erzeugt.
    /// </summary>
    public IReadOnlyList<CleanupCategory> CreateCategories()
        => Definitions.Where(d => d.IsAvailable?.Invoke() ?? true)
                      .Select(d => new CleanupCategory
                      {
                          Name = d.Name,
                          Description = d.Description,
                          Kind = d.Kind
                      }).ToList();

    /// <summary>
    /// Scannt mehrere Kategorien nacheinander und meldet dabei den Fortschritt.
    /// Vorab wird eine grobe Gesamtzahl der Top-Level-Einträge ermittelt, damit
    /// eine Prozentanzeige möglich ist. Rein lesend/berechnend – verändert weder
    /// die Kategorien noch deren <c>Items</c>, daher gefahrlos per Task.Run im
    /// Hintergrund aufrufbar. Das Anwenden der Ergebnisse macht das ViewModel
    /// anschließend auf dem UI-Thread.
    /// </summary>
    public IReadOnlyList<(CleanupCategory Category, IReadOnlyList<ScanItem> Items)> ScanAll(
        IReadOnlyList<CleanupCategory> categories,
        IProgress<ScanProgress>? progress = null)
    {
        // Grobe Gesamtzahl (billig) für die Prozent-/ETA-Berechnung.
        int total = 0;
        foreach (var category in categories)
            total += CountTopLevel(category);
        if (total <= 0) total = 1;

        int done = 0;
        var result = new List<(CleanupCategory, IReadOnlyList<ScanItem>)>();

        // Kategorieübergreifendes Dedup: ein identischer FullPath wird nur von der
        // zuerst laufenden Kategorie beansprucht, spätere überspringen ihn.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            var items = Scan(category, progress, ref done, total, seen);
            result.Add((category, items));
        }

        // Abschließend 100 % melden.
        progress?.Report(new ScanProgress { CurrentPath = "", Done = total, Total = total });
        return result;
    }

    /// <summary>
    /// Ermittelt die zu löschenden Items einer Kategorie und gibt sie zurück.
    /// Rein lesend/berechnend – verändert die Kategorie NICHT, daher gefahrlos
    /// vom ViewModel per Task.Run im Hintergrund aufrufbar. Das Anwenden der
    /// Ergebnisse (Befüllen der ObservableCollection) macht das ViewModel auf dem UI-Thread.
    /// </summary>
    public IReadOnlyList<ScanItem> Scan(CleanupCategory category)
    {
        int done = 0;
        return Scan(category, null, ref done, 0);
    }

    /// <summary>
    /// Wie <see cref="Scan(CleanupCategory)"/>, meldet aber pro verarbeitetem
    /// Top-Level-Eintrag den Fortschritt und zählt <paramref name="done"/> hoch.
    /// </summary>
    public IReadOnlyList<ScanItem> Scan(
        CleanupCategory category,
        IProgress<ScanProgress>? progress,
        ref int done,
        int total)
    {
        // Öffentliche Einzel-Überladung: eigenes (nur kategorie-internes) Dedup-Set.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Scan(category, progress, ref done, total, seen);
    }

    /// <summary>
    /// Interne Scan-Verarbeitung mit einem von außen gereichten <paramref name="seen"/>-Set.
    /// Wird von <see cref="ScanAll"/> mit einem GEMEINSAMEN Set aufgerufen, damit ein
    /// identischer Pfad nicht in zwei Kategorien auftaucht.
    /// </summary>
    private IReadOnlyList<ScanItem> Scan(
        CleanupCategory category,
        IProgress<ScanProgress>? progress,
        ref int done,
        int total,
        HashSet<string> seen)
    {
        var results = new List<ScanItem>();
        var def = Definitions.FirstOrDefault(d => d.Name == category.Name);
        if (def is null) return results;

        if (def.Kind == CleanupKind.RecycleBin)
        {
            progress?.Report(new ScanProgress { CurrentPath = "Papierkorb …", Done = done, Total = total });
            long size = RecycleBin.GetSize();
            if (size > 0)
            {
                results.Add(new ScanItem
                {
                    FullPath = "Papierkorb (alle Laufwerke)",
                    SizeBytes = size,
                    IsDirectory = true
                });
            }
            done++;
            return results;
        }

        foreach (var root in def.RootProvider())
        {
            if (!Directory.Exists(root.Path)) continue;

            // Der Ordner selbst ist EIN Item (mit rekursiver Gesamtgröße).
            if (root.SelfAsItem)
            {
                done++;
                progress?.Report(new ScanProgress { CurrentPath = root.Path, Done = done, Total = total });
                long selfSize = DirectorySize(root.Path);
                if ((selfSize > 0 || IsEmptyReclaimable(root.Path)) && seen.Add(root.Path))
                    results.Add(new ScanItem { FullPath = root.Path, SizeBytes = selfSize, IsDirectory = true });
                continue;
            }

            // Datei-Pattern (z. B. thumbcache_*.db): passende Dateien im Ordner.
            if (root.Pattern is not null)
            {
                string[] files;
                try { files = Directory.GetFiles(root.Path, root.Pattern, SearchOption.TopDirectoryOnly); }
                catch { continue; }

                foreach (var f in files)
                {
                    done++;
                    progress?.Report(new ScanProgress { CurrentPath = f, Done = done, Total = total });
                    var item = TryMakeFileItem(f);
                    if (item is not null && seen.Add(item.FullPath))
                        results.Add(item);
                }
                continue;
            }

            // Standard: unmittelbare Kinder des Ordners auflisten (Ordner als ein Item mit Gesamtgröße).
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(root.Path); }
            catch { continue; }

            foreach (var entry in entries)
            {
                done++;
                progress?.Report(new ScanProgress { CurrentPath = entry, Done = done, Total = total });

                ScanItem? item;
                if (Directory.Exists(entry))
                {
                    long size = DirectorySize(entry);
                    item = size > 0 || IsEmptyReclaimable(entry)
                        ? new ScanItem { FullPath = entry, SizeBytes = size, IsDirectory = true }
                        : null;
                }
                else
                {
                    item = TryMakeFileItem(entry);
                }

                if (item is not null && seen.Add(item.FullPath))
                    results.Add(item);
            }
        }

        return results;
    }

    /// <summary>
    /// Billige Vorab-Zählung der Top-Level-Einträge einer Kategorie (für die Prozentanzeige).
    /// Fehler beim Zugriff werden ignoriert – es ist nur eine Schätzung.
    /// </summary>
    private static int CountTopLevel(CleanupCategory category)
    {
        var def = Definitions.FirstOrDefault(d => d.Name == category.Name);
        if (def is null) return 0;
        if (def.Kind == CleanupKind.RecycleBin) return 1;

        int count = 0;
        foreach (var root in def.RootProvider())
        {
            if (!Directory.Exists(root.Path)) continue;
            if (root.SelfAsItem) { count += 1; continue; }
            try
            {
                count += root.Pattern is not null
                    ? Directory.GetFiles(root.Path, root.Pattern, SearchOption.TopDirectoryOnly).Length
                    : Directory.GetFileSystemEntries(root.Path).Length;
            }
            catch { /* Zugriff verweigert -> ignorieren */ }
        }
        return count;
    }

    private static ScanItem? TryMakeFileItem(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return new ScanItem { FullPath = file, SizeBytes = info.Length, IsDirectory = false };
        }
        catch { return null; }
    }

    // Leere Ordner dürfen trotzdem entfernt werden, zählen aber 0 Bytes.
    private static bool IsEmptyReclaimable(string dir)
    {
        try { return !Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
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

    // ---- Ziel-Definitionen --------------------------------------------------

    private static IReadOnlyList<CategoryDef> Definitions { get; } = BuildDefinitions();

    private static IReadOnlyList<CategoryDef> BuildDefinitions() => new List<CategoryDef>
    {
        new(
            "Windows-Temp",
            "Temporäre Dateien von Windows und Programmen (%TEMP%, C:\\Windows\\Temp).",
            CleanupKind.FileDeletion,
            () => Distinct(new[]
            {
                Env("TEMP"), Env("TMP"),
                Path.Combine(WinDir, "Temp"),
                Path.Combine(LocalAppData, "Temp")
            }).Select(p => new Root(p, null))),

        new(
            "Windows-Update-Cache",
            "Bereits installierte Update-Downloads (SoftwareDistribution\\Download). Benötigt Admin.",
            CleanupKind.FileDeletion,
            () => new[] { new Root(Path.Combine(WinDir, "SoftwareDistribution", "Download"), null) }),

        new(
            "Thumbnail-Cache",
            "Miniaturansicht-Datenbanken des Explorers (thumbcache_*.db). Werden neu aufgebaut.",
            CleanupKind.FileDeletion,
            () => new[]
            {
                new Root(Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db"),
                new Root(Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), "iconcache_*.db")
            }),

        new(
            "Google Chrome",
            "Zwischengespeicherte Webinhalte von Google Chrome (alle Profile).",
            CleanupKind.FileDeletion,
            () => ChromiumRoots(ChromeUserData),
            () => Directory.Exists(ChromeUserData)),

        new(
            "Microsoft Edge",
            "Zwischengespeicherte Webinhalte von Microsoft Edge (alle Profile).",
            CleanupKind.FileDeletion,
            () => ChromiumRoots(EdgeUserData),
            () => Directory.Exists(EdgeUserData)),

        new(
            "Brave",
            "Zwischengespeicherte Webinhalte von Brave (alle Profile).",
            CleanupKind.FileDeletion,
            () => ChromiumRoots(BraveUserData),
            () => Directory.Exists(BraveUserData)),

        new(
            "Mozilla Firefox",
            "Zwischengespeicherte Webinhalte von Mozilla Firefox (alle Profile).",
            CleanupKind.FileDeletion,
            FirefoxCacheRoots,
            IsFirefoxInstalled),

        new(
            "Internet-Cache (Legacy/WinINet)",
            "Zwischengespeicherter WinINet-/IE-Cache (%LocalAppData%\\Microsoft\\Windows\\INetCache).",
            CleanupKind.FileDeletion,
            InetCacheRoots,
            () => Directory.Exists(InetCachePath)),

        new(
            "App-Caches (AppData)",
            "Bekannte Cache-Ordner installierter Apps unter %AppData% und %LocalAppData%.",
            CleanupKind.FileDeletion,
            AppDataCacheRoots),

        new(
            "Papierkorb",
            "Leert den Papierkorb aller Laufwerke über die Windows-Shell.",
            CleanupKind.RecycleBin,
            Array.Empty<Root>)
    };

    // ---- Browser (je Browser eine Kategorie) --------------------------------

    // Chromium-basiert (Chrome/Edge/Brave): jedes Profil hat Cache + Code Cache.
    private static IEnumerable<Root> ChromiumRoots(string userData)
    {
        var roots = new List<Root>();
        AddChromium(roots, userData);
        return roots;
    }

    // Firefox: <Profil>\cache2 unter %LocalAppData% (NICHT Roaming).
    private static IEnumerable<Root> FirefoxCacheRoots()
    {
        var roots = new List<Root>();
        if (Directory.Exists(FirefoxProfiles))
        {
            foreach (var profile in SafeDirs(FirefoxProfiles))
            {
                string cache2 = Path.Combine(profile, "cache2");
                if (Directory.Exists(cache2)) roots.Add(new Root(cache2, null));
            }
        }
        return roots;
    }

    // Internet Explorer / WinINet Legacy-Cache.
    private static IEnumerable<Root> InetCacheRoots()
        => Directory.Exists(InetCachePath)
            ? new[] { new Root(InetCachePath, null) }
            : Array.Empty<Root>();

    private static bool IsFirefoxInstalled()
        => Directory.Exists(FirefoxProfiles) && SafeDirs(FirefoxProfiles).Any();

    private static string ChromeUserData => Path.Combine(LocalAppData, "Google", "Chrome", "User Data");
    private static string EdgeUserData => Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data");
    private static string BraveUserData => Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data");
    private static string FirefoxProfiles => Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles");
    private static string InetCachePath => Path.Combine(LocalAppData, "Microsoft", "Windows", "INetCache");

    /// <summary>
    /// Bekannte Cache-Ordner installierter Apps: unter %AppData% (Roaming) UND %LocalAppData%
    /// werden je App-Ordner nur die unmittelbaren Unterordner mit eindeutigem Cache-Namen
    /// aufgenommen. Sicherheit: bleibt innerhalb beider Wurzeln, nur exakte Namen.
    /// </summary>
    private static IEnumerable<Root> AppDataCacheRoots()
    {
        var roots = new List<Root>();
        var cacheNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cache", "Caches", "GPUCache", "Code Cache", "CachedData", "cache2"
        };

        // Bereits von dedizierten Kategorien (Browser/IE, Temp) abgedeckte Bereiche.
        var covered = CoveredCacheAreas().ToList();

        foreach (var baseDir in new[] { RoamingAppData, LocalAppData })
        {
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;

            foreach (var appDir in SafeDirs(baseDir))
            {
                foreach (var sub in SafeDirs(appDir))
                {
                    string name = Path.GetFileName(sub.TrimEnd(Path.DirectorySeparatorChar));
                    if (!cacheNames.Contains(name)) continue;

                    // Überspringen, wenn gleich/Nachfahre/Vorfahre eines abgedeckten Bereichs.
                    if (covered.Any(area => PathsRelated(sub, area))) continue;

                    roots.Add(new Root(sub, null, SelfAsItem: true));
                }
            }
        }

        return roots;
    }

    /// <summary>
    /// Bereiche, die bereits von „Browser-Caches" bzw. „Windows-Temp" abgedeckt sind und
    /// daher nicht zusätzlich als „App-Caches (AppData)" auftauchen sollen.
    /// </summary>
    private static IEnumerable<string> CoveredCacheAreas()
    {
        var areas = new[]
        {
            Path.Combine(LocalAppData, "Google", "Chrome"),
            Path.Combine(LocalAppData, "Microsoft", "Edge"),
            Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser"),
            Path.Combine(LocalAppData, "Mozilla", "Firefox"),
            Path.Combine(LocalAppData, "Microsoft", "Windows"), // INetCache & Explorer-Thumbs
            Env("TEMP"),
            Env("TMP"),
            Path.Combine(LocalAppData, "Temp"),
            Path.Combine(WinDir, "Temp")
        };
        return areas.Where(a => !string.IsNullOrWhiteSpace(a));
    }

    /// <summary>Normalisiert einen Pfad (voll qualifiziert, ohne Trailing-Separator).</summary>
    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    /// <summary>
    /// True, wenn <paramref name="path"/> gleich <paramref name="basePath"/> oder ein
    /// Nachfahre davon ist. Nutzt Pfad-Grenzen (Separator), damit „…\Temp" nicht
    /// fälschlich „…\TempX" matcht. Case-insensitiv.
    /// </summary>
    private static bool IsWithinOrEqual(string path, string basePath)
    {
        string a = NormalizePath(path);
        string b = NormalizePath(basePath);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True, wenn die Pfade gleich sind oder einer den anderen enthält (beide Richtungen).</summary>
    private static bool PathsRelated(string a, string b)
        => IsWithinOrEqual(a, b) || IsWithinOrEqual(b, a);

    private static void AddChromium(List<Root> roots, string userData)
    {
        if (!Directory.Exists(userData)) return;
        foreach (var profile in SafeDirs(userData))
        {
            string cache = Path.Combine(profile, "Cache");
            string codeCache = Path.Combine(profile, "Code Cache");
            if (Directory.Exists(cache)) roots.Add(new Root(cache, null));
            if (Directory.Exists(codeCache)) roots.Add(new Root(codeCache, null));
        }
    }

    private static IEnumerable<string> SafeDirs(string parent)
    {
        try { return Directory.GetDirectories(parent); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> paths)
        => paths.Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}
