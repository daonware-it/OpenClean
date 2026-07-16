namespace OpenClean.Models;

/// <summary>
/// Ein Autostart-Eintrag, dessen Start OpenClean verzögert: Der Original-Eintrag ist
/// über StartupApproved deaktiviert, gestartet wird das Programm stattdessen von einer
/// geplanten Aufgabe mit Anmelde-Trigger und Verzögerung.
///
/// Wird in der settings.json persistiert, damit die Verzögerung nach einem App-Neustart
/// weiterhin angezeigt und sauber zurückgenommen werden kann.
/// </summary>
public sealed class DelayedStartupItem
{
    /// <summary>Identität über Neustarts hinweg: "&lt;Location&gt;|&lt;name-kleingeschrieben&gt;".</summary>
    public string Key { get; set; } = "";

    /// <summary>Anzeigename des Autostart-Eintrags (wie in der Registry/im Ordner).</summary>
    public string Name { get; set; } = "";

    /// <summary>Original-Befehl – erlaubt die Wiederherstellung ohne erneuten Registry-Scan.</summary>
    public string Command { get; set; } = "";

    /// <summary>Herkunft als <see cref="StartupLocation"/>-Name (String, damit JSON-stabil).</summary>
    public string Location { get; set; } = "";

    /// <summary>Verzögerung in Sekunden (&gt; 0; 0 bedeutet „keine Verzögerung" = kein Datensatz).</summary>
    public int DelaySeconds { get; set; }

    /// <summary>Voller Name der geplanten Aufgabe, z. B. <c>OpenClean Delayed\HkcuRun_Discord</c>.</summary>
    public string TaskName { get; set; } = "";
}
