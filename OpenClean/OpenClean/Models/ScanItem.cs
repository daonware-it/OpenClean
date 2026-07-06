using OpenClean.ViewModels;

namespace OpenClean.Models;

/// <summary>
/// Eine einzelne, zur Löschung vorgeschlagene Datei bzw. ein Ordner.
/// Der Scanner erzeugt diese Objekte – gelöscht wird nichts, bis der Nutzer bestätigt.
/// </summary>
public sealed class ScanItem : ViewModelBase
{
    private bool _isSelected = true;

    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    public bool IsDirectory { get; init; }

    /// <summary>Ob dieses Item beim Bereinigen gelöscht wird.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    /// <summary>Wird von der Kategorie abonniert, um die Summe live zu aktualisieren.</summary>
    public Action? SelectionChanged { get; set; }

    public string SizeDisplay => Services.ByteFormatter.Format(SizeBytes);
}
