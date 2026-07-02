using System.Collections.ObjectModel;
using OpenClean.Services.Privacy;
using OpenClean.ViewModels;

namespace OpenClean.Models;

/// <summary>
/// Eine Privatsphäre-Kategorie in der UI: kapselt einen <see cref="IPrivacyProvider"/>
/// und dessen gefundene <see cref="PrivacyItem"/>. Analog zu <see cref="CleanupCategory"/>.
/// </summary>
public sealed class PrivacyGroup : ViewModelBase
{
    public PrivacyGroup(IPrivacyProvider provider)
    {
        Provider = provider;
    }

    /// <summary>Der zugrunde liegende Provider (führt Scan/Clean aus).</summary>
    public IPrivacyProvider Provider { get; }

    public string Name => Provider.Name;
    public string Description => Provider.Description;
    public bool RequiresConfirmation => Provider.RequiresConfirmation;

    /// <summary>Ob diese Kategorie gescannt werden soll.</summary>
    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public ObservableCollection<PrivacyItem> Items { get; } = new();

    public int SelectedCount => Items.Count(i => i.IsSelected);

    public string HeaderDisplay =>
        Items.Count == 0
            ? Name
            : $"{Name} — {SelectedCount}/{Items.Count} ausgewählt";

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
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HeaderDisplay));
        OnPropertyChanged(nameof(AllSelected));
    }
}
