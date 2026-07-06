using System;
using System.IO;

namespace OpenClean.Services;

/// <summary>
/// Schreibt ein einfaches Textprotokoll aller Update-Versuche als <c>updates.log</c>
/// im Datenverzeichnis (<see cref="AppPaths.DataDirectory"/>). Alle Dateioperationen sind gekapselt – bei
/// fehlendem Zugriff oder Datenträgerfehlern wird nie eine Ausnahme durchgereicht
/// (das Protokoll ist optional und darf den Update-Vorgang nie stören).
/// </summary>
public sealed class UpdateLogService
{
    /// <summary>Vollständiger Pfad der Protokolldatei (<c>updates.log</c> im Datenverzeichnis).</summary>
    public string LogFilePath { get; }

    /// <summary>Bestimmt den Protokollpfad; die Datei wird erst beim ersten <see cref="Log"/> angelegt.</summary>
    public UpdateLogService()
    {
        LogFilePath = AppPaths.GetDataFile("updates.log");
    }

    /// <summary>
    /// Hängt eine Zeile ans Protokoll an, z. B.
    /// <c>2026-07-02 14:35:12  [OK]  Firefox  1.2.3 -> 1.2.4</c>.
    /// Schlägt das Schreiben fehl, wird es stillschweigend verworfen.
    /// </summary>
    public void Log(string appName, string fromVersion, string toVersion, bool success)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string status = success ? "[OK]" : "[FEHLT]";
            string line = $"{stamp}  {status}  {appName}  {fromVersion} -> {toVersion}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, line);
        }
        catch
        {
            // Nicht schreibbar (Rechte/Datenträger) -> Protokolleintrag wird verworfen.
        }
    }
}
