namespace OpenClean.Models;

/// <summary>
/// Der Ort im Explorer, an dem ein Kontextmenü-Eintrag erscheint.
/// </summary>
public enum ContextMenuLocation
{
    AllFiles,            // HKCR\*
    Directory,           // HKCR\Directory            (Rechtsklick auf einen Ordner)
    DirectoryBackground, // HKCR\Directory\Background (Rechtsklick auf die freie Fläche im Ordner)
    Folder,              // HKCR\Folder
    Drive,               // HKCR\Drive
    Desktop,             // HKCR\DesktopBackground
    RecycleBin           // HKCR\CLSID\{645FF040-…}
}

/// <summary>
/// Art des Eintrags: ein statisches Verb (Registry-Befehl) oder eine COM-Shell-Extension,
/// die der Explorer beim Öffnen des Menüs lädt.
/// </summary>
public enum ContextMenuEntryKind
{
    StaticVerb,
    ShellExtension
}

/// <summary>
/// Ein einzelner Eintrag des Windows-Rechtsklick-Menüs.
/// </summary>
public sealed class ContextMenuEntry
{
    /// <summary>Anzeigename (MUIVerb/Standardwert, sonst der Schlüsselname).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Alle Orte, an denen der Eintrag registriert ist. Derselbe Handler hängt oft an mehreren
    /// Orten (7-Zip z. B. an Dateien und Ordnern) – das ist EIN Eintrag, kein mehrfacher.
    /// </summary>
    public required IReadOnlyList<ContextMenuLocation> Locations { get; init; }

    public required ContextMenuEntryKind Kind { get; init; }

    /// <summary>
    /// HKCR-relative Pfade aller zugehörigen Schlüssel (je Ort einer, z. B. <c>*\shell\7-Zip</c>).
    /// Beim Abschalten eines Menü-Befehls werden alle davon stillgelegt.
    /// </summary>
    public required IReadOnlyList<string> RegistryPaths { get; init; }

    /// <summary>Nur bei <see cref="ContextMenuEntryKind.StaticVerb"/>: der hinterlegte Befehl.</summary>
    public string Command { get; init; } = "";

    /// <summary>Nur bei <see cref="ContextMenuEntryKind.ShellExtension"/>: die CLSID des Handlers.</summary>
    public string? Clsid { get; init; }

    /// <summary>Aufgelöste Handler-Datei (DLL der Extension bzw. EXE des Verbs); null, wenn unbekannt.</summary>
    public string? HandlerPath { get; init; }

    /// <summary>Verifizierter Authenticode-Herausgeber der Handler-Datei; leer, wenn nicht ermittelbar.</summary>
    public string Publisher { get; init; } = "";

    public bool IsEnabled { get; set; }

    /// <summary>
    /// False bei Windows-Kern-Verben (Öffnen, Kopieren, Umbenennen …). Diese lassen sich zwar
    /// technisch abschalten, würden den Explorer aber unbrauchbar machen – OpenClean sperrt sie.
    /// </summary>
    public bool CanToggle { get; init; } = true;

    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher)
        ? Loc.T("contextmenu.publisher.unknown")
        : Publisher;

    /// <summary>Alle Orte als Aufzählung („Dateien, Ordner, Laufwerke").</summary>
    public string LocationDisplay => string.Join(", ", Locations.Select(DisplayFor));

    /// <summary>Erster Registry-Pfad (Zweitzeile in der Liste); die übrigen stehen im Tooltip.</summary>
    public string RegistryPathDisplay => string.Join("   ·   ", RegistryPaths);

    /// <summary>Lokalisierte Bezeichnung eines Ortes (auch vom Ort-Filter der View genutzt).</summary>
    public static string DisplayFor(ContextMenuLocation location) => location switch
    {
        ContextMenuLocation.AllFiles => Loc.T("contextmenu.location.allFiles"),
        ContextMenuLocation.Directory => Loc.T("contextmenu.location.directory"),
        ContextMenuLocation.DirectoryBackground => Loc.T("contextmenu.location.background"),
        ContextMenuLocation.Folder => Loc.T("contextmenu.location.folder"),
        ContextMenuLocation.Drive => Loc.T("contextmenu.location.drive"),
        ContextMenuLocation.Desktop => Loc.T("contextmenu.location.desktop"),
        ContextMenuLocation.RecycleBin => Loc.T("contextmenu.location.recycleBin"),
        _ => location.ToString()
    };

    public string KindDisplay => Kind == ContextMenuEntryKind.ShellExtension
        ? Loc.T("contextmenu.kind.extension")
        : Loc.T("contextmenu.kind.verb");

    public string StatusDisplay => IsEnabled
        ? Loc.T("contextmenu.state.enabled")
        : Loc.T("contextmenu.state.disabled");
}
