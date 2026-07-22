using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClean.Services.DeepClean;

/// <summary>Ausgewertete DISM-Analyse des Komponentenspeichers.</summary>
/// <param name="ActualSizeBytes">Tatsächliche Größe des Komponentenspeichers.</param>
/// <param name="ReclaimableBytes">Summe aus „Backups and Disabled Features" und „Cache and Temporary Data" – das, was <c>/StartComponentCleanup</c> realistisch holen kann.</param>
/// <param name="ReclaimablePackages">Anzahl entfernbarer Pakete.</param>
/// <param name="CleanupRecommended">DISMs eigene Empfehlung.</param>
/// <param name="IsValid">False, wenn die Ausgabe nicht auswertbar war (Werkzeugfehler, unerwartetes Format).</param>
internal sealed record DismComponentStoreAnalysis(
    long ActualSizeBytes,
    long ReclaimableBytes,
    int ReclaimablePackages,
    bool CleanupRecommended,
    bool IsValid)
{
    /// <summary>Nicht auswertbares Ergebnis.</summary>
    public static DismComponentStoreAnalysis Invalid { get; } = new(0, 0, 0, false, false);
}

/// <summary>
/// Reine Textauswertung der Windows-Werkzeuge. Bewusst statisch und ohne jede Seitenwirkung:
/// Genau hier steckt die Logik, die sich ohne Administratorrechte und ohne echte
/// Systemänderung testen lässt.
///
/// <para><b>Sprachfestigkeit:</b> DISM wird mit <c>/English</c> aufgerufen, seine Bezeichner sind
/// daher stabil. <c>vssadmin</c> und <c>sc</c> kennen keinen solchen Schalter – deren Auswertung
/// hängt deshalb bewusst NICHT an übersetzbaren Beschriftungen, sondern an sprachneutralen
/// Mustern (Zahl + Einheit + Prozent bzw. die englisch bleibenden Zustandskonstanten).</para>
/// </summary>
internal static class DeepCleanParsers
{
    // Tausendertrenner der von Windows bedienten Sprachräume: Punkt/Komma (de, es, pt, en) sowie
    // Leerzeichen, geschütztes (U+00A0) und schmales geschütztes Leerzeichen (U+202F) – fr, ru,
    // pl gruppieren so ("1 234,5 GB"). Ohne diese drei läge das Ergebnis dort um Faktor 1000
    // daneben, weil nur "1" gelesen würde.
    private const string ThousandsSeparators = @"[.,\u00A0\u202F ]";

    // Zahl mit optionaler Gruppierung: "6.29", "8,16", "742", "1.234,5", "1 234,5".
    private const string NumberPattern =
        @"\d{1,3}(?:" + ThousandsSeparators + @"\d{3})*(?:[.,]\d+)?|\d+(?:[.,]\d+)?";

    // Einheiten hinten. Reihenfolge ist bedeutsam: "bytes" vor "byte" vor "B", sonst matchte das
    // kürzere Muster zuerst und ließe den Rest stehen; "B" zuletzt, sonst würde es das B von "KB"
    // greifen. DISM schreibt kleine Werte als "0 bytes" statt "0 B" – ohne die Langform bliebe die
    // Zeile ungelesen und das Feld stumm auf 0.
    private const string UnitPattern = @"KB|MB|GB|TB|bytes|byte|B";

