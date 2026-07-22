using System.Diagnostics;

namespace OpenClean.Services.DeepClean;

/// <summary>Aussagekraft einer Restzeit-Schätzung.</summary>
public enum DeepCleanEtaKind
{
    /// <summary>Noch keine belastbare Schätzung – es wird nichts angezeigt.</summary>
    None,

    /// <summary>Der Fortschritt steht; eine Zahl wäre geraten. Anzeige: „noch einige Zeit".</summary>
    Unknown,

    /// <summary>Belastbare Schätzung in <see cref="DeepCleanEta.Remaining"/>.</summary>
    Value
}

/// <summary>Geschätzte Restzeit eines Arbeitsschritts.</summary>
/// <param name="Kind">Ob und wie aussagekräftig die Schätzung ist.</param>
/// <param name="Remaining">Restzeit; nur bei <see cref="DeepCleanEtaKind.Value"/> bedeutsam.</param>
public readonly record struct DeepCleanEta(DeepCleanEtaKind Kind, TimeSpan Remaining)
{
    /// <summary>Keine Schätzung.</summary>
    public static DeepCleanEta None { get; } = new(DeepCleanEtaKind.None, TimeSpan.Zero);

    /// <summary>Fortschritt steht – es gibt keine ehrliche Zahl.</summary>
    public static DeepCleanEta Unknown { get; } = new(DeepCleanEtaKind.Unknown, TimeSpan.Zero);
}

/// <summary>
/// Was ein Bereich gerade TUT. Ohne diese Unterscheidung behauptet die Anzeige, fertig zu sein,
/// während noch gearbeitet wird: Die Bereinigung des Komponentenspeichers misst vor und nach dem
/// eigentlichen Aufräumen die Größe (je ein eigener DISM-Lauf, auf diesem System rund 33 s).
/// Beide Messungen melden keinen Fortschritt – der Balken stand nach dem Aufräumen also auf
/// 100 %, und darunter lief eine halbe Minute lang „Bereinige …". Genau dieses Schweigen ist der
/// Grund für die Phase.
/// </summary>
public enum DeepCleanPhase
{
    /// <summary>Die eigentliche Arbeit; hier ist der Prozentwert gemessen.</summary>
    Working,

    /// <summary>Ausgangsgröße wird ermittelt – vor der Arbeit, ohne bezifferbaren Fortschritt.</summary>
    MeasuringBefore,

    /// <summary>Endgröße wird ermittelt – nach der Arbeit, ohne bezifferbaren Fortschritt.</summary>
    MeasuringAfter
}

/// <summary>
/// Fortschritt INNERHALB eines Bereichs: Phase, Prozent und Restzeit. Prozent und Restzeit sind
/// nur in <see cref="DeepCleanPhase.Working"/> bedeutsam; in den Messphasen sagt der Bereich
/// bewusst nur, DASS er misst – eine Zahl hätte er dort nicht.
/// </summary>
/// <param name="Percent">Fortschritt des laufenden Bereichs, 0–100.</param>
/// <param name="Eta">Geschätzte Restzeit dieses Bereichs.</param>
/// <param name="Phase">Was der Bereich gerade tut.</param>
public sealed record DeepCleanProgress(
    double Percent,
    DeepCleanEta Eta,
    DeepCleanPhase Phase = DeepCleanPhase.Working);

/// <summary>
/// Hält den zuletzt gemeldeten Fortschritt eines Werkzeugs und lässt ihn NIE zurücklaufen.
///
/// <para>Nötig, weil DISM denselben Wert mehrfach meldet und ein Balken, der zurückspringt, den
/// Nutzer glauben lässt, die Arbeit fange von vorn an. Zurückgewiesen wird auch alles, was gar
/// keinen Prozentwert trägt – der Rest der DISM-Ausgabe ist Fließtext.</para>
/// </summary>
internal sealed class DismProgressTracker
{
    /// <summary>Höchster bisher gesehener Wert; Startwert 0, nicht „unbekannt".</summary>
    public double Percent { get; private set; }

    /// <summary>
    /// Nimmt einen Ausgabe-Abschnitt auf. Liefert true, wenn er den Fortschritt WEITER gebracht
    /// hat – nur dann lohnt eine neue Anzeige. Wiederholungen und Rückschritte liefern false.
    /// </summary>
    public bool TryAdvance(string? segment, out double percent)
    {
        percent = Percent;
        if (!DeepCleanParsers.TryParseProgressPercent(segment, out double parsed)) return false;

        return TryAdvance(parsed, out percent);
    }

    /// <summary>Wie <see cref="TryAdvance(string?, out double)"/>, aber mit fertigem Prozentwert.</summary>
    public bool TryAdvance(double parsed, out double percent)
    {
        percent = Percent;
        if (parsed <= Percent) return false;

        Percent = parsed;
        percent = parsed;
        return true;
    }
}

