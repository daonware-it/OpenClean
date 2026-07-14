namespace OpenClean.Models;

/// <summary>
/// Ein Ordner im Belegungsbaum des Sunbursts: kennt seine Gesamtgröße (inklusive aller
/// Unterordner) und seine direkten Kinder.
///
/// <para><see cref="IsPartial"/> markiert einen Knoten, dessen Unterordner beim Scan wegen
/// der Tiefenbegrenzung nicht einzeln erfasst wurden – ihre Bytes stecken zwar in
/// <see cref="TotalBytes"/>, aber <see cref="Children"/> ist leer. Beim Drill-down wird
/// so ein Knoten nachgeladen.</para>
/// </summary>
public sealed class FolderNode
{
    public FolderNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    /// <summary>Anzeigename (letztes Pfadsegment), z. B. „Downloads".</summary>
    public string Name { get; }

    /// <summary>Vollständiger Pfad, z. B. „C:\Users\Oliver\Downloads".</summary>
    public string FullPath { get; }

    /// <summary>Gesamtgröße inklusive aller Unterordner.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Direkte Unterordner (nur bis zur Scan-Tiefe befüllt).</summary>
    public List<FolderNode> Children { get; } = new();

    /// <summary>Elternknoten; null bei der Wurzel. Für die Breadcrumb-Navigation.</summary>
    public FolderNode? Parent { get; set; }

    /// <summary>True, wenn der Teilbaum wegen der Tiefenbegrenzung abgeschnitten wurde.</summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Ersetzt die Kinder – wird beim Nachladen eines <see cref="IsPartial"/>-Knotens benutzt.
    /// Setzt bei allen neuen Kindern den <see cref="Parent"/>-Verweis.
    /// </summary>
    public void ReplaceChildren(IEnumerable<FolderNode> children)
    {
        Children.Clear();
        foreach (var child in children)
        {
            child.Parent = this;
            Children.Add(child);
        }
        IsPartial = false;
    }
}
