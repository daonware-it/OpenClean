using System;
using System.Collections.ObjectModel;
using System.Linq;
using OpenClean.ViewModels;

namespace OpenClean.Models;

/// <summary>
/// Eine Unterkategorie innerhalb einer <see cref="CleanupCategory"/> (z. B. „Cache" vs.
/// „Code-Cache" eines Browsers). Sie referenziert dieselben <see cref="ScanItem"/>-Instanzen
/// wie die Kategorie – die Kategorie bleibt die Wahrheitsquelle für Löschung/Summen; die
/// Unterkategorie ist nur eine Sicht mit eigener Auswahl-Checkbox.
/// </summary>
public sealed class CleanupSubcategory : ViewModelBase
{
    /// <summary>
    /// Basis-Lokalisierungsschlüssel (z. B. „sub.browserCache"). <c>null</c> = generische
    /// Gruppe („Dateien").
    /// </summary>
    public string? Key { get; init; }

    /// <summary>Lokalisierter Anzeigename.</summary>
    public string Name => Key is null ? Loc.T("sub.files.name") : Loc.T($"{Key}.name");

    public ObservableCollection<ScanItem> Items { get; } = new();

    /// <summary>Vom ViewModel gesetzt: aktualisiert Kategorie + Gesamtauswahl nach einer Änderung.</summary>
    public Action? Changed { get; set; }

    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
    public int SelectedCount => Items.Count(i => i.IsSelected);

    public string HeaderDisplay =>
        Loc.T("cleaner.subheader.withItems",
            Name, SelectedCount, Items.Count, Services.ByteFormatter.Format(SelectedBytes));

    /// <summary>Tri-State-Auswahl auf Unterkategorie-Ebene.</summary>
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
            // danach genau EINE Gesamt-Aktualisierung (Unterkategorie + Kategorie + VM).
            bool target = value ?? false;
            foreach (var item in Items)
            {
                var callback = item.SelectionChanged;
                item.SelectionChanged = null;
                try { item.IsSelected = target; }
                finally { item.SelectionChanged = callback; }
            }
            RefreshTotals();
            Changed?.Invoke();
        }
    }

    public void RefreshTotals()
    {
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HeaderDisplay));
        OnPropertyChanged(nameof(AllSelected));
    }

    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HeaderDisplay));
    }
}
