using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenClean.Services.DeepClean;

/// <summary>Ergebnis eines externen Prozessaufrufs (dism, vssadmin, sc, …).</summary>
/// <param name="ExitCode">Rückgabewert des Prozesses bzw. eine der Sentinel-Konstanten aus <see cref="ProcessRunner"/>.</param>
/// <param name="StandardOutput">Vollständige Standardausgabe.</param>
/// <param name="StandardError">Vollständige Fehlerausgabe.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True, wenn der Prozess mit 0 beendet wurde.</summary>
    public bool Success => ExitCode == 0;

    /// <summary>Beide Ströme zusammen – die Werkzeuge schreiben Details mal hierhin, mal dorthin.</summary>
    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>
/// Abstraktion der Prozess-Ausführung. Existiert, damit die gesamte Auswerte-Logik der
/// Tiefen-Bereinigung (DISM-/vssadmin-/sc-Ausgaben) ohne echte Systemänderung und ohne
/// Administratorrechte testbar bleibt.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Startet <paramref name="fileName"/> mit den Argumenten, wartet bis
    /// <paramref name="timeout"/> abgelaufen ist und liefert Exit-Code samt Ausgaben.
    /// Wirft nur <see cref="OperationCanceledException"/> (bei <paramref name="ct"/>); jeder
    /// andere Fehler kommt als Sentinel-Exit-Code zurück.
    /// </summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Wie <see cref="RunAsync(string, IReadOnlyList{string}, TimeSpan, CancellationToken)"/>,
    /// meldet aber während des Laufs jeden Fortschrittswert, den das Werkzeug auf die
    /// Standardausgabe schreibt (0–100). <paramref name="onProgress"/> wird aus einem
    /// Hintergrund-Thread aufgerufen und kann für dasselbe Prozent mehrfach feuern – das ist
    /// gewollt, es hält die Stagnations-Erkennung am Leben.
    ///
    /// <para>Die Standardimplementierung ignoriert den Fortschritt und ruft die schlichte
    /// Überladung auf: Attrappen in Tests, die nichts zu melden haben, müssen dafür nichts
    /// tun.</para>
    /// </summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        Action<double>? onProgress, CancellationToken ct)
        => RunAsync(fileName, arguments, timeout, ct);
}

/// <summary>
/// Echte Implementierung über <see cref="Process"/>: ohne Fenster, mit umgeleiteten Strömen und
/// hartem Zeitlimit. OpenClean läuft dank app.manifest bereits erhöht, die Kindprozesse erben
/// die Rechte – es wird bewusst nichts zusätzlich eskaliert.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Prozess konnte nicht gestartet werden (Werkzeug fehlt).</summary>
    public const int LaunchFailedExitCode = -1;

    /// <summary>Zeitlimit abgelaufen, Prozess wurde beendet.</summary>
    public const int TimeoutExitCode = -2;

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        => RunAsync(fileName, arguments, timeout, onProgress: null, ct);

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        Action<double>? onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // Werkzeug nicht vorhanden (z. B. dism.exe auf einer beschnittenen Installation).
            return new ProcessResult(LaunchFailedExitCode, "", "");
        }
        catch (Exception ex)
        {
            return new ProcessResult(LaunchFailedExitCode, "", ex.Message);
        }

        if (process is null)
            return new ProcessResult(LaunchFailedExitCode, "", "");

        using (process)
        {
            // Ströme SOFORT leeren: ein volles Pipe-Puffer würde den Prozess blockieren und
            // damit das Zeitlimit auslösen, obwohl er längst fertig wäre.
            Task<string> stdout = ReadWithProgressAsync(process.StandardOutput, onProgress);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* schon beendet */ }

                // Abbruch durch den Nutzer wird durchgereicht; ein reines Zeitlimit nicht.
                ct.ThrowIfCancellationRequested();

                // Bis hierher Gelesenes noch einsammeln: Bei einem DISM-Zeitlimit steht in der
                // Ausgabe, woran es hing (Fortschritt, Fehlercode) – genau das, was man zur
                // Fehlersuche braucht. Der Kill schließt die Ströme, die Lese-Aufgaben laufen
                // also aus; das kurze Limit ist nur die Rückversicherung, damit ein
                // überlebender Enkelprozess die Pipe nicht offen hält.
                return new ProcessResult(
                    TimeoutExitCode,
                    await DrainAsync(stdout).ConfigureAwait(false),
                    await DrainAsync(stderr).ConfigureAwait(false));
            }

            string outText = await stdout.ConfigureAwait(false);
            string errText = await stderr.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, outText, errText);
        }
    }

    /// <summary>
    /// Liest die Standardausgabe vollständig und meldet dabei jeden Fortschrittswert.
    ///
    /// <para><b>Warum blockweise und nicht zeilenweise:</b> DISM aktualisiert seinen Balken per
    /// WAGENRÜCKLAUF an Ort und Stelle. Nachgemessen an
    /// <c>dism /Online /Cleanup-Image /AnalyzeComponentStore /English</c> (32,6 s): 75 CR gegen
    /// 49 LF – 26 Balken hängen an einem alleinstehenden <c>\r</c>. Auf ein <c>\n</c> je Balken
    /// (<c>ReadLine</c>, <c>OutputDataReceived</c>) ist also kein Verlass; getrennt wird deshalb
    /// selbst an <c>\r</c> UND <c>\n</c>. Der zurückgelieferte Text bleibt davon unberührt: Er
    /// enthält den Strom Zeichen für Zeichen so, wie das Werkzeug ihn geschrieben hat.</para>
    /// </summary>
    internal static async Task<string> ReadWithProgressAsync(TextReader reader, Action<double>? onProgress)
    {
        var all = new StringBuilder();
        var segment = new StringBuilder();
        var buffer = new char[4096];

        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            all.Append(buffer, 0, read);

            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is '\r' or '\n')
                {
                    ReportSegment(segment, onProgress);
                    segment.Clear();
                }
                else
                {
                    segment.Append(c);
                }
            }
        }

        // Letzter Abschnitt ohne abschließenden Umbruch – bei einem Kill der Regelfall.
        ReportSegment(segment, onProgress);
        return all.ToString();
    }

    /// <summary>Meldet den Fortschritt eines Abschnitts, falls er einen trägt. Nie eine Ausnahme.</summary>
    private static void ReportSegment(StringBuilder segment, Action<double>? onProgress)
    {
        if (onProgress is null || segment.Length == 0) return;

        if (DeepCleanParsers.TryParseProgressPercent(segment.ToString(), out double percent))
        {
            // Der Rückruf gehört dem Aufrufer; ein Fehler dort darf das Auslesen des Stroms
            // nicht abreißen lassen – sonst blockierte der Prozess an einer vollen Pipe.
            try { onProgress(percent); } catch { /* Anzeige ist Zubrot, der Strom ist Pflicht */ }
        }
    }

    /// <summary>Wartezeit auf die Ströme nach einem Kill – kurz, das Zeitlimit ist schon abgelaufen.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Liefert das Ergebnis einer Lese-Aufgabe, wenn sie zeitnah fertig wird, sonst einen leeren
    /// Text. Nie eine Ausnahme: Nach einem Zeitlimit ist die Ausgabe ein Zubrot, kein Vertrag.
    /// </summary>
    private static async Task<string> DrainAsync(Task<string> reader)
    {
        try
        {
            return await reader.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }
}
