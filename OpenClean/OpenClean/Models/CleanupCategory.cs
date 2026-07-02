using System.Collections.ObjectModel;
using System.Linq;
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

    public CleanupKind Kind { get; init; } = CleanupKind.FileDeletion;

    /// <summary>Ob diese Kategorie überhaupt gescannt werden soll.</summary>
    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public ObservableCollection<ScanItem> Items { get; } = new();

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

    public void Reset() => Items.Clear();

    public void RefreshTotals()
    {
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HeaderDisplay));
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>Aktualisiert Name/Beschreibung/Kopfzeile nach einem Sprachwechsel.</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HeaderDisplay));
    }
}
