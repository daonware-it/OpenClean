using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using OpenClean.ViewModels;

namespace OpenClean.Models;

/// <summary>
/// Art des Löschvorgangs für eine Kategorie.
/// </summary>
public enum CleanupKind
{
    /// <summary>Einzelne Dateien/Ordner löschen (Standard).</summary>
    FileDeletion,
    /// <summary>Papierkorb über die Shell-API leeren (keine direkte Dateilöschung).</summary>
    RecycleBin
}

/// <summary>
/// Eine Bereinigungskategorie (z. B. "Windows-Temp") mit ihren gefundenen Items.
/// </summary>
public sealed class CleanupCategory : ViewModelBase
{
    /// <summary>Basis-Lokalisierungsschlüssel (z. B. "cat.windowsTemp").</summary>
    public required string Key { get; init; }

    /// <summary>Lokalisierter Anzeigename ("&lt;Key&gt;.name").</summary>
    public string Name => Loc.T($"{Key}.name");

    /// <summary>Lokalisierte Beschreibung ("&lt;Key&gt;.description").</summary>
    public string Description => Loc.T($"{Key}.description");

    // ---- Optik (Icon-Chip, Größe, Objekte) ----------------------------------

    // Icon-Geometrie + Farbe je Kategorie-Key. Farbe ist der Hauptunterschied;
    // die Geometrie ist eine schlichte, gefüllte Vektorform (Ordner/Download/Globus/Bild/Papierkorb).
    private static readonly Dictionary<string, (string Icon, string Color)> Visuals = new()
    {
        ["cat.windowsTemp"] = ("M2 5 H9 L11 7 H22 V19 H2 Z", "#38BDF8"),
        ["cat.updateCache"] = ("M11 3 H13 V11 H16 L12 15 L8 11 H11 Z M5 18 H19 V21 H5 Z", "#A78BFA"),
        ["cat.thumbnails"]  = ("M3 5 H21 V19 H3 Z M3 15 L9 10 L13 14 L16 11 L21 16 V19 H3 Z", "#34D399"),
        ["cat.chrome"]      = ("M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z", "#F59E0B"),
        ["cat.edge"]        = ("M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z", "#22D3EE"),
        ["cat.brave"]       = ("M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z", "#FB923C"),
        ["cat.firefox"]     = ("M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z", "#FB7185"),
        ["cat.inetCache"]   = ("M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z", "#60A5FA"),
        ["cat.appCache"]    = ("M2 5 H9 L11 7 H22 V19 H2 Z", "#2DD4BF"),
        ["cat.recycleBin"]  = ("M5 6 H19 L18 21 H6 Z M9 3 H15 V6 H9 Z", "#F87171"),
    };

    private (string Icon, string Color) Visual =>
        Visuals.TryGetValue(Key, out var v) ? v : ("M2 5 H9 L11 7 H22 V19 H2 Z", "#8F94A8");

    private Geometry? _iconGeometry;
    private Brush? _chipForeground;
    private Brush? _chipBackground;

    /// <summary>Vektor-Icon der Kategorie (gefüllt).</summary>
    public Geometry IconGeometry => _iconGeometry ??= FrozenGeometry(Visual.Icon);

    /// <summary>Icon-Farbe (voll).</summary>
    public Brush ChipForeground => _chipForeground ??= FrozenBrush(Visual.Color, 0xFF);

    /// <summary>Chip-Hintergrund (dezent getönt).</summary>
    public Brush ChipBackground => _chipBackground ??= FrozenBrush(Visual.Color, 0x24);

    private ImageSource? _iconImage;
    /// <summary>
    /// Echtes Programm-Icon (z. B. Browser), falls verfügbar – wird dann statt der
    /// Vektor-Geometrie angezeigt.
    /// </summary>
    public ImageSource? IconImage
    {
        get => _iconImage;
        set
        {
            if (SetProperty(ref _iconImage, value))
                OnPropertyChanged(nameof(HasIconImage));
        }
    }

    /// <summary>True, wenn ein echtes Icon-Bild vorliegt (sonst Vektor-Chip).</summary>
    public bool HasIconImage => _iconImage is not null;

    /// <summary>Gesamtgröße der Kategorie als Text.</summary>
    public string SizeDisplay => Services.ByteFormatter.Format(TotalBytes);

