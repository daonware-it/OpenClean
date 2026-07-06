using System;
using System.Diagnostics;
using System.Text;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Kapselt Aufrufe des Windows-Paketmanagers <c>winget</c> zum Auflisten und Installieren
/// von Programm-Updates. Die Anwendung läuft dank Admin-Manifest bereits erhöht, daher
/// erbt winget die Elevation ohne Zusatzbehandlung. Alle Methoden sind defensiv: winget-
/// Fehler, ein fehlendes winget oder kaputte Ausgaben führen nie zu einer Ausnahme beim
/// Aufrufer (leere Liste bzw. <c>false</c>). Fehlgeschlagene/erfolgreiche Upgrades werden
/// über <see cref="UpdateLogService"/> protokolliert.
/// </summary>
public sealed class WingetService
{
    private readonly UpdateLogService _log = new();

    /// <summary>
    /// Baut eine <see cref="ProcessStartInfo"/> für <c>winget</c> mit umgeleiteten
    /// Ausgabeströmen (UTF-8) und ohne sichtbares Fenster. Argumente werden über die
    /// <see cref="ProcessStartInfo.ArgumentList"/> übergeben, damit kein manuelles
    /// Quoting nötig ist.
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// Prüft, ob winget vorhanden und aufrufbar ist (<c>winget --version</c>).
    /// Gibt bei fehlendem winget oder jeglichem Fehler <c>false</c> zurück.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(BuildStartInfo("--version"));
            if (process is null)
                return false;