/// <summary>
/// Schätzt aus verstrichener Zeit und Fortschritt die Restzeit – nach denselben Regeln wie im
/// Cleaner (ETA = verstrichen / Prozent × Restprozent), aber gegen die Eigenheiten von DISM
/// gehärtet:
///
/// <list type="bullet">
/// <item>Erst ab einem SOCKEL (Mindestzeit und Mindestprozent) überhaupt eine Zahl. Bei 0,5 %
/// nach einer Sekunde stünde dort „noch 3 Minuten", eine Sekunde später „noch 40 Sekunden" –
/// solche Zahlen sind schlimmer als gar keine.</item>
/// <item>GEGLÄTTET über ein gleitendes Fenster: DISM springt in Schüben (5,9 → 12,5 → fünfmal
/// 16,5), jede Einzelmessung für sich wäre wertlos.</item>
/// <item>Sie läuft NICHT zurück (also nicht hoch), solange die Schätzung nicht deutlich
/// schlechter wird. Eine Restzeit, die wächst, wirkt wie ein Hänger.</item>
/// <item>STEHT der Fortschritt zu lange, gibt es keine Zahl mehr, sondern
/// <see cref="DeepCleanEtaKind.Unknown"/> – ehrlicher als eine Zahl, die niemand mehr hält.</item>
/// </list>
/// </summary>
internal sealed class EtaEstimator
{
    /// <summary>Sockel: vorher ist jede Hochrechnung reines Rauschen.</summary>
    private static readonly TimeSpan MinElapsed = TimeSpan.FromSeconds(3);
    private const double MinPercent = 5.0;

    /// <summary>Ab hier gilt der Fortschritt als stehend.</summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    /// <summary>Breite des Glättungsfensters (Anzahl Roh-Schätzungen).</summary>
    private const int WindowSize = 5;

    /// <summary>Erst ab dieser Verschlechterung darf die angezeigte Restzeit steigen.</summary>
    private const double WorseFactor = 1.25;

    private readonly Queue<double> _window = new();
    private double _lastPercent = -1;
    private TimeSpan _lastAdvance = TimeSpan.Zero;
    private double? _shownSeconds;

    /// <summary>
    /// Meldet den aktuellen Stand. Wird bewusst AUCH bei unverändertem Prozentwert aufgerufen –
    /// nur so lässt sich ein stehender Fortschritt von einem zügigen unterscheiden.
    /// </summary>
    /// <param name="elapsed">Seit Beginn des Schritts verstrichene Zeit.</param>
    /// <param name="percent">Aktueller Fortschritt 0–100 (bereits monoton).</param>
    public DeepCleanEta Update(TimeSpan elapsed, double percent)
    {
        if (percent > _lastPercent)
        {
            _lastPercent = percent;
            _lastAdvance = elapsed;
        }

        // Fertig: keine Restzeit mehr anzuzeigen.
        if (percent >= 100) return DeepCleanEta.None;

        // Sockel noch nicht erreicht -> schweigen statt raten.
        if (percent < MinPercent || elapsed < MinElapsed) return DeepCleanEta.None;

        // Fortschritt steht: die letzte Zahl gilt nicht mehr, eine neue wäre erfunden.
        if (elapsed - _lastAdvance > StallAfter) return DeepCleanEta.Unknown;

        double raw = elapsed.TotalSeconds / percent * (100 - percent);
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0) return DeepCleanEta.None;

        _window.Enqueue(raw);
        while (_window.Count > WindowSize) _window.Dequeue();

        double smoothed = _window.Average();

        // Nur nach unten frei; nach oben erst, wenn die Lage wirklich schlechter geworden ist.
        double shown = _shownSeconds is { } previous && smoothed > previous && smoothed < previous * WorseFactor
            ? previous
            : smoothed;

        _shownSeconds = shown;
        return new DeepCleanEta(DeepCleanEtaKind.Value, TimeSpan.FromSeconds(shown));
    }
}

/// <summary>
/// Klammert Balkenwerte, Monotonie, Restzeit und Meldung zusammen: Genau das, was jeder Bereich
/// mit echtem Fortschritt braucht. Der Aufrufer reicht nur noch die rohen Prozentwerte des
/// Werkzeugs herein.
/// </summary>
internal sealed class DeepCleanProgressReporter
{
    private readonly IProgress<DeepCleanProgress>? _progress;
    private readonly DismProgressTracker _tracker = new();
    private readonly EtaEstimator _eta = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public DeepCleanProgressReporter(IProgress<DeepCleanProgress>? progress) => _progress = progress;

    /// <summary>
    /// Nimmt einen rohen Prozentwert des Werkzeugs auf und meldet den Stand weiter.
    ///
    /// <para>Wiederholte gleiche Werte werden NICHT verworfen: Sie sind die einzige Auskunft
    /// darüber, dass das Werkzeug noch lebt, und damit die Grundlage der Stagnations-Erkennung.
    /// Der angezeigte Prozentwert bleibt dabei dank <see cref="DismProgressTracker"/> stehen.</para>
    /// </summary>
    public void Report(double rawPercent)
    {
        if (_progress is null) return;

        _tracker.TryAdvance(rawPercent, out _);
        DeepCleanEta eta = _eta.Update(_clock.Elapsed, _tracker.Percent);
        _progress.Report(new DeepCleanProgress(_tracker.Percent, eta));
    }

    /// <summary>Meldet einen Schritt als abgeschlossen (100 %, keine Restzeit).</summary>
    public void ReportComplete() => _progress?.Report(new DeepCleanProgress(100, DeepCleanEta.None));
}
