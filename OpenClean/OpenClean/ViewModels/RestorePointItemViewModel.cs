using System.Globalization;
using OpenClean.Services;
using OpenClean.Services.Safety;

namespace OpenClean.ViewModels;

/// <summary>
/// Ein vorhandener Windows-Wiederherstellungspunkt in der Wiederherstellen-Ansicht. Reine Anzeige:
/// Ein Systemzustands-Rollback löst OpenClean bewusst NICHT selbst aus – dafür verweist die Ansicht
/// auf das Windows-eigene <c>rstrui.exe</c> (siehe <see cref="BackupHistoryViewModel.OpenSystemRestoreCommand"/>).
/// </summary>
public sealed class RestorePointItemViewModel : ViewModelBase
{
    private readonly RestorePointInfo _info;

    public RestorePointItemViewModel(RestorePointInfo info, RelayCommand delete)
    {
        _info = info;
        DeleteCommand = delete;
    }

    /// <summary>Sequenznummer, mit der Windows den Punkt führt (an <see cref="DeleteCommand"/> übergeben).</summary>
    public uint SequenceNumber => _info.SequenceNumber;

    /// <summary>Entfernt genau diesen Punkt.</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>Beschreibung des Punkts; leere Beschreibungen bekommen einen Ersatztext.</summary>
    public string Description => string.IsNullOrWhiteSpace(_info.Description)
        ? Loc.T("safety.points.noDescription")
        : _info.Description;

    /// <summary>Erstellzeitpunkt als lokale, gut lesbare Zeichenkette.</summary>
    public string CreatedText => _info.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Laufende Nummer, mit der Windows den Punkt führt (hilft beim Zuordnen in rstrui).</summary>
    public string SequenceText => Loc.T("safety.points.sequence", _info.SequenceNumber);

    /// <summary>Aktualisiert die berechneten Texte nach einem Sprachwechsel.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(SequenceText));
    }
}