    // "6.29 GB", "8,16 GB", "742 MB", "1.234,5 MB", "0 bytes".
    private static readonly Regex SizeRegex = new(
        @"(?<num>" + NumberPattern + @")\s*(?<unit>" + UnitPattern + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Größenangabe, der unmittelbar ein Prozentwert folgt: "8.16 GB (2%)" / "8,16 GB (2 %)".
    private static readonly Regex SizeWithPercentRegex = new(
        @"(?<num>" + NumberPattern + @")\s*(?<unit>" + UnitPattern + @")\s*\(\s*\d+(?:[.,]\d+)?\s*%\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Fortschrittsbalken von DISM: "[===                        5.9%                           ]".
    // Der Prozentwert steht MITTEN im Balken, der Dezimaltrenner richtet sich nach der
    // Systemsprache ("5.9" bzw. "5,9") – auch mit /English, der Schalter übersetzt nur die
    // Beschriftungen. Verlangt wird die öffnende Klammer: nur so kann die Zahl aus einer
    // Balkenzeile stammen und nicht aus einer beliebigen anderen Ausgabe mit Prozentzeichen.
    private static readonly Regex ProgressBarRegex = new(
        @"\[[^\]]*?(?<num>\d{1,3}(?:[.,]\d+)?)\s*%",
        RegexOptions.CultureInvariant);

    // "STATE : 4  RUNNING" bzw. deutsch "STATUS : 4  RUNNING": Die Beschriftung ist übersetzt,
    // die Zustandskonstante rechts NICHT – deshalb hängt das Muster an ihr.
    private static readonly Regex ServiceStateRegex = new(
        @":\s*(?<code>\d+)\s+(?<name>STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Dienstzustände aus der Win32-API (sprachneutral, wie sie <c>sc query</c> ausgibt).</summary>
    internal const int ServiceStopped = 1;
    internal const int ServiceRunning = 4;

    /// <summary>
    /// Liest die erste Größenangabe („12,5 GB") aus einem Text. Erkennt beide Dezimaltrenner:
    /// Kommen Punkt UND Komma vor, gilt das zuletzt stehende Zeichen als Dezimaltrenner und das
    /// andere als Tausendertrenner – sonst würde „1.234,5 MB" um Faktor 1000 danebenliegen.
    /// </summary>
    internal static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        Match match = SizeRegex.Match(text);
        if (!match.Success) return false;

        return TryConvert(match.Groups["num"].Value, match.Groups["unit"].Value, out bytes);
    }

    private static bool TryConvert(string number, string unit, out long bytes)
    {
        bytes = 0;

        string normalized = NormalizeNumber(number);
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return false;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return false;

        double factor = unit.ToUpperInvariant() switch
        {
            "B" or "BYTE" or "BYTES" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 0d
        };
        if (factor <= 0) return false;

        double result = value * factor;
        if (result > long.MaxValue) return false;

        bytes = (long)Math.Round(result);
        return true;
    }

    /// <summary>Vereinheitlicht die Trennzeichen einer Zahl auf das invariante Format.</summary>
    private static string NormalizeNumber(string number)
    {
        // Leerzeichen-Gruppierung (fr, ru, pl) zuerst weg: sie kann nur Tausendertrenner sein,
        // ein Dezimaltrenner ist nie ein Leerzeichen.
        number = number.Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "");

        int lastDot = number.LastIndexOf('.');
        int lastComma = number.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            // Das hintere Zeichen ist der Dezimaltrenner, das vordere der Tausendertrenner.
            char thousands = lastDot > lastComma ? ',' : '.';
            char decimalSep = lastDot > lastComma ? '.' : ',';
            return number.Replace(thousands.ToString(), "").Replace(decimalSep, '.');
        }

        if (lastComma >= 0)
            return number.Replace(',', '.');

        return number;
    }

    /// <summary>
    /// Wertet <c>dism /English /Online /Cleanup-Image /AnalyzeComponentStore</c> aus.
    /// Der freigebbare Anteil ist bewusst die Summe aus „Backups and Disabled Features" und
    /// „Cache and Temporary Data": „Shared with Windows" gehört fest zum Betriebssystem und wird
    /// von <c>/StartComponentCleanup</c> nie angefasst – es als freigebbar auszuweisen wäre eine
    /// Lüge in der wichtigsten Zahl der Anzeige.
    /// </summary>
    internal static DismComponentStoreAnalysis ParseDismAnalyze(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return DismComponentStoreAnalysis.Invalid;

        long actual = 0, backups = 0, cache = 0;
        int packages = 0;
        bool recommended = false;
        bool sawAny = false;

        foreach (string raw in SplitLines(output))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (Contains(line, "Actual Size of Component Store") && TryParseValueSize(line, out long a))
            {
                actual = a;
                sawAny = true;
            }
            else if (Contains(line, "Backups and Disabled Features") && TryParseValueSize(line, out long b))
            {
                backups = b;
                sawAny = true;
            }
            else if (Contains(line, "Cache and Temporary Data") && TryParseValueSize(line, out long c))
            {
                cache = c;
                sawAny = true;
            }
            else if (Contains(line, "Number of Reclaimable Packages"))
            {
                string value = ValuePart(line);
                if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                {
                    packages = p;
                    sawAny = true;
                }
            }
            else if (Contains(line, "Component Store Cleanup Recommended"))
            {
                recommended = ValuePart(line).Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
                sawAny = true;
            }
        }

