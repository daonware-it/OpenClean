using System.Diagnostics;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Agent (Deinstallation): startet das Deinstallations-Kommando eines Programms.
/// Bei stiller (Batch-)Deinstallation wird – sofern vorhanden – das
/// QuietUninstallString bzw. ein stilles msiexec-Kommando verwendet; sonst der
/// normale (interaktive) Deinstaller des Herstellers.
///
/// OpenClean löscht Programme NICHT selbst – es ruft den jeweiligen Hersteller-
/// Deinstaller auf. Das ist der einzige korrekte Weg.
/// </summary>
public sealed class UninstallerService
{
    /// <summary>
    /// Führt die Deinstallation aus und wartet auf ihr Ende. <paramref name="silent"/>
    /// bevorzugt ein unbeaufsichtigtes Kommando (für Batch-Läufe).
    /// Gibt true zurück, wenn der Deinstaller mit einem Erfolgscode endet.
    /// </summary>
    public async Task<bool> UninstallAsync(InstalledApp app, bool silent)
    {
        string? command = ChooseCommand(app, silent);
        if (string.IsNullOrWhiteSpace(command)) return false;

        var (exe, args) = SplitCommand(command);
        if (string.IsNullOrWhiteSpace(exe)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true // Hersteller-Deinstaller normal starten (inkl. evtl. Elevation).
            };

            using var process = Process.Start(psi);
            if (process is null) return false;

            await process.WaitForExitAsync();

            // 0 = Erfolg, 1605 = Produkt bereits entfernt, 3010 = Erfolg (Neustart nötig).
            int code = process.ExitCode;
            return code is 0 or 1605 or 3010;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Wählt das passende Kommando je nach gewünschtem (stillem) Modus.</summary>
    private static string? ChooseCommand(InstalledApp app, bool silent)
    {
        if (silent)
        {
            if (!string.IsNullOrWhiteSpace(app.QuietUninstallString))
                return app.QuietUninstallString;
            if (!string.IsNullOrWhiteSpace(app.MsiProductCode))
                return $"msiexec.exe /x {app.MsiProductCode} /qn /norestart";
            // Kein stilles Kommando bekannt -> auf das interaktive zurückfallen.
            return FallbackCommand(app);
        }

        // Interaktiv: normalen Deinstaller bevorzugen.
        if (!string.IsNullOrWhiteSpace(app.UninstallString))
            return app.UninstallString;
        return FallbackCommand(app);
    }

    private static string? FallbackCommand(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.UninstallString)) return app.UninstallString;
        if (!string.IsNullOrWhiteSpace(app.QuietUninstallString)) return app.QuietUninstallString;
        if (!string.IsNullOrWhiteSpace(app.MsiProductCode))
            return $"msiexec.exe /x {app.MsiProductCode}";
        return null;
    }

    /// <summary>
    /// Zerlegt ein Kommando in ausführbare Datei + Argumente. Berücksichtigt einen
    /// quotierten Pfad ("C:\…\uninst.exe" /S) ebenso wie unquotierte Kommandos
    /// (MsiExec.exe /X{GUID}).
    /// </summary>
    private static (string exe, string args) SplitCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command).Trim();
        if (command.Length == 0) return ("", "");

        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0)
                return (command.Substring(1, end - 1), command[(end + 1)..].Trim());
            return (command.Trim('"'), "");
        }

        int space = command.IndexOf(' ');
        return space > 0
            ? (command[..space], command[(space + 1)..].Trim())
            : (command, "");
    }
}
