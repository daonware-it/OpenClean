using System.Collections.ObjectModel;
using System.Text;
using OpenClean.Services;
using OpenClean.Services.DeepClean;
using OpenClean.Services.Integrity;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>
/// Steuert die Tiefen-System-Bereinigung: Analysieren → Bereiche auswählen → bestätigen →
/// bereinigen. Es wird nie etwas angefasst, bevor analysiert und ausdrücklich bestätigt wurde.
///
/// <para>Anders als die normale Bereinigung gibt es hier KEIN Datei-Backup: die Bereiche sind
/// zweistellige Gigabyte groß, eine Sicherungskopie würde die Platte sprengen statt sie zu
/// entlasten. Das Sicherheitsnetz ist deshalb der Wiederherstellungspunkt – der passende
/// Mechanismus für Änderungen dieser Tiefe.</para>
/// </summary>
public sealed class DeepCleanViewModel : ScanViewModelBase
{
    private readonly DeepCleanService _service = DeepCleanService.Instance;
    private readonly IDialogService _dialogs;

    private bool _hasAnalyzed;
    private string _statusText = Loc.T("deepclean.status.ready");
    private string _progressText = "";
    private string _etaText = "";
    private double _progressPercent;
    private bool _progressIsIndeterminate = true;

    /// <summary>
    /// Zählt jeden Bereichswechsel mit und entwertet damit die Fortschritts-Rückmeldungen des
    /// vorigen Bereichs (siehe <see cref="CreateAreaProgress"/>).
    /// </summary>
    private int _areaSequence;

    /// <summary>
    /// Die Zeile „Bereinige Bereich 1/2: … " des laufenden Bereichs. Gemerkt, weil die Messphasen
    /// sie vorübergehend durch ihren eigenen Text ersetzen und danach wiederherstellen müssen.
    /// </summary>
    private string _areaStepText = "";
    private string _lastReportText = "";
    private bool _hasReport;

