namespace OpenClean.Services.DeepClean;

/// <summary>Zustand eines Bereichs nach der Analyse.</summary>
public enum DeepCleanStatus
{
    /// <summary>Noch nicht analysiert.</summary>
    Unknown,

    /// <summary>Analysiert, es gibt etwas zu holen.</summary>
    Ready,

    /// <summary>Analysiert, aber nichts zu bereinigen (0 Bytes).</summary>
    Empty,

    /// <summary>Bereich existiert auf diesem System nicht (z. B. kein <c>Windows.old</c>).</summary>
    Unavailable,

    /// <summary>Analyse scheiterte an fehlenden Administratorrechten.</summary>
    AccessDenied,

    /// <summary>Analyse scheiterte (Werkzeug fehlt, Zeitlimit, unerwartete Ausgabe).</summary>
    Failed
}

/// <summary>
/// Ergebnis der Analyse eines Bereichs – Grundlage der Größenanzeige VOR der Bereinigung.
///
/// <para><b>„Belegt" und „freigebbar" sind zwei verschiedene Aussagen</b> und dürfen nie in einer
/// Zahl verschmelzen. Auf einem echten System meldet <c>vssadmin list shadowstorage</c> 7,98 GB
/// belegten Schattenkopie-Speicher, freigebbar sind davon 0 B (nur ein Wiederherstellungspunkt,
/// und der jüngste bleibt stehen). Eine einzelne „0 B"-Spalte behauptet dann, dort läge nichts –
/// der Nutzer hält die Anzeige folgerichtig für kaputt. Deshalb tragen beide Zahlen ihr eigenes
/// Feld: <see cref="SizeBytes"/> beantwortet „was kann weg", <see cref="OccupiedBytes"/> „was
/// liegt da".</para>
/// </summary>
/// <param name="Kind">Der analysierte Bereich.</param>
/// <param name="SizeBytes">FREIGEBBARER Speicher in Bytes – die Zahl, die die Bereinigung verspricht.</param>
/// <param name="ItemCount">Objektzahl (Pakete, Wiederherstellungspunkte …); 0, wenn ohne Bedeutung.</param>
/// <param name="Status">Fachlicher Zustand des Bereichs.</param>
/// <param name="Message">Fertig übersetzte Detailmeldung für die Anzeige.</param>
/// <param name="OccupiedBytes">
/// BELEGTER Speicher des Bereichs, soweit ein Werkzeug ihn nennt (vssadmin: belegter
/// Schattenkopie-Speicher, DISM: tatsächliche Größe des Komponentenspeichers). 0 bedeutet
/// „nicht bekannt", nicht „nichts belegt" – bei den Ordner-Bereichen sind belegt und freigebbar
/// ohnehin dieselbe Zahl.
/// </param>
public sealed record DeepCleanAnalysis(
    DeepCleanAreaKind Kind,
    long SizeBytes,
    int ItemCount,
    DeepCleanStatus Status,
    string Message,
    long OccupiedBytes = 0)
{
    /// <summary>True, wenn dieser Bereich bereinigt werden kann.</summary>
    public bool IsCleanable => Status == DeepCleanStatus.Ready;

    /// <summary>Analyse-Ergebnis für einen Bereich, den es hier nicht gibt.</summary>
    public static DeepCleanAnalysis Unavailable(DeepCleanAreaKind kind, string message)
        => new(kind, 0, 0, DeepCleanStatus.Unavailable, message);
}

/// <summary>Ausgang einer Bereinigung.</summary>
public enum DeepCleanOutcomeStatus
{
    /// <summary>Vollständig bereinigt.</summary>
    Success,

    /// <summary>Teilweise bereinigt – Reste blieben (z. B. Dateien in Verwendung).</summary>
    Partial,

    /// <summary>Es gab nichts zu tun.</summary>
    NothingToDo,

    /// <summary>Verweigert, weil die Integritätsprüfung angeschlagen hat (OPCL-20).</summary>
    Blocked,

    /// <summary>Verweigert mangels Administratorrechten.</summary>
    AccessDenied,

    /// <summary>Vom Nutzer abgebrochen.</summary>
    Canceled,

    /// <summary>Fehlgeschlagen (Werkzeug-Exitcode, Zeitlimit, Zugriff verweigert …).</summary>
    Failed
}

/// <summary>Ergebnis einer Bereinigung – Grundlage der „so viel wurde frei"-Anzeige.</summary>
/// <param name="Kind">Der bereinigte Bereich.</param>
/// <param name="Status">Ausgang.</param>
/// <param name="FreedBytes">Tatsächlich freigewordener Speicher (vorher/nachher gemessen).</param>
/// <param name="Message">Fertig übersetzte Meldung für die Anzeige.</param>
public sealed record DeepCleanOutcome(
    DeepCleanAreaKind Kind,
    DeepCleanOutcomeStatus Status,
    long FreedBytes,
    string Message)
{
    /// <summary>True, wenn der Bereich danach als bereinigt gelten darf.</summary>
    public bool IsSuccess => Status is DeepCleanOutcomeStatus.Success or DeepCleanOutcomeStatus.NothingToDo;
}