        if (!sawAny) return DismComponentStoreAnalysis.Invalid;

        return new DismComponentStoreAnalysis(actual, backups + cache, packages, recommended, IsValid: true);
    }

    /// <summary>
    /// Summiert den von Schattenkopien BELEGTEN Speicher aus
    /// <c>vssadmin list shadowstorage</c> über alle Zuordnungsblöcke.
    ///
    /// <para>Je Block stehen drei Größen in fester Reihenfolge (belegt, zugewiesen, maximal),
    /// jeweils mit Prozentangabe. Gezählt wird nur die erste – die beiden anderen sind
    /// Reservierung bzw. Obergrenze, kein tatsächlich belegter Platz. Da die Beschriftungen
    /// übersetzt sind, trägt allein die Position innerhalb des Blocks die Information.</para>
    /// </summary>
    internal static long ParseUsedShadowStorage(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return 0;

        long total = 0;
        bool expectUsed = true;

        foreach (string raw in SplitLines(output))
        {
            Match match = SizeWithPercentRegex.Match(raw);
            if (!match.Success)
            {
                // Zeile ohne Größe+Prozent (Blockkopf, Leerzeile, "UNBOUNDED") -> nächster Block.
                expectUsed = true;
                continue;
            }

            if (!expectUsed) continue;

            if (TryConvert(match.Groups["num"].Value, match.Groups["unit"].Value, out long bytes))
                total += bytes;

            expectUsed = false;
        }

        return total;
    }

    /// <summary>
    /// Liest den Fortschritt aus EINEM Ausgabe-Abschnitt eines Werkzeugs, z. B. aus
    /// „<c>[===                        5.9%                           ]</c>".
    ///
    /// <para>Bewusst sprachneutral: Der Wert hängt nur an Klammer, Ziffern und Prozentzeichen,
    /// nicht an einer Beschriftung. Punkt und Komma gelten beide als Dezimaltrenner – DISM richtet
    /// sich hier nach der Systemsprache, auch mit <c>/English</c>.</para>
    ///
    /// <para>Erwartet einen bereits an <c>\r</c>/<c>\n</c> getrennten Abschnitt: DISM aktualisiert
    /// den Balken per Wagenrücklauf an Ort und Stelle, ein Zeilenumbruch je Balken ist NICHT
    /// zugesichert (siehe <see cref="ProcessRunner"/>).</para>
    /// </summary>
    internal static bool TryParseProgressPercent(string? segment, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(segment)) return false;

        Match match = ProgressBarRegex.Match(segment);
        if (!match.Success) return false;

        string normalized = NormalizeNumber(match.Groups["num"].Value);
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return false;
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        // Über 100 % gibt es nicht; eine solche Zahl stammt sicher nicht aus einem Balken.
        if (value < 0 || value > 100) return false;

        percent = value;
        return true;
    }

    /// <summary>
    /// Liest den Dienstzustand aus <c>sc query &lt;dienst&gt;</c>. Liefert false, wenn die Ausgabe
    /// keinen Zustand enthält (Dienst unbekannt, Fehlermeldung).
    /// </summary>
    internal static bool TryParseServiceState(string? output, out int state)
    {
        state = 0;
        if (string.IsNullOrWhiteSpace(output)) return false;

        Match match = ServiceStateRegex.Match(output);
        if (!match.Success) return false;

        return int.TryParse(match.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out state);
    }

    /// <summary>Teil rechts des ersten Doppelpunkts (DISM schreibt „Beschriftung : Wert").</summary>
    private static string ValuePart(string line)
    {
        int colon = line.IndexOf(':');
        return colon >= 0 && colon + 1 < line.Length ? line[(colon + 1)..] : "";
    }

    private static bool TryParseValueSize(string line, out long bytes)
        => TryParseSize(ValuePart(line), out bytes);

    private static bool Contains(string line, string needle)
        => line.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
