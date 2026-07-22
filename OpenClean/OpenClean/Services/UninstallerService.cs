using System.Diagnostics;
using OpenClean.Models;
using OpenClean.Services.Integrity;

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
        // Sperre bei erkannter Manipulation (OPCL-20). Auch der Batch-Lauf aus dem
        // Premium-Modul kommt hier vorbei.
        if (IntegrityState.IsBlocked) return false;

        string? command = ChooseCommand(app, silent);
        if (string.IsNullOrWhiteSpace(command)) return false;

        var (exe, args) = CommandLine.Split(command);
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

}
