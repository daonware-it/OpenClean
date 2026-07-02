using System.Globalization;
using Microsoft.Win32;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Agent (Inventar): liest die installierten Programme aus den Windows-Uninstall-
/// Registry-Schlüsseln (HKLM 64-/32-Bit und HKCU) – genau die Quelle, die auch
/// „Apps &amp; Features" verwendet. Bewusst KEIN <c>Win32_Product</c> (WMI): das ist
/// langsam und kann ungewollt MSI-Reparaturen auslösen.
///
/// Reine Leseoperation. Jede Quelle/jeder Eintrag ist gekapselt, damit ein defekter
/// Wert die übrigen nicht verschluckt.
/// </summary>
public sealed class InstalledAppsService
{
    private const string UninstallPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();

        Safe(() => ReadUninstallKey(apps, RegistryHive.LocalMachine, RegistryView.Registry64));
        Safe(() => ReadUninstallKey(apps, RegistryHive.LocalMachine, RegistryView.Registry32));
        Safe(() => ReadUninstallKey(apps, RegistryHive.CurrentUser, RegistryView.Registry64));

        return Deduplicate(apps);
    }

    private static void Safe(Action source)
    {
        try { source(); }
        catch { /* Quelle nicht lesbar -> übrige Quellen laufen weiter */ }
    }

    private static void ReadUninstallKey(List<InstalledApp> apps, RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(UninstallPath);
        if (uninstall is null) return;

        foreach (var subKeyName in uninstall.GetSubKeyNames())
        {
            try
            {
                using var key = uninstall.OpenSubKey(subKeyName);
                if (key is null) continue;

                var app = TryReadApp(key, subKeyName);
                if (app is not null)
                    apps.Add(app);
            }
            catch { /* einzelner Eintrag defekt -> überspringen */ }
        }
    }

    /// <summary>
    /// Liest einen Uninstall-Eintrag. Gibt null zurück, wenn er kein „echtes" Programm
    /// ist (kein Anzeigename, System-Komponente, Update/Patch, oder reiner Elternknoten).
    /// </summary>
    private static InstalledApp? TryReadApp(RegistryKey key, string subKeyName)
    {
        string name = GetString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;

        // System-Komponenten und Windows-Updates/Patches ausblenden (wie „Apps & Features").
        if (GetDword(key, "SystemComponent") == 1) return null;

        string releaseType = GetString(key, "ReleaseType");
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
            return null;

        // Einträge mit ParentKeyName sind i. d. R. Bestandteile/Updates eines Hauptprogramms.
        if (!string.IsNullOrWhiteSpace(GetString(key, "ParentKeyName"))) return null;

        // Windows-Updates heißen oft „... (KB1234567)" ohne Uninstall-Möglichkeit.
        string uninstallString = GetString(key, "UninstallString");
        string quiet = GetString(key, "QuietUninstallString");

        long sizeBytes = (long)GetDword(key, "EstimatedSize") * 1024; // KB -> Bytes

        // MSI-Produktcode: bei WindowsInstaller-Paketen ist der Schlüsselname der {GUID}.
        string? msiCode = null;
        if (GetDword(key, "WindowsInstaller") == 1 && LooksLikeGuid(subKeyName))
            msiCode = subKeyName;

        return new InstalledApp
        {
            Name = name.Trim(),
            Version = GetString(key, "DisplayVersion"),
            Publisher = GetString(key, "Publisher"),
            InstallLocation = GetString(key, "InstallLocation").Trim().Trim('"'),
            EstimatedBytes = sizeBytes,
            InstallDate = ParseInstallDate(GetString(key, "InstallDate")),
            UninstallString = uninstallString,
            QuietUninstallString = quiet,
            MsiProductCode = msiCode
        };
    }

    /// <summary>Entfernt Dubletten (gleicher Name + Version, z. B. aus 64-/32-Bit-View).</summary>
    private static List<InstalledApp> Deduplicate(List<InstalledApp> apps)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<InstalledApp>(apps.Count);
        foreach (var app in apps)
        {
            var id = (app.Name.ToLowerInvariant(), app.Version.ToLowerInvariant());
            if (seen.Add(id))
                result.Add(app);
        }
        return result;
    }

    // ---- Registry-Hilfsfunktionen -------------------------------------------

    private static string GetString(RegistryKey key, string name)
        => key.GetValue(name)?.ToString() ?? "";

    private static int GetDword(RegistryKey key, string name)
        => key.GetValue(name) is int i ? i : 0;

    private static bool LooksLikeGuid(string text)
        => text.StartsWith('{') && text.EndsWith('}') && text.Length >= 34;

    private static DateTime? ParseInstallDate(string raw)
    {
        if (DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return date;
        return null;
    }
}