            // Ausgabe leeren, um Blockaden zu vermeiden, dann mit Timeout warten.
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(true); } catch { /* egal */ }
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // winget nicht gefunden.
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Führt <c>winget upgrade</c> aus und parst die Tabelle der aktualisierbaren Programme.
    /// Das Parsen ist locale-tolerant (keine Abhängigkeit von englischen Spaltenüberschriften)
    /// und robust gegen Fortschrittsanzeigen. Bei jedem Fehler wird eine leere Liste geliefert.
    /// </summary>
    public IReadOnlyList<AppUpdate> GetUpgradable()
    {
        try
        {
            string output;
            using (var process = Process.Start(BuildStartInfo(
                "upgrade", "--include-unknown", "--accept-source-agreements")))
            {
                if (process is null)
                    return Array.Empty<AppUpdate>();

                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(60000);
            }

            return ParseUpgradeTable(output);
        }
        catch
        {
            return Array.Empty<AppUpdate>();
        }
    }

    /// <summary>
    /// Parst die winget-Upgrade-Tabelle. Vorgehen:
    /// Zeilen anhand von '\n' trennen, je Zeile nur den Teil nach dem letzten '\r' verwenden
    /// (entfernt Fortschritts-Spinner), rechts trimmen. Die Trennzeile aus Bindestrichen
    /// markiert das Ende des Kopfes; die Zeile davor ist der Spaltenkopf. Die Spaltenstarts
    /// werden aus dem Kopf über Läufe von 2+ Leerzeichen bestimmt (5 Spalten: Name, Id,
    /// Version, Verfügbar, Quelle). Jede Datenzeile wird an diesen Startindizes zerlegt.
    /// </summary>
    private static IReadOnlyList<AppUpdate> ParseUpgradeTable(string output)
    {
        var result = new List<AppUpdate>();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        // 1) Normalisieren: Zeilen enden als "TEXT\r" (durch \r\n) und Fortschritts-Spinner
        //    überschreiben Inhalte mitten in der Zeile per '\r'. Deshalb die Zeile an '\r'
        //    zerlegen und das LETZTE nicht-leere Segment nehmen (der zuletzt gerenderte Text).
        //    Ein simples "Teil nach dem letzten \r" wäre falsch: bei "TEXT\r" wäre der Teil leer.
        string[] raw = output.Split('\n');
        var lines = new List<string>(raw.Length);
        foreach (string r in raw)
        {
            string cleaned = "";
            string[] segments = r.Split('\r');
            for (int s = segments.Length - 1; s >= 0; s--)
            {
                if (segments[s].Trim().Length > 0)
                {
                    cleaned = segments[s];
                    break;
                }
            }
            lines.Add(cleaned.TrimEnd());
        }

        // 2) Trennzeile aus Bindestrichen finden (Inhalt nur '-', Länge > 10).
        int dashesIndex = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.Length > 10 && t.TrimStart('-').Length == 0)
            {
                dashesIndex = i;
                break;
            }
        }

        // Kopfzeile steht unmittelbar davor.
        if (dashesIndex <= 0)
            return result;

        string header = lines[dashesIndex - 1];

        // 3) Spaltenstarts aus dem Kopf ermitteln (Läufe von 2+ Leerzeichen trennen Spalten).
        var columnStarts = new List<int>();
        bool inColumn = false;
        int spaceRun = 0;
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] == ' ')
            {
                spaceRun++;
                if (spaceRun >= 2)
                    inColumn = false;
            }
            else
            {
                if (!inColumn)
                {
                    columnStarts.Add(i);
                    inColumn = true;
                }
                spaceRun = 0;
            }
        }

        // Wir erwarten 5 Spalten (Name, Id, Version, Verfügbar, Quelle).
        if (columnStarts.Count < 4)
            return result;

        // 4) Datenzeilen nach der Trennzeile zerlegen.
        for (int i = dashesIndex + 1; i < lines.Count; i++)
        {
            string line = lines[i];

            // Leerzeile beendet die Tabelle.
            if (string.IsNullOrWhiteSpace(line))
                break;

            // Zeile muss mindestens bis zur Verfügbar-Spalte (Index 3) reichen.
            if (line.Length <= columnStarts[3])
                continue;

            string name = SliceColumn(line, columnStarts, 0);
            string id = SliceColumn(line, columnStarts, 1);
            string version = SliceColumn(line, columnStarts, 2);
            string available = SliceColumn(line, columnStarts, 3);
            string source = columnStarts.Count >= 5 ? SliceColumn(line, columnStarts, 4) : "";

            // Zeilen ohne Id oder ohne verfügbare Version überspringen (z. B. Summenzeilen).
            if (id.Length == 0 || available.Length == 0)
                continue;

            result.Add(new AppUpdate
            {
                Name = name,
                Id = id,
                CurrentVersion = version,
                AvailableVersion = available,
                Source = source
            });
        }

        return result;
    }

    /// <summary>
    /// Schneidet das Feld einer Spalte aus einer Zeile heraus (vom Startindex der Spalte bis
    /// zum Start der nächsten Spalte bzw. Zeilenende) und trimmt es. Kürzere Zeilen liefern
    /// leere Felder.
    /// </summary>
    private static string SliceColumn(string line, List<int> columnStarts, int columnIndex)
    {
        if (columnIndex >= columnStarts.Count)
            return "";

        int start = columnStarts[columnIndex];
        if (start >= line.Length)
            return "";

        int end = columnIndex + 1 < columnStarts.Count
            ? Math.Min(columnStarts[columnIndex + 1], line.Length)
            : line.Length;

        if (end <= start)
            return "";

        return line[start..end].Trim();
    }

    /// <summary>
    /// Aktualisiert genau ein Programm über seine Paket-Id (<c>--exact</c> verhindert
    /// mehrdeutige Treffer). Ergebnis wird protokolliert. Erfolg = Exit-Code 0.
    /// Fehler führen zu einem Fehlprotokoll und <c>false</c> – nie zu einer Ausnahme.
    /// </summary>
    public async Task<bool> UpgradeAsync(AppUpdate app, CancellationToken ct = default)
    {
        try
        {
            using var process = Process.Start(BuildStartInfo(
                "upgrade",
                "--id", app.Id,
                "--exact",
                "--silent",
                "--accept-package-agreements",
                "--accept-source-agreements"));

            if (process is null)
            {
                _log.Log(app.Name, app.CurrentVersion, app.AvailableVersion, false);
                return false;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            bool success = process.ExitCode == 0;
            _log.Log(app.Name, app.CurrentVersion, app.AvailableVersion, success);
            return success;
        }
        catch (OperationCanceledException)
        {
            // Abbruch durch den Aufrufer -> als Fehlschlag protokollieren, nicht werfen.
            _log.Log(app.Name, app.CurrentVersion, app.AvailableVersion, false);
            return false;
        }
        catch
        {
            _log.Log(app.Name, app.CurrentVersion, app.AvailableVersion, false);
            return false;
        }
    }

    /// <summary>
    /// Aktualisiert nacheinander alle übergebenen Programme, meldet vor jedem Schritt den
    /// Fortschritt und respektiert Abbruchanforderungen (bricht die Schleife ab). Jedes
    /// Upgrade protokolliert sich selbst über <see cref="UpgradeAsync"/>.
    /// </summary>
    /// <returns><c>true</c>, wenn alle Programme erfolgreich aktualisiert wurden.</returns>
    public async Task<bool> UpgradeAllAsync(
        IReadOnlyList<AppUpdate> apps,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct = default)
    {
        if (apps is null || apps.Count == 0)
            return true;

        int successCount = 0;

        for (int i = 0; i < apps.Count; i++)
        {
            if (ct.IsCancellationRequested)
                break;

            AppUpdate app = apps[i];
            progress?.Report(new UpdateProgress
            {
                CurrentApp = app.Name,
                Done = i,
                Total = apps.Count
            });

            if (await UpgradeAsync(app, ct).ConfigureAwait(false))
                successCount++;
        }

        // Abschließend als "fertig" melden.
        progress?.Report(new UpdateProgress
        {
            CurrentApp = "",
            Done = apps.Count,
            Total = apps.Count
        });

        return successCount == apps.Count;
    }
}
