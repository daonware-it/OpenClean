using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace OpenClean.Services;

/// <summary>
/// Ermittelt das echte Programm-Icon eines Browsers für die Bereinigungs-Kategorien
/// (Chrome/Edge/Brave/Firefox) über die „App Paths"-Registry bzw. bekannte Installationspfade
/// und extrahiert es via <see cref="AppIconService"/>. So werden echte Marken-Icons gezeigt
/// statt eines generischen Symbols. <c>null</c>, wenn der Browser nicht gefunden wird.
/// </summary>
public static class BrowserIcons
{
    private static readonly Dictionary<string, string> ExeByKey = new()
    {
        ["cat.chrome"] = "chrome.exe",
        ["cat.edge"] = "msedge.exe",
        ["cat.brave"] = "brave.exe",
        ["cat.firefox"] = "firefox.exe",
    };

    /// <summary>Icon für den Kategorie-Key (z. B. "cat.chrome"), sonst <c>null</c>.</summary>
    public static ImageSource? Resolve(string categoryKey)
    {
        if (!ExeByKey.TryGetValue(categoryKey, out var exe)) return null;
        string? path = ResolveExe(exe);
        return path is null ? null : AppIconService.GetIcon(path);
    }

    private static string? ResolveExe(string exe)
    {
        // "App Paths": der Default-Wert des Schlüssels ist der volle EXE-Pfad.
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (k?.GetValue(null) is string p)
                {
                    p = p.Trim().Trim('"');
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* Quelle nicht lesbar -> nächste probieren */ }
        }

        foreach (var p in FallbackPaths(exe))
            if (File.Exists(p)) return p;

        return null;
    }

    private static IEnumerable<string> FallbackPaths(string exe)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        switch (exe)
        {
            case "chrome.exe":
                yield return Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(pfx86, "Google", "Chrome", "Application", "chrome.exe");
                break;
            case "msedge.exe":
                yield return Path.Combine(pfx86, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe");
                break;
            case "brave.exe":
                yield return Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
                yield return Path.Combine(lad, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
                break;
            case "firefox.exe":
                yield return Path.Combine(pf, "Mozilla Firefox", "firefox.exe");
                yield return Path.Combine(pfx86, "Mozilla Firefox", "firefox.exe");
                break;
        }
    }
}
