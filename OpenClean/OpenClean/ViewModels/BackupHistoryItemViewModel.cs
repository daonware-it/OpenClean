using System.Globalization;
using OpenClean.Models;
using OpenClean.Services;

namespace OpenClean.ViewModels;

/// <summary>
/// Ein einzelner gesicherter Bereinigungs-Durchlauf in der Wiederherstellen-Ansicht. Zeigt
/// Herkunftsbereich, Zeitpunkt, Anzahl und Größe und bietet Wiederherstellen/Löschen an.
/// </summary>
public sealed class BackupHistoryItemViewModel : ViewModelBase
{
    private readonly BackupManifest _manifest;

    public BackupHistoryItemViewModel(BackupManifest manifest, RelayCommand restore, RelayCommand delete)
    {
        _manifest = manifest;
        RestoreCommand = restore;
        DeleteCommand = delete;
    }

    /// <summary>Eindeutige Id des Durchlaufs (an die Commands übergeben).</summary>
    public string Id => _manifest.Id;

    /// <summary>Lokalisierter Herkunftsbereich (z. B. „Bereinigung", „Duplikate").</summary>
    public string AreaLabel => Loc.T("safety.area." + _manifest.Area);

    /// <summary>Erstellzeitpunkt als lokale, gut lesbare Zeichenkette.</summary>
    public string CreatedText
    {
        get
        {
            if (DateTime.TryParse(_manifest.CreatedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var utc))
                return utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            return _manifest.CreatedUtc;
        }
    }

    /// <summary>Zusammenfassung „N Objekte · X MB".</summary>
    public string Summary =>
        Loc.T("safety.history.summary", _manifest.Count, ByteFormatter.Format(_manifest.TotalBytes));

    public RelayCommand RestoreCommand { get; }
    public RelayCommand DeleteCommand { get; }

    /// <summary>Aktualisiert die berechneten Texte nach einem Sprachwechsel.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(AreaLabel));
        OnPropertyChanged(nameof(Summary));
    }
}
