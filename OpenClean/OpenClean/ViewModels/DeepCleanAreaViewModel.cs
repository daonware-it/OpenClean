using OpenClean.Services;
using OpenClean.Services.DeepClean;

namespace OpenClean.ViewModels;

/// <summary>
/// Eine Zeile der Tiefen-Bereinigung: ein Bereich mit seiner gemessenen Größe, seinem Zustand
/// und der Auswahl des Nutzers. Hält bewusst keine Logik – analysiert und bereinigt wird in
/// <see cref="DeepCleanViewModel"/>, damit alle fünf Zeilen identisch behandelt werden.
/// </summary>
public sealed class DeepCleanAreaViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isBusy;
    private long _sizeBytes;
    private long _occupiedBytes;
    private DeepCleanStatus _status = DeepCleanStatus.Unknown;
    private string _message = "";

    public DeepCleanAreaViewModel(DeepCleanAreaKind kind)
    {
        Kind = kind;
        _message = Loc.T("deepclean.msg.notAnalyzed");
    }

    /// <summary>Der Bereich, den diese Zeile darstellt.</summary>
    public DeepCleanAreaKind Kind { get; }

    /// <summary>Anzeigename des Bereichs.</summary>
    public string Name => Loc.T(DeepCleanAreas.NameKey(Kind));

    /// <summary>Kurzbeschreibung unter dem Namen.</summary>
    public string Description => Loc.T(DeepCleanAreas.DescriptionKey(Kind));

    /// <summary>Warnhinweis für den Bestätigungsdialog.</summary>
    public string ConfirmText => Loc.T(DeepCleanAreas.ConfirmKey(Kind));

    /// <summary>Vom Nutzer für die Bereinigung ausgewählt.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    /// <summary>Meldet dem übergeordneten ViewModel eine geänderte Auswahl.</summary>
    public Action? SelectionChanged { get; set; }

    /// <summary>True, während dieser Bereich gerade analysiert oder bereinigt wird.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Gemessene, freigebbare Größe in Bytes.</summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        private set
        {
            if (!SetProperty(ref _sizeBytes, value)) return;
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(OccupiedDisplay));
            OnPropertyChanged(nameof(HasOccupiedInfo));
        }
    }

    /// <summary>
    /// Belegter Speicher des Bereichs, soweit bekannt (0 = keine Auskunft). NICHT dasselbe wie
    /// <see cref="SizeBytes"/> – siehe <see cref="OccupiedDisplay"/>.
    /// </summary>
    public long OccupiedBytes
    {
        get => _occupiedBytes;
        private set
        {
            if (!SetProperty(ref _occupiedBytes, value)) return;
            OnPropertyChanged(nameof(OccupiedDisplay));
            OnPropertyChanged(nameof(HasOccupiedInfo));
        }
    }

    /// <summary>
    /// Die FREIGEBBARE Größe als „1,2 GB" – die wichtigste Zahl der Zeile. Was sie bedeutet, sagt
    /// <see cref="SizeLabel"/> darüber; ein Wert ohne Beschriftung beantwortet sonst die falsche
    /// Frage („was liegt da" statt „was kann weg").
    /// </summary>
    public string SizeDisplay => _status switch
    {
        DeepCleanStatus.Ready => ByteFormatter.Format(SizeBytes),
        DeepCleanStatus.Empty => ByteFormatter.Format(0),
        // Unknown/Unavailable/AccessDenied/Failed: es GIBT hier keine Zahl. Der Strich steht für
        // sich allein nur so lange, wie SizeLabel danebensagt, warum – „–" trägt keine Auskunft.
        _ => "–"
    };

    /// <summary>
    /// Beschriftung über der Zahl: Sie sagt, WAS dort steht bzw. warum nichts dort steht. Ohne
    /// sie las sich „–" bei fehlender Windows.old wie ein Defekt und „0 B" bei den Schattenkopien
    /// wie „da liegt nichts" – obwohl vssadmin 7,98 GB belegt meldet.
    /// </summary>
    public string SizeLabel => _status switch
    {
        DeepCleanStatus.Ready or DeepCleanStatus.Empty => Loc.T("deepclean.size.reclaimableLabel"),
        DeepCleanStatus.Unavailable => Loc.T("deepclean.size.notPresentLabel"),
        DeepCleanStatus.AccessDenied or DeepCleanStatus.Failed => Loc.T("deepclean.size.unknownLabel"),
        // Noch nicht analysiert: Der Strich ist hier selbsterklärend, eine Beschriftung wäre Lärm.
        _ => ""
    };

    /// <summary>True, wenn <see cref="SizeLabel"/> etwas zu sagen hat.</summary>
    public bool HasSizeLabel => SizeLabel.Length > 0;

    /// <summary>
    /// Der BELEGTE Platz als Zusatzzeile („7,98 GB belegt"). Erscheint nur, wenn er etwas
    /// beiträgt: Ist er gleich der freigebbaren Größe (alle Ordner-Bereiche), stünde dieselbe
    /// Zahl zweimal da; ist er unbekannt (0), gäbe es nichts zu sagen.
    /// </summary>
    public string OccupiedDisplay => HasOccupiedInfo
        ? Loc.T("deepclean.size.occupied", ByteFormatter.Format(OccupiedBytes))
        : "";

    /// <summary>True, wenn der belegte Platz bekannt ist UND eine andere Aussage trägt als die freigebbare Größe.</summary>
    public bool HasOccupiedInfo => OccupiedBytes > 0 && OccupiedBytes != SizeBytes;

    /// <summary>Zustand nach der Analyse.</summary>
    public DeepCleanStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(SizeLabel));
            OnPropertyChanged(nameof(HasSizeLabel));
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(HasProblem));
        }
    }

    /// <summary>Detailmeldung (fertig übersetzt) neben der Größe.</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>Nur analysierte Bereiche mit Inhalt lassen sich auswählen.</summary>
    public bool CanSelect => _status == DeepCleanStatus.Ready;

    /// <summary>True, wenn die Meldung ein Problem beschreibt (Anzeige in Warnfarbe).</summary>
    public bool HasProblem => _status is DeepCleanStatus.Failed or DeepCleanStatus.AccessDenied;

    /// <summary>Übernimmt ein Analyse-Ergebnis in die Anzeige.</summary>
    public void Apply(DeepCleanAnalysis analysis)
    {
        SizeBytes = analysis.SizeBytes;
        OccupiedBytes = analysis.OccupiedBytes;
        Status = analysis.Status;
        Message = analysis.Message;

        // Ein nicht mehr bereinigbarer Bereich darf nicht ausgewählt bleiben – sonst zählte die
        // Zusammenfassung Bytes mit, die es gar nicht mehr gibt.
        if (!CanSelect && IsSelected)
            IsSelected = false;

        OnPropertyChanged(nameof(SizeDisplay));
    }

    /// <summary>Übernimmt ein Bereinigungs-Ergebnis: Bereich gilt danach als geleert.</summary>
    public void Apply(DeepCleanOutcome outcome)
    {
        Message = outcome.Message;

        if (outcome.IsSuccess)
        {
            SizeBytes = 0;
            // Der belegte Platz von VOR der Bereinigung stimmt jetzt nicht mehr; ihn stehen zu
            // lassen hieße, eine veraltete Zahl als aktuelle Auskunft auszugeben.
            OccupiedBytes = 0;
            Status = DeepCleanStatus.Empty;
            IsSelected = false;
            return;
        }

        // Teilerfolg/Fehler: Größe stimmt nicht mehr, ehrlich als „unbekannt" zeigen und zur
        // erneuten Analyse zwingen, statt eine veraltete Zahl stehen zu lassen.
        OccupiedBytes = 0;
        Status = outcome.Status == DeepCleanOutcomeStatus.Canceled
            ? DeepCleanStatus.Unknown
            : DeepCleanStatus.Failed;
        IsSelected = false;
    }

    /// <summary>Setzt die Zeile auf „noch nicht analysiert" zurück.</summary>
    public void Reset()
    {
        SizeBytes = 0;
        OccupiedBytes = 0;
        Status = DeepCleanStatus.Unknown;
        Message = Loc.T("deepclean.msg.notAnalyzed");
        IsSelected = false;
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel die berechneten Texte.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(HasSizeLabel));
        OnPropertyChanged(nameof(OccupiedDisplay));

        if (Status == DeepCleanStatus.Unknown)
            Message = Loc.T("deepclean.msg.notAnalyzed");
    }
}