    public ObservableCollection<DeepCleanAreaViewModel> Areas { get; } = new();

    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }

    public DeepCleanViewModel(IDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? DialogService.Default;

        foreach (DeepCleanAreaKind kind in DeepCleanAreas.All)
        {
            var area = new DeepCleanAreaViewModel(kind) { SelectionChanged = RefreshSelectionState };
            Areas.Add(area);
        }

        AnalyzeCommand = new AsyncRelayCommand(_ => AnalyzeAsync(), _ => !IsBusy);
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => CanClean);
        SelectAllCommand = new RelayCommand(_ => SetAllSelection(true), _ => CanChangeSelection);
        DeselectAllCommand = new RelayCommand(_ => SetAllSelection(false), _ => CanChangeSelection);

        RegisterBusyCommands(AnalyzeCommand, CleanCommand, SelectAllCommand, DeselectAllCommand);
    }

    /// <summary>Neben den Commands hängen auch diese abgeleiteten Zustände am Busy-Flag.</summary>
    protected override void OnBusyChanged()
    {
        OnPropertyChanged(nameof(CanChangeSelection));
        OnPropertyChanged(nameof(CanClean));
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Text unter der Fortschrittsleiste, z. B. „Analysiere Bereich 3/5: Komponentenspeicher …".</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>
    /// Gesamtfortschritt über alle Bereiche, 0–100. Setzt sich zusammen aus den fertigen
    /// Bereichen (echte Zahl: N von 5) und – sofern der laufende Bereich einen meldet – seinem
    /// Detailfortschritt innerhalb seines Anteils.
    /// </summary>
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    /// <summary>
    /// True, solange der laufende Bereich noch KEINEN Fortschritt gemeldet hat. Nur der
    /// Komponentenspeicher meldet einen (DISM-Balken); die Ordner-Bereiche und vssadmin können
    /// es nicht, dort bleibt es beim laufenden Balken statt bei einer erfundenen Zahl.
    /// </summary>
    public bool ProgressIsIndeterminate
    {
        get => _progressIsIndeterminate;
        private set => SetProperty(ref _progressIsIndeterminate, value);
    }

    /// <summary>Geschätzte Restzeit des laufenden Bereichs, z. B. „ca. 00:42 verbleibend".</summary>
    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    /// <summary>Ergebnis-Banner nach dem Bereinigen.</summary>
    public string LastReportText
    {
        get => _lastReportText;
        private set => SetProperty(ref _lastReportText, value);
    }

    public bool HasReport
    {
        get => _hasReport;
        private set => SetProperty(ref _hasReport, value);
    }

    /// <summary>
    /// True, wenn die Integritätsprüfung angeschlagen hat (OPCL-20). Wie im Cleaner bleibt die
    /// Analyse nutzbar, das Bereinigen ist gesperrt; <see cref="DeepCleanService"/> sperrt zusätzlich selbst.
    /// </summary>
    public bool IsIntegrityBlocked => IntegrityState.IsBlocked;

    public bool CanChangeSelection => !IsBusy && Areas.Any(a => a.CanSelect);

    public bool CanClean => _hasAnalyzed && !IsBusy && SelectedCount > 0 && !IntegrityState.IsBlocked;

    public int SelectedCount => Areas.Count(a => a.IsSelected);

    public long TotalSelectedBytes => Areas.Where(a => a.IsSelected).Sum(a => a.SizeBytes);

    public string SelectionSummary => _hasAnalyzed
        ? Loc.T("deepclean.selection.summary", SelectedCount, ByteFormatter.Format(TotalSelectedBytes))
        : Loc.T("deepclean.msg.notAnalyzed");

    private void SetAllSelection(bool selected)
    {
        foreach (var area in Areas)
        {
            if (selected && !area.CanSelect) continue;
            area.IsSelected = selected;
        }

        RefreshSelectionState();
    }

    // ===================================================================================
    //  Analyse
    // ===================================================================================

    private async Task AnalyzeAsync()
    {
        await RunCancellableAsync(async ct =>
        {
            _hasAnalyzed = false;
            HasReport = false;
            StatusText = Loc.T("deepclean.status.analyzing");
            BeginRun();

            for (int index = 0; index < Areas.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                var area = Areas[index];
                BeginArea(index, Areas.Count);
                _areaStepText = Loc.T("deepclean.progress.analyzingStep", index + 1, Areas.Count, area.Name);
                ProgressText = _areaStepText;
                area.IsBusy = true;
                try
                {
                    // Der Dienst kapselt seine Fehler selbst; ein Bereich kann die anderen
                    // nicht mit in den Abgrund reißen.
                    DeepCleanAnalysis analysis = await _service.AnalyzeAsync(
                        area.Kind, CreateAreaProgress(index, Areas.Count), ct);
                    area.Apply(analysis);
                }
                finally
                {
                    area.IsBusy = false;
                    EndArea(index, Areas.Count);
                }
            }

            _hasAnalyzed = true;
            StatusText = Areas.Any(a => a.CanSelect)
                ? Loc.T("deepclean.status.doneSelectable", ByteFormatter.Format(TotalReclaimableBytes))
                : Loc.T("deepclean.status.doneEmpty");
        }, onCancelled: () => StatusText = Loc.T("deepclean.status.canceled"));

        EndRun();
        RefreshSelectionState();
    }

    /// <summary>Summe aller freigebbaren Bytes (nicht nur der ausgewählten) – für die Statuszeile.</summary>
    private long TotalReclaimableBytes => Areas.Where(a => a.CanSelect).Sum(a => a.SizeBytes);

    // ===================================================================================
    //  Fortschritt
    // ===================================================================================

    /// <summary>Setzt die Anzeige auf den Start eines Laufs.</summary>
    private void BeginRun()
    {
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
        ProgressText = "";
        _areaStepText = "";
        EtaText = "";
    }

    /// <summary>Räumt die Fortschrittsanzeige nach einem Lauf ab.</summary>
    private void EndRun()
    {
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
        ProgressText = "";
        _areaStepText = "";
        EtaText = "";
    }

    /// <summary>
    /// Beginn eines Bereichs: Der Balken steht auf dem Sockel der bereits erledigten Bereiche –
    /// eine echte Zahl. Bis der Bereich selbst etwas meldet, läuft er unbestimmt; die meisten
    /// Bereiche melden nie etwas (siehe <see cref="ProgressIsIndeterminate"/>).
    /// </summary>
    private void BeginArea(int index, int count)
    {
        _areaSequence++;
        ProgressPercent = AreaBase(index, count);
        ProgressIsIndeterminate = true;
        EtaText = "";
    }

    /// <summary>
    /// Ende eines Bereichs: Sein Anteil ist vollständig erledigt. Das Weiterzählen von
    /// <see cref="_areaSequence"/> entwertet dabei die Rückmeldungen dieses Bereichs.
    /// </summary>
    private void EndArea(int index, int count)
    {
        _areaSequence++;
        ProgressPercent = AreaBase(index + 1, count);
        ProgressIsIndeterminate = true;
        EtaText = "";
    }

    /// <summary>Anteil der ersten <paramref name="index"/> Bereiche am Gesamtfortschritt.</summary>
    private static double AreaBase(int index, int count) => count <= 0 ? 0 : 100.0 * index / count;

    /// <summary>
    /// Fortschritts-Rückmeldung für einen Bereich. Bewusst hier auf dem UI-Thread erzeugt:
    /// <see cref="Progress{T}"/> marshallt die Rückrufe damit selbst dorthin – dieselbe Naht wie
    /// im Cleaner.
    ///
    /// <para><b>Verspätete Rückmeldungen werden verworfen.</b> <see cref="Progress{T}"/> stellt
    /// die Rückrufe über den Dispatcher zu; die letzten eines Bereichs können daher erst
    /// eintreffen, wenn er längst abgeschlossen ist. Ohne den Zähler zöge so ein Nachzügler den
    /// Balken wieder auf den vorigen Bereich zurück – genau das Zurücklaufen, das hier nirgends
    /// passieren darf.</para>
    /// </summary>
    private IProgress<DeepCleanProgress> CreateAreaProgress(int index, int count)
    {
        int token = _areaSequence;

        return new Progress<DeepCleanProgress>(p =>
        {
            if (token != _areaSequence) return;
            ApplyProgress(p, index, count);
        });
    }

    /// <summary>
    /// Überträgt eine Meldung des Dienstes in die Anzeige.
    ///
    /// <para>Die Messphasen sind der Grund, warum <see cref="ProgressIsIndeterminate"/> hier
    /// wieder auf true gehen kann: Nach dem letzten 100%-Balken der Bereinigung folgt noch ein
    /// kompletter DISM-Messlauf (nachgemessen 21–33 s). Bliebe der Balken bestimmt auf 100 %
    /// stehen, behauptete die Anzeige „fertig", während noch gemessen wird – genau der Befund aus
    /// dem Betrieb.</para>
    ///
    /// <para>Bewusst <c>internal</c> statt <c>private</c>: Das hier ist die Regel, die der
    /// Fehlerbericht getroffen hat, und sie gehört geprüft. Über die öffentlichen Befehle wäre sie
    /// nicht erreichbar – die hängen am echten <see cref="DeepCleanService"/> und damit am
    /// laufenden System.</para>
    /// </summary>
    internal void ApplyProgress(DeepCleanProgress p, int index, int count)
    {
        ProgressPercent = Advance(AreaBase(index, count) + p.Percent / count);

        if (p.Phase == DeepCleanPhase.Working)
        {
            // Erst jetzt ist erwiesen, dass dieser Bereich einen Fortschritt kennt.
            ProgressIsIndeterminate = false;
            ProgressText = _areaStepText;
            EtaText = FormatEta(p.Eta);
            return;
        }

        // Messphase: Es gibt keinen bezifferbaren Fortschritt und damit auch keine Restzeit.
        // Eine stehengelassene Zahl („ca. 00:00 verbleibend") wäre hier die dreisteste Variante.
        ProgressIsIndeterminate = true;
        EtaText = "";
        ProgressText = Loc.T(p.Phase == DeepCleanPhase.MeasuringBefore
            ? "deepclean.progress.measuringBefore"
            : "deepclean.progress.measuringAfter");
    }

    /// <summary>
    /// Lässt den Balken innerhalb eines Bereichs nie zurücklaufen. Nötig, weil jede Phase ihren
    /// eigenen Zähler mitbringt: Die Nachmessung meldet ihren Sockel, nicht die 100 % der
    /// Bereinigung davor.
    /// </summary>
    private double Advance(double target) => target < ProgressPercent ? ProgressPercent : target;

    /// <summary>
    /// Übersetzt eine Restzeit-Schätzung in Anzeigetext. Steht der Fortschritt, gibt es bewusst
    /// „noch einige Zeit" statt einer Zahl, die niemand halten kann.
    /// </summary>
    private static string FormatEta(DeepCleanEta eta) => eta.Kind switch
    {
        DeepCleanEtaKind.Value => Loc.T("deepclean.eta.remaining", FormatDuration(eta.Remaining)),
        DeepCleanEtaKind.Unknown => Loc.T("deepclean.eta.unknown"),
        _ => ""
    };

    /// <summary>Dauer als „mm:ss" bzw. „h:mm:ss" – wie im Cleaner.</summary>
    private static string FormatDuration(TimeSpan remaining)
        => remaining.TotalHours >= 1
            ? remaining.ToString(@"h\:mm\:ss")
            : remaining.ToString(@"mm\:ss");

    // ===================================================================================
    //  Bereinigung
    // ===================================================================================

    private async Task CleanAsync()
    {
        var selected = Areas.Where(a => a.IsSelected && a.CanSelect).ToList();
        if (selected.Count == 0) return;

        if (!_dialogs.ConfirmThemed(
                BuildConfirmMessage(selected),
                Loc.T("deepclean.confirm.title"),
                Loc.T("deepclean.confirm.action")))
        {
            return;
        }

        // cleaned bleibt false, wenn am Wiederherstellungspunkt-Gate abgebrochen wird – dann läuft
        // (wie bisher) KEIN Report und KEIN RefreshSelectionState, nur EndRun.
        bool cleaned = false;

        await RunCancellableAsync(async ct =>
        {
            HasReport = false;
            StatusText = Loc.T("deepclean.status.cleaning");
            BeginRun();

            // Wiederherstellungspunkt als Sicherheitsnetz – aber nur, wenn ein Bereich dabei ist, der
            // davon profitiert. Für die Schattenkopien wäre er sinnlos bis schädlich: er belegt Platz
            // und wäre danach ausgerechnet der eine Punkt, den die Bereinigung stehen lässt.
            if (selected.Any(a => a.Kind != DeepCleanAreaKind.ShadowCopies))
            {
                if (!await SafetyPrompt.EnsureRestorePointAsync(_dialogs, msg => ProgressText = msg))
                {
                    StatusText = Loc.T("safety.aborted");
                    return;
                }
            }

            cleaned = true;
            long totalFreed = 0;
            int succeeded = 0, failed = 0;

            try
            {
                for (int index = 0; index < selected.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();

                    var area = selected[index];
                    BeginArea(index, selected.Count);
                    _areaStepText = Loc.T("deepclean.progress.cleaningStep", index + 1, selected.Count, area.Name);
                    ProgressText = _areaStepText;
                    area.IsBusy = true;
                    try
                    {
                        DeepCleanOutcome outcome = await _service.CleanAsync(
                            area.Kind, CreateAreaProgress(index, selected.Count), ct);
                        area.Apply(outcome);

                        totalFreed += outcome.FreedBytes;
                        if (outcome.IsSuccess || outcome.Status == DeepCleanOutcomeStatus.Partial) succeeded++;
                        else failed++;
                    }
                    finally
                    {
                        area.IsBusy = false;
                        EndArea(index, selected.Count);
                    }
                }

                StatusText = Loc.T("deepclean.status.cleaned", ByteFormatter.Format(totalFreed));
            }
            finally
            {
                LastReportText = failed > 0
                    ? Loc.T("deepclean.report.partial", ByteFormatter.Format(totalFreed), failed)
                    : Loc.T("deepclean.report", ByteFormatter.Format(totalFreed));
                HasReport = succeeded > 0 || failed > 0;
            }
        }, onCancelled: () => StatusText = Loc.T("deepclean.status.canceled"));

        EndRun();
        if (cleaned) RefreshSelectionState();
    }

    /// <summary>
    /// Baut den Text des Bestätigungsdialogs: was passiert, wie viel es bringt und – je Bereich –
    /// welche Folge das hat. Die Warnungen stehen bewusst ungekürzt darin: „Windows.old löschen"
    /// heißt, dass die Rückkehr zur vorherigen Windows-Version danach unmöglich ist.
    /// </summary>
    private static string BuildConfirmMessage(IReadOnlyList<DeepCleanAreaViewModel> selected)
    {
        long total = selected.Sum(a => a.SizeBytes);

        var text = new StringBuilder();
        text.AppendLine(Loc.T("deepclean.confirm.intro", selected.Count, ByteFormatter.Format(total)));
        text.AppendLine();

        foreach (var area in selected)
        {
            text.Append("• ").Append(area.Name).Append(" (").Append(area.SizeDisplay).AppendLine(")");
            text.Append("   ").AppendLine(area.ConfirmText);
        }

        text.AppendLine();
        text.Append(Loc.T("deepclean.confirm.irreversible"));
        return text.ToString();
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalSelectedBytes));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(CanChangeSelection));
        CleanCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        DeselectAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Aktualisiert nach einem Sprachwechsel alle berechneten Texte.</summary>
    public void Relocalize()
    {
        foreach (var area in Areas)
            area.Relocalize();

        OnPropertyChanged(nameof(SelectionSummary));

        if (!IsBusy && !_hasAnalyzed)
            StatusText = Loc.T("deepclean.status.ready");
    }
}
