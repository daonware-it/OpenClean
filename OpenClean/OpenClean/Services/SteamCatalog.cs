using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenClean.Services;

/// <summary>
/// Liefert eine Versionsangabe für Steam-Spiele, die in der Uninstall-Registry KEINE
/// <c>DisplayVersion</c> hinterlegen. Quelle ist die <c>buildid</c> aus den Steam-
/// <c>appmanifest_&lt;appid&gt;.acf</c>-Dateien (die Steam beim Update aktualisiert).
/// Reine Leseoperation; der Katalog wird einmal (lazy) aufgebaut und zwischengespeichert.
/// </summary>
public static class SteamCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> BuildIds = new(Build);

    /// <summary>Build-ID für die appid, sonst <c>null</c>.</summary>
    public static string? GetBuildId(string appId)
        => BuildIds.Value.TryGetValue(appId, out var b) ? b : null;

    private static readonly Regex PathRe = new("\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
    private static readonly Regex AppIdRe = new("\"appid\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
    private static readonly Regex BuildIdRe = new("\"buildid\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);

    private static IReadOnlyDictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>();
        try
        {
            string? steam = SteamPath();
            if (steam is null) return map;

            foreach (var lib in LibraryPaths(steam))
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;

                foreach (var acf in SafeFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        string text = File.ReadAllText(acf);
                        var id = AppIdRe.Match(text);
                        var build = BuildIdRe.Match(text);
                        if (id.Success && build.Success)
                            map[id.Groups[1].Value] = build.Groups[1].Value;
                    }
                    catch { /* defekte Manifest-Datei -> überspringen */ }
                }
            }
        }
        catch { /* Steam nicht installiert / nicht lesbar */ }
        return map;
    }

    private static string? SteamPath()
    {
        (RegistryHive Hive, RegistryView View, string Sub, string Value)[] sources =
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        };

        foreach (var s in sources)
        {
            try
            {
                using var b = RegistryKey.OpenBaseKey(s.Hive, s.View);
                using var k = b.OpenSubKey(s.Sub);
                if (k?.GetValue(s.Value) is string p && Directory.Exists(p)) return p;
            }
            catch { /* nächste Quelle */ }
        }
        return null;
    }

    private static IEnumerable<string> LibraryPaths(string steam)
    {
        var result = new List<string> { steam };
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (Match m in PathRe.Matches(File.ReadAllText(vdf)))
                {
                    // VDF escaped Backslashes doppelt: "C:\\Games" -> C:\Games.
                    string p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(p)) result.Add(p);
                }
            }
        }
        catch { /* Datei nicht lesbar -> nur Basisordner */ }
        return result;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
