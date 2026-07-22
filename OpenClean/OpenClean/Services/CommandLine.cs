namespace OpenClean.Services;

/// <summary>
/// Zerlegt Kommandozeilen aus Registry/Autostart in Programm + Argumente.
/// Zentralisiert die zuvor in <c>StartupService</c> und <c>UninstallerService</c>
/// byte-gleich kopierte Logik an einer Stelle.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// Zerlegt einen Befehl in ausführbare Datei und Argumente. Umgebungsvariablen werden
    /// expandiert; ein quotierter Pfad (<c>"C:\…\app.exe" /S</c>) darf Leerzeichen enthalten,
    /// unquotierte Befehle (<c>MsiExec.exe /X{GUID}</c>) werden am ersten Leerzeichen getrennt.
    /// Leerer EXE-Teil, wenn der Befehl leer ist.
    /// </summary>
    internal static (string exe, string arguments) Split(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return ("", "");
        command = Environment.ExpandEnvironmentVariables(command).Trim();

        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0
                ? (command.Substring(1, end - 1), command[(end + 1)..].Trim())
                : (command.Trim('"'), "");
        }

        int space = command.IndexOf(' ');
        return space > 0
            ? (command[..space], command[(space + 1)..].Trim())
            : (command, "");
    }
}