    /// <summary>„N Objekte“ (lokalisiert).</summary>
    public string CountDisplay => $"{Items.Count:N0} {Loc.T("cleaner.objects")}";

    /// <summary>Anteil ausgewählter Bytes (0–100) – für den dünnen Balken rechts.</summary>
    public double SelectedRatioPercent => TotalBytes > 0 ? (double)SelectedBytes / TotalBytes * 100.0 : 0;

    /// <summary>Ob die Kategorie Administratorrechte benötigt (zeigt ein ADMIN-Badge).</summary>
    public bool RequiresAdmin => Key == "cat.updateCache";

    private static Geometry FrozenGeometry(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static Brush FrozenBrush(string hex, byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public CleanupKind Kind { get; init; } = CleanupKind.FileDeletion;

    /// <summary>Ob diese Kategorie überhaupt gescannt werden soll.</summary>
    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public ObservableCollection<ScanItem> Items { get; } = new();

    /// <summary>
    /// Nach Unterkategorien gruppierte Sicht auf <see cref="Items"/> (dieselben Instanzen).
    /// Wird nach jedem Scan über <see cref="RebuildSubcategories"/> neu aufgebaut.
    /// </summary>
    public ObservableCollection<CleanupSubcategory> Subcategories { get; } = new();

    /// <summary>
    /// Baut <see cref="Subcategories"/> aus den aktuellen <see cref="Items"/> neu auf,
    /// gruppiert nach <see cref="ScanItem.SubKey"/> (Reihenfolge des ersten Auftretens).
    /// Items ohne SubKey landen in einer generischen Gruppe.
    /// </summary>
    public void RebuildSubcategories()
    {
        Subcategories.Clear();

        const string generic = "\0generic"; // Sentinel, da Dictionary keinen null-Key erlaubt
        var order = new System.Collections.Generic.List<string>();
        var map = new System.Collections.Generic.Dictionary<string, CleanupSubcategory>();

        foreach (var item in Items)
        {
            string bucket = item.SubKey ?? generic;
            if (!map.TryGetValue(bucket, out var sub))
            {
                sub = new CleanupSubcategory { Key = item.SubKey, Changed = RefreshTotals };
                map[bucket] = sub;
                order.Add(bucket);
            }
            sub.Items.Add(item);
        }

        foreach (var bucket in order)
        {
            map[bucket].RefreshTotals();
            Subcategories.Add(map[bucket]);
        }
    }

    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
    public int SelectedCount => Items.Count(i => i.IsSelected);

    public string HeaderDisplay =>
        Items.Count == 0
            ? Name
            : Loc.T("cleaner.header.withItems",
                Name, SelectedCount, Items.Count, Services.ByteFormatter.Format(SelectedBytes));

    /// <summary>Tri-State-Auswahl auf Kategorie-Ebene (für die "Alle"-Checkbox).</summary>
    public bool? AllSelected
    {
        get
        {
            if (Items.Count == 0) return false;
            int sel = SelectedCount;
            if (sel == 0) return false;
            if (sel == Items.Count) return true;
            return null;
        }
        set
        {
            // Bulk-Operation: pro-Item-Callbacks unterdrücken (sonst O(n²)-Benachrichtigungen),
            // danach genau EINE Gesamt-Aktualisierung.
            bool target = value ?? false;
            foreach (var item in Items)
            {
                var callback = item.SelectionChanged;
                item.SelectionChanged = null;
                try { item.IsSelected = target; }
                finally { item.SelectionChanged = callback; }
            }
            RefreshTotals();
        }
    }

    public void Reset()
    {
        Items.Clear();
        Subcategories.Clear();
    }

    public void RefreshTotals()
    {
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HeaderDisplay));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(CountDisplay));
        OnPropertyChanged(nameof(SelectedRatioPercent));
        // Unterkategorien-Header/Tri-State mitziehen (dieselben Items).
        foreach (var sub in Subcategories) sub.RefreshTotals();
    }

    /// <summary>Aktualisiert Name/Beschreibung/Kopfzeile nach einem Sprachwechsel.</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HeaderDisplay));
        foreach (var sub in Subcategories) sub.RefreshLabels();
    }
}
