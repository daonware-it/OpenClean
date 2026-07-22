using System.Runtime.Versioning;
using OpenClean.Services.Integrity;

namespace OpenClean.Services.DeepClean;

/// <summary>
/// Analysiert und bereinigt die fünf tiefen Systembereiche (Windows.old, Komponentenspeicher,
/// Schattenkopien, Update-Zwischenspeicher, Übermittlungsoptimierung).
///
/// <para><b>Aufbau:</b> Alles, was das echte System berührt, läuft über <see cref="IProcessRunner"/>
/// und <see cref="IDeepCleanEnvironment"/>. Dadurch ist die gesamte Entscheidungs- und
/// Auswertelogik ohne Administratorrechte und ohne Systemänderung testbar – nötig, weil diese
/// Bereinigungen die destruktivsten der ganzen Anwendung sind.</para>
///
/// <para><b>Freigewordener Speicher</b> wird nie geschätzt, sondern vor und nach der Aktion
/// gemessen und differenziert. Eine gemeldete Zahl entspricht damit immer der Realität, auch
/// wenn ein Werkzeug Erfolg meldet, aber nichts getan hat.</para>
///
/// <para><b>Bestätigung</b> ist NICHT Aufgabe dieses Dienstes – die holt das ViewModel über
/// <c>ConfirmDialog</c> ein, bevor es hier überhaupt etwas aufruft. Die Sperren, die hier liegen
/// (Integrität, Rechte), sind die letzte Verteidigungslinie, kein Ersatz dafür.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeepCleanService
{
    /// <summary>Prozessweiter Singleton, wie die übrigen Dienste.</summary>
    public static DeepCleanService Instance { get; } = new();

    private readonly IProcessRunner _runner;
    private readonly IDeepCleanEnvironment _env;

    /// <summary>Erzeugt den Dienst gegen das echte System.</summary>
    public DeepCleanService() : this(new ProcessRunner(), new WindowsDeepCleanEnvironment()) { }

    /// <summary>Test-Konstruktor: erlaubt Prozess- und Umgebungs-Attrappen.</summary>
    internal DeepCleanService(IProcessRunner runner, IDeepCleanEnvironment env)
    {
        _runner = runner;
        _env = env;
    }

    // Zeitlimits. DISM-Komponentenbereinigung kann auf langsamen Platten sehr lange laufen –
    // ein zu knappes Limit würde einen halb fertigen Komponentenspeicher hinterlassen.
    private static readonly TimeSpan DismAnalyzeTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DismCleanupTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan VssTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FileToolTimeout = TimeSpan.FromMinutes(60);

    // DISM meldet 3010 = Erfolg, Neustart erforderlich. Das ist kein Fehler.
    private const int ExitRebootRequired = 3010;

    // sc.exe: Dienst läuft bereits / Dienst wurde nicht gestartet. Beides ist für uns Erfolg.
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    /// <summary>
    /// Bekannte SID der lokalen Administratoren (VORDEFINIERT\Administratoren). Als SID statt als
    /// Name, weil der Gruppenname auf jeder Windows-Systemsprache anders lautet.
    /// </summary>
    private const string AdministratorsSid = "*S-1-5-32-544";

    // ===================================================================================
    //  Analyse
    // ===================================================================================

    /// <summary>
    /// Ermittelt den belegten Speicher eines Bereichs. Rein lesend – ändert nie etwas.
    /// </summary>
    public Task<DeepCleanAnalysis> AnalyzeAsync(DeepCleanAreaKind kind, CancellationToken ct = default)
        => AnalyzeAsync(kind, progress: null, ct);

    /// <summary>
    /// Wie <see cref="AnalyzeAsync(DeepCleanAreaKind, CancellationToken)"/>, meldet aber den
    /// Fortschritt innerhalb des Bereichs.
    ///
    /// <para><b>Nur der Komponentenspeicher meldet echten Fortschritt</b> – DISM schreibt ihn als
    /// Balken auf die Standardausgabe. <c>vssadmin</c> und <c>sc</c> sind in Sekunden fertig, und
    /// eine Ordnergröße lässt sich nicht in Prozent ausdrücken, ohne vorher schon einmal gezählt
    /// zu haben. Für diese Bereiche bleibt <paramref name="progress"/> deshalb bewusst STUMM: Der
    /// Aufrufer erkennt daran, dass er nichts Bezifferbares anzuzeigen hat, statt eine erfundene
    /// Zahl zu bekommen.</para>
    /// </summary>
    public async Task<DeepCleanAnalysis> AnalyzeAsync(DeepCleanAreaKind kind,
        IProgress<DeepCleanProgress>? progress, CancellationToken ct)
    {
        // Ohne Rechte ist jede der fünf Messungen entweder unmöglich oder gelogen (unlesbare
        // Zweige zählen als 0). Ehrlich melden statt eine zu kleine Zahl anzuzeigen.
        if (!_env.IsElevated)
            return new DeepCleanAnalysis(kind, 0, 0, DeepCleanStatus.AccessDenied, Loc.T("deepclean.error.admin"));

        try
        {
            return kind switch
            {
                DeepCleanAreaKind.WindowsOld => AnalyzeWindowsOld(ct),
                DeepCleanAreaKind.ComponentStore => await AnalyzeComponentStoreAsync(progress, ct).ConfigureAwait(false),
                DeepCleanAreaKind.ShadowCopies => await AnalyzeShadowCopiesAsync(ct).ConfigureAwait(false),
                DeepCleanAreaKind.UpdateCache => AnalyzeFolder(kind, _env.UpdateCachePath, ct),
                DeepCleanAreaKind.DeliveryOptimization => AnalyzeFolder(kind, _env.DeliveryOptimizationPath, ct),
                _ => new DeepCleanAnalysis(kind, 0, 0, DeepCleanStatus.Failed, Loc.T("deepclean.error.generic"))
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Eine Analyse darf niemals die Anwendung mitreißen.
            return new DeepCleanAnalysis(kind, 0, 0, DeepCleanStatus.Failed, Loc.T("deepclean.error.generic"));
        }
    }

    private DeepCleanAnalysis AnalyzeWindowsOld(CancellationToken ct)
    {
        string path = _env.WindowsOldPath;
        if (!_env.DirectoryExists(path))
            return DeepCleanAnalysis.Unavailable(DeepCleanAreaKind.WindowsOld, Loc.T("deepclean.msg.noWindowsOld"));

        return AnalyzeFolder(DeepCleanAreaKind.WindowsOld, path, ct);
    }

    /// <summary>
    /// Gemeinsame Ordner-Analyse für Windows.old, Update-Cache und Übermittlungsoptimierung.
    ///
    /// <para>Ein FEHLENDER Ordner ist „nicht verfügbar", nicht „leer". Der Unterschied ist keine
    /// Wortklauberei: „leer" tarnt einen falsch ermittelten Pfad als „nichts zu tun" – genau so
    /// blieb unbemerkt, dass der Pfad der Übermittlungsoptimierung ins Leere zeigte.</para>
    /// </summary>
    private DeepCleanAnalysis AnalyzeFolder(DeepCleanAreaKind kind, string path, CancellationToken ct)
    {
        if (!_env.DirectoryExists(path))
            return DeepCleanAnalysis.Unavailable(kind, Loc.T("deepclean.msg.noFolder"));

        long size = _env.GetDirectorySize(path, ct);

        return size <= 0
            ? new DeepCleanAnalysis(kind, 0, 0, DeepCleanStatus.Empty, Loc.T("deepclean.msg.empty"))
            : new DeepCleanAnalysis(kind, size, 0, DeepCleanStatus.Ready,
                Loc.T("deepclean.msg.ready", ByteFormatter.Format(size)));
    }

    private async Task<DeepCleanAnalysis> AnalyzeComponentStoreAsync(
        IProgress<DeepCleanProgress>? progress, CancellationToken ct)
    {
        ProcessResult result = await RunDismAsync(
            new[] { "/English", "/Online", "/Cleanup-Image", "/AnalyzeComponentStore" },
            DismAnalyzeTimeout, progress, ct).ConfigureAwait(false);

        if (MapToolFailure(DeepCleanAreaKind.ComponentStore, result, "DISM") is { } failure)
            return failure;

        DismComponentStoreAnalysis parsed = DeepCleanParsers.ParseDismAnalyze(result.CombinedOutput);
        if (!parsed.IsValid)
            return new DeepCleanAnalysis(DeepCleanAreaKind.ComponentStore, 0, 0, DeepCleanStatus.Failed,
                Loc.T("deepclean.error.dismOutput"));

        // Belegt ist der ganze Komponentenspeicher, freigebbar nur Backups + Cache. Nachgemessen
        // auf diesem System: 22,13 GB belegt, davon 14,19 GB freigebbar – „Shared with Windows"
        // (7,93 GB) gehört fest zum Betriebssystem. Ohne die belegte Zahl daneben liest sich ein
        // aufgeräumter Speicher als „0 B" und damit als Fehler.
        if (parsed.ReclaimableBytes <= 0)
            return new DeepCleanAnalysis(DeepCleanAreaKind.ComponentStore, 0, parsed.ReclaimablePackages,
                DeepCleanStatus.Empty, Loc.T("deepclean.msg.empty"), parsed.ActualSizeBytes);

        return new DeepCleanAnalysis(
            DeepCleanAreaKind.ComponentStore,
            parsed.ReclaimableBytes,
            parsed.ReclaimablePackages,
            DeepCleanStatus.Ready,
            Loc.T("deepclean.msg.readyPackages",
                ByteFormatter.Format(parsed.ReclaimableBytes), parsed.ReclaimablePackages),
            parsed.ActualSizeBytes);
    }

    private async Task<DeepCleanAnalysis> AnalyzeShadowCopiesAsync(CancellationToken ct)
    {
        long used = await ReadShadowStorageAsync(ct).ConfigureAwait(false);
        int points = _env.CountRestorePoints();

        // Der jüngste Punkt bleibt grundsätzlich stehen (siehe CleanShadowCopiesAsync). Bei
        // höchstens einem Punkt gibt es also nichts zu entfernen – unabhängig vom Platzverbrauch.
        //
        // Der belegte Platz wird trotzdem durchgereicht, und genau darauf kommt es an: Auf einem
        // echten System liegen hier 7,98 GB, freigebbar sind 0 B. Nur die 0 zu zeigen, wäre
        // formal richtig und trotzdem irreführend – die Spalte legt nahe, es läge dort nichts.
        if (points <= 1)
            return new DeepCleanAnalysis(DeepCleanAreaKind.ShadowCopies, 0, points, DeepCleanStatus.Empty,
                Loc.T("deepclean.msg.shadowNothing"), used);

        return new DeepCleanAnalysis(
            DeepCleanAreaKind.ShadowCopies,
            used,
            points,
            DeepCleanStatus.Ready,
            Loc.T("deepclean.msg.readyPoints", ByteFormatter.Format(used), points),
            used);
    }

    private async Task<long> ReadShadowStorageAsync(CancellationToken ct)
    {
        ProcessResult result = await _runner
            .RunAsync("vssadmin.exe", new[] { "list", "shadowstorage" }, VssTimeout, ct)
            .ConfigureAwait(false);

        // Kein Erfolg -> 0 belegt melden; die Punktzahl trägt die Anzeige dann allein.
        return result.Success ? DeepCleanParsers.ParseUsedShadowStorage(result.CombinedOutput) : 0;
    }

    // ===================================================================================
    //  Bereinigung
    // ===================================================================================

    /// <summary>
    /// Bereinigt einen Bereich. Der Aufrufer MUSS die Bestätigung des Nutzers vorher eingeholt
    /// haben. Meldet den tatsächlich (nicht geschätzt) freigewordenen Speicher.
    /// </summary>
    public Task<DeepCleanOutcome> CleanAsync(DeepCleanAreaKind kind, CancellationToken ct = default)
        => CleanAsync(kind, progress: null, ct);

    /// <summary>
    /// Wie <see cref="CleanAsync(DeepCleanAreaKind, CancellationToken)"/>, meldet aber den
    /// Fortschritt innerhalb des Bereichs. Auch hier gilt: Nur der Komponentenspeicher hat einen
    /// bezifferbaren Fortschritt, die übrigen Bereiche schweigen (siehe
    /// <see cref="AnalyzeAsync(DeepCleanAreaKind, IProgress{DeepCleanProgress}, CancellationToken)"/>).
    /// </summary>
    public async Task<DeepCleanOutcome> CleanAsync(DeepCleanAreaKind kind,
        IProgress<DeepCleanProgress>? progress, CancellationToken ct)
    {
        // Sperre bei erkannter Manipulation (OPCL-20) – dieselbe Klammer wie in CleanerService:
        // bewusst im Dienst und nicht nur im ViewModel, damit kein Aufrufpfad daran vorbeikommt.
        if (IntegrityState.IsBlocked)
            return new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Blocked, 0, Loc.T("integrity.blocked.summary"));

        if (!_env.IsElevated)
            return new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.AccessDenied, 0, Loc.T("deepclean.error.admin"));

        try
        {
            return kind switch
            {
                DeepCleanAreaKind.WindowsOld => await CleanWindowsOldAsync(ct).ConfigureAwait(false),
                DeepCleanAreaKind.ComponentStore => await CleanComponentStoreAsync(progress, ct).ConfigureAwait(false),
                DeepCleanAreaKind.ShadowCopies => await CleanShadowCopiesAsync(ct).ConfigureAwait(false),
                DeepCleanAreaKind.UpdateCache => await CleanServiceBackedFolderAsync(
                    DeepCleanAreaKind.UpdateCache, _env.UpdateCachePath,
                    new[] { "wuauserv", "bits" }, ct).ConfigureAwait(false),
                DeepCleanAreaKind.DeliveryOptimization => await CleanServiceBackedFolderAsync(
                    DeepCleanAreaKind.DeliveryOptimization, _env.DeliveryOptimizationPath,
                    new[] { "DoSvc" }, ct).ConfigureAwait(false),
                _ => new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Failed, 0, Loc.T("deepclean.error.generic"))
            };
        }
        catch (OperationCanceledException)
        {
            return new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Canceled, 0, Loc.T("deepclean.result.canceled"));
        }
        catch (Exception)
        {
            return new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Failed, 0, Loc.T("deepclean.error.generic"));
        }
    }

    /// <summary>
    /// Entfernt <c>C:\Windows.old</c>. Die Dateien gehören TrustedInstaller, ein schlichtes
    /// <c>Directory.Delete</c> scheitert daher fast überall an „Zugriff verweigert". Deshalb der
    /// Dreischritt Besitz übernehmen → Rechte setzen → löschen. Die ersten beiden Schritte dürfen
    /// scheitern (Teilerfolg ist möglich); maßgeblich ist allein, was danach noch auf der Platte
    /// liegt.
    ///
    /// <para><b>Warum kein takeown.exe:</b> dessen Schalter <c>/d</c> erwartet die LOKALISIERTE
    /// Bestätigung – auf deutschem Windows „J". Ein fest verdrahtetes <c>/d Y</c> scheitert dort
    /// mit „Ungültige Syntax", und in der Folge scheitern icacls (kein WRITE_DAC) und rd
    /// (Zugriff verweigert) gleich mit: Windows.old wurde auf jedem nicht-englischen System nie
    /// gelöscht. <c>icacls /setowner</c> kennt keine solche Rückfrage und ist damit
    /// sprachunabhängig.</para>
    /// </summary>
    private async Task<DeepCleanOutcome> CleanWindowsOldAsync(CancellationToken ct)
    {
        string path = _env.WindowsOldPath;
        if (!_env.DirectoryExists(path))
            return new DeepCleanOutcome(DeepCleanAreaKind.WindowsOld, DeepCleanOutcomeStatus.NothingToDo, 0,
                Loc.T("deepclean.msg.noWindowsOld"));

        long before = _env.GetDirectorySize(path, ct);

        // *S-1-5-32-544 ist die bekannte SID der lokalen Administratoren. Der Stern-Präfix sagt
        // icacls „das ist eine SID, kein Name" – deshalb funktioniert der Aufruf auf JEDER
        // Windows-Systemsprache. Niemals durch "Administratoren"/"Administrators" ersetzen: der
        // Gruppenname ist übersetzt und der Aufruf schlüge auf den meisten Systemen fehl.
        //
        // /l = Reparse-Punkten NICHT folgen. Windows.old enthält Junctions (z. B. „Dokumente und
        // Einstellungen"), die aus dem Ordner heraus zeigen können; ohne /l würde /t ihnen folgen
        // und außerhalb von Windows.old Besitz und Vollzugriff für Administratoren setzen.
        // Ausgewertet wird bewusst kein Ausgabetext, sondern nur die Größe danach – icacls'
        // Meldungen sind lokalisiert, die Bilanz auf der Platte nicht.
        await _runner.RunAsync("icacls.exe", new[] { path, "/setowner", AdministratorsSid, "/t", "/c", "/l", "/q" },
            FileToolTimeout, ct).ConfigureAwait(false);
        await _runner.RunAsync("icacls.exe", new[] { path, "/grant", AdministratorsSid + ":F", "/t", "/c", "/l", "/q" },
            FileToolTimeout, ct).ConfigureAwait(false);

        // rd ist ein cmd-Builtin, daher der Umweg über cmd.exe.
        await _runner.RunAsync("cmd.exe", new[] { "/c", "rd", "/s", "/q", path }, FileToolTimeout, ct)
            .ConfigureAwait(false);

        long after = _env.DirectoryExists(path) ? _env.GetDirectorySize(path, ct) : 0;
        return BuildFolderOutcome(DeepCleanAreaKind.WindowsOld, before, after);
    }

    private async Task<DeepCleanOutcome> CleanComponentStoreAsync(
        IProgress<DeepCleanProgress>? progress, CancellationToken ct)
    {
        // Ausgangsgröße aus DISM selbst; ein Verzeichnis-Scan über WinSxS wäre wegen der
        // Hardlinks bedeutungslos.
        //
        // Diese Messung ist ein EIGENER, vollwertiger DISM-Lauf – auf diesem System nachgemessen
        // 21,2 s bzw. 32,6 s. Sie muss deshalb angesagt werden: Ohne die Phase stünde darunter
        // eine halbe Minute lang „Bereinige …", obwohl noch gar nichts bereinigt wird.
        progress?.Report(new DeepCleanProgress(0, DeepCleanEta.None, DeepCleanPhase.MeasuringBefore));
        DismComponentStoreAnalysis before = await ReadDismAnalysisAsync(ct).ConfigureAwait(false);

        // Fortschritt hängt allein an der Bereinigung, nicht an den beiden Messungen drumherum.
        // Alle drei Läufe in EINEN Balken zu falten, hieße ihre Dauer gegeneinander zu gewichten –
        // und dieses Verhältnis ist nirgends gemessen. Erfundene Gewichte sind erfundene Prozente.
        ProcessResult result = await RunDismAsync(
            new[] { "/English", "/Online", "/Cleanup-Image", "/StartComponentCleanup" },
            DismCleanupTimeout, progress, ct).ConfigureAwait(false);

        if (!result.Success && result.ExitCode != ExitRebootRequired)
            return new DeepCleanOutcome(DeepCleanAreaKind.ComponentStore, DeepCleanOutcomeStatus.Failed, 0,
                DescribeToolFailure(result, "DISM"));

        // Der Grund für den Fehlerbericht aus dem Betrieb: Nach dem 100%-Balken der Bereinigung
        // folgt HIER noch ein kompletter Messlauf. DISM selbst ist nach seinen 100 % sofort fertig
        // (nachgemessen: 18 ms zwischen dem 100%-Balken und dem Prozessende) – die gefühlte
        // Hängepartie war allein diese Nachmessung, die niemand angesagt hat.
        progress?.Report(new DeepCleanProgress(100, DeepCleanEta.None, DeepCleanPhase.MeasuringAfter));
        DismComponentStoreAnalysis after = await ReadDismAnalysisAsync(ct).ConfigureAwait(false);

        long freed = 0;
        if (before.IsValid && after.IsValid && before.ActualSizeBytes > after.ActualSizeBytes)
            freed = before.ActualSizeBytes - after.ActualSizeBytes;

        if (freed <= 0)
            return new DeepCleanOutcome(DeepCleanAreaKind.ComponentStore, DeepCleanOutcomeStatus.NothingToDo, 0,
                Loc.T("deepclean.result.nothing"));

        return new DeepCleanOutcome(DeepCleanAreaKind.ComponentStore, DeepCleanOutcomeStatus.Success, freed,
            Loc.T("deepclean.result.success", ByteFormatter.Format(freed)));
    }

    private async Task<DismComponentStoreAnalysis> ReadDismAnalysisAsync(CancellationToken ct)
    {
        ProcessResult result = await RunDismAsync(
            new[] { "/English", "/Online", "/Cleanup-Image", "/AnalyzeComponentStore" },
            DismAnalyzeTimeout, ct).ConfigureAwait(false);

        return result.Success
            ? DeepCleanParsers.ParseDismAnalyze(result.CombinedOutput)
            : DismComponentStoreAnalysis.Invalid;
    }

    /// <summary>
    /// Entfernt alle Wiederherstellungspunkte bis auf den jüngsten. Der jüngste bleibt bewusst
    /// stehen – er ist in aller Regel das Sicherheitsnetz der letzten Bereinigung
    /// (siehe <c>RestorePointService.RemoveAllButNewest</c>).
    /// </summary>
    private async Task<DeepCleanOutcome> CleanShadowCopiesAsync(CancellationToken ct)
    {
        if (_env.CountRestorePoints() <= 1)
            return new DeepCleanOutcome(DeepCleanAreaKind.ShadowCopies, DeepCleanOutcomeStatus.NothingToDo, 0,
                Loc.T("deepclean.msg.shadowNothing"));

        long before = await ReadShadowStorageAsync(ct).ConfigureAwait(false);
        (int removed, int failed) = _env.RemoveOldRestorePoints();
        long after = await ReadShadowStorageAsync(ct).ConfigureAwait(false);

        long freed = before > after ? before - after : 0;

        if (removed == 0 && failed > 0)
            return new DeepCleanOutcome(DeepCleanAreaKind.ShadowCopies, DeepCleanOutcomeStatus.Failed, 0,
                Loc.T("deepclean.error.shadowRemove"));

        if (removed == 0)
            return new DeepCleanOutcome(DeepCleanAreaKind.ShadowCopies, DeepCleanOutcomeStatus.NothingToDo, 0,
                Loc.T("deepclean.result.nothing"));

        if (failed > 0)
            return new DeepCleanOutcome(DeepCleanAreaKind.ShadowCopies, DeepCleanOutcomeStatus.Partial, freed,
                Loc.T("deepclean.result.shadowPartial", removed, failed, ByteFormatter.Format(freed)));

        return new DeepCleanOutcome(DeepCleanAreaKind.ShadowCopies, DeepCleanOutcomeStatus.Success, freed,
            Loc.T("deepclean.result.shadow", removed, ByteFormatter.Format(freed)));
    }

    /// <summary>
    /// Gemeinsamer Ablauf für Update-Zwischenspeicher und Übermittlungsoptimierung:
    /// Dienste stoppen → Ordner leeren → Dienste wieder starten.
    ///
    /// <para>Der Neustart der Dienste läuft in einem <c>finally</c>: Bricht der Nutzer mitten im
    /// Leeren ab oder fliegt eine Ausnahme, dürfen Windows Update bzw. die Übermittlungs-
    /// optimierung nicht dauerhaft gestoppt zurückbleiben.</para>
    /// </summary>
    private async Task<DeepCleanOutcome> CleanServiceBackedFolderAsync(
        DeepCleanAreaKind kind, string path, IReadOnlyList<string> services, CancellationToken ct)
    {
        if (!_env.DirectoryExists(path))
            return new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.NothingToDo, 0, Loc.T("deepclean.result.nothing"));

        long before = _env.GetDirectorySize(path, ct);
        if (before <= 0)
            return new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.NothingToDo, 0, Loc.T("deepclean.result.nothing"));

        // Vorgemerkt wird VOR dem Stoppversuch, nicht nach einem erfolgreichen: `sc stop` ist
        // abgesetzt und wirkt weiter, sobald der Aufruf läuft. Ob wir danach noch mitbekommen,
        // dass der Dienst wirklich steht (Warten reicht nicht, Nutzer bricht ab, Ausnahme),
        // ändert daran nichts. Ein fälschlich zusätzlich gestarteter Dienst ist folgenlos
        // (sc start meldet dann 1056), ein gestoppt zurückgelassener nicht.
        var touched = new List<string>(services.Count);
        try
        {
            foreach (string service in services)
            {
                touched.Add(service);

                if (!await StopServiceAsync(service, ct).ConfigureAwait(false))
                    return new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Failed, 0,
                        Loc.T("deepclean.error.serviceStop", service));
            }

            _env.EmptyDirectory(path, ct);
        }
        finally
        {
            // Best effort, und bewusst ohne den Abbruch-Token: ein abgebrochener Lauf darf die
            // Dienste erst recht nicht gestoppt zurücklassen.
            foreach (string service in touched)
                await StartServiceAsync(service, CancellationToken.None).ConfigureAwait(false);
        }

        long after = _env.GetDirectorySize(path, ct);
        return BuildFolderOutcome(kind, before, after);
    }

    /// <summary>Übersetzt „vorher/nachher" in ein Ergebnis – die einzige Stelle, die freed berechnet.</summary>
    private static DeepCleanOutcome BuildFolderOutcome(DeepCleanAreaKind kind, long before, long after)
    {
        long freed = before > after ? before - after : 0;

        if (after <= 0)
        {
            return freed > 0
                ? new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Success, freed,
                    Loc.T("deepclean.result.success", ByteFormatter.Format(freed)))
                : new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.NothingToDo, 0, Loc.T("deepclean.result.nothing"));
        }

        // Es blieb etwas liegen: fast immer Dateien in Verwendung oder durch Rechte geschützt.
        return freed > 0
            ? new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Partial, freed,
                Loc.T("deepclean.result.partial", ByteFormatter.Format(freed), ByteFormatter.Format(after)))
            : new DeepCleanOutcome(kind, DeepCleanOutcomeStatus.Failed, 0, Loc.T("deepclean.error.inUse"));
    }

    // ===================================================================================
    //  Dienststeuerung (über sc.exe – kein zusätzliches NuGet-Paket nötig)
    // ===================================================================================

    /// <summary>
    /// Stoppt einen Dienst und wartet, bis er wirklich steht. <c>sc stop</c> kehrt sofort zurück –
    /// würde direkt danach gelöscht, wären die Dateien noch offen.
    /// </summary>
    private async Task<bool> StopServiceAsync(string service, CancellationToken ct)
    {
        ProcessResult result = await _runner
            .RunAsync("sc.exe", new[] { "stop", service }, ServiceTimeout, ct)
            .ConfigureAwait(false);

        // Bereits gestoppt ist kein Fehler, sondern genau der gewünschte Zustand.
        if (result.ExitCode == ErrorServiceNotActive) return true;
        if (!result.Success) return false;

        return await WaitForServiceStateAsync(service, DeepCleanParsers.ServiceStopped, ct).ConfigureAwait(false);
    }

    /// <summary>Startet einen Dienst wieder. Best effort – ein Fehlschlag kippt die Bereinigung nicht.</summary>
    private async Task<bool> StartServiceAsync(string service, CancellationToken ct)
    {
        try
        {
            ProcessResult result = await _runner
                .RunAsync("sc.exe", new[] { "start", service }, ServiceTimeout, ct)
                .ConfigureAwait(false);

            return result.Success || result.ExitCode == ErrorServiceAlreadyRunning;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pollt <c>sc query</c>, bis der Dienst den erwarteten Zustand hat oder die Zeit abläuft.</summary>
    private async Task<bool> WaitForServiceStateAsync(string service, int expectedState, CancellationToken ct)
    {
        const int attempts = 30;          // 30 × 500 ms = 15 s
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            ProcessResult query = await _runner
                .RunAsync("sc.exe", new[] { "query", service }, ServiceTimeout, ct)
                .ConfigureAwait(false);

            if (DeepCleanParsers.TryParseServiceState(query.CombinedOutput, out int state) && state == expectedState)
                return true;

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    // ===================================================================================
    //  Werkzeug-Aufruf und Fehlerabbildung
    // ===================================================================================

    private Task<ProcessResult> RunDismAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        => _runner.RunAsync("dism.exe", arguments, timeout, ct);

    /// <summary>
    /// DISM-Aufruf, dessen Fortschrittsbalken bis zum Aufrufer durchgereicht wird. Die rohen
    /// Werte gehen ungefiltert an den <see cref="DeepCleanProgressReporter"/> – der macht daraus
    /// eine monotone Reihe samt Restzeit.
    /// </summary>
    private Task<ProcessResult> RunDismAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        IProgress<DeepCleanProgress>? progress, CancellationToken ct)
    {
        if (progress is null) return RunDismAsync(arguments, timeout, ct);

        var reporter = new DeepCleanProgressReporter(progress);
        return _runner.RunAsync("dism.exe", arguments, timeout, reporter.Report, ct);
    }

    /// <summary>
    /// Bildet einen fehlgeschlagenen Werkzeugaufruf auf ein Analyse-Ergebnis ab, oder liefert
    /// null, wenn alles in Ordnung war.
    /// </summary>
    private static DeepCleanAnalysis? MapToolFailure(DeepCleanAreaKind kind, ProcessResult result, string tool)
    {
        if (result.Success) return null;

        return new DeepCleanAnalysis(kind, 0, 0, DeepCleanStatus.Failed, DescribeToolFailure(result, tool));
    }

    /// <summary>
    /// Entscheidet, WELCHE Meldung ein fehlgeschlagener Werkzeugaufruf verdient: i18n-Schlüssel
    /// plus Argumente. Bewusst getrennt vom Übersetzen – die Wahl des Schlüssels und das
    /// Durchreichen des Exit-Codes ist Logik und gehört geprüft, der übersetzte Satz ist Anzeige.
    /// </summary>
    internal static (string Key, object[] Args) ToolFailureInfo(ProcessResult result, string tool)
        => result.ExitCode switch
        {
            ProcessRunner.LaunchFailedExitCode => ("deepclean.error.toolMissing", new object[] { tool }),
            ProcessRunner.TimeoutExitCode => ("deepclean.error.timeout", new object[] { tool }),
            _ => ("deepclean.error.exitCode", new object[] { tool, result.ExitCode })
        };

    /// <summary>Klartext für einen fehlgeschlagenen Werkzeugaufruf – der Exit-Code steht immer drin.</summary>
    internal static string DescribeToolFailure(ProcessResult result, string tool)
    {
        (string key, object[] args) = ToolFailureInfo(result, tool);
        return Loc.T(key, args);
    }
}
