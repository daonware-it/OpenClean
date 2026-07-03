using System.IO;
using System.Text.Json;
using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>
/// Agent (Berichterstatter): persistiert die Berichte der automatischen Reinigungen
/// im Datenverzeichnis (<see cref="AppPaths.DataDirectory"/>) – als strukturierte JSON-Historie
/// (<c>autoclean-reports.json</c>, für die Anzeige im UI) sowie als menschenlesbares
/// Textprotokoll (<c>autoclean.log</c>). Alle Dateioperationen sind gekapselt: ein
/// Schreib-/Lesefehler darf die eigentliche Reinigung niemals stören.
/// </summary>
public sealed class AutoCleanReportStore
{
    // Maximale Anzahl aufbewahrter Berichte und maximale Pfadanzahl je Bericht
    // (deckelt die Dateigröße – bei tausenden gelöschten Dateien).
    private const int MaxReports = 30;
    private const int MaxPathsPerReport = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Pfad der JSON-Historie.</summary>
    public string JsonPath { get; }

    /// <summary>Pfad des Textprotokolls.</summary>
    public string LogPath { get; }

    public AutoCleanReportStore()
    {
        JsonPath = AppPaths.GetDataFile("autoclean-reports.json");
        LogPath = AppPaths.GetDataFile("autoclean.log");
    }

    /// <summary>Lädt die gespeicherten Berichte (neueste zuerst); bei Fehler eine leere Liste.</summary>
    public IReadOnlyList<AutoCleanReport> Load()
    {
        try
        {
            if (!File.Exists(JsonPath)) return Array.Empty<AutoCleanReport>();
            string json = File.ReadAllText(JsonPath);
            var data = JsonSerializer.Deserialize<List<AutoCleanReport>>(json, JsonOptions);
            return data ?? new List<AutoCleanReport>();
        }
        catch
        {
            return Array.Empty<AutoCleanReport>();
        }
    }

    /// <summary>
    /// Erzeugt aus einem Reinigungsergebnis einen Bericht, speichert ihn (vorne in der
    /// Historie, gedeckelt auf <see cref="MaxReports"/>) und schreibt eine Protokollzeile.
    /// Gibt den erzeugten Bericht zurück (z. B. für die Benachrichtigung).
    /// </summary>
    public AutoCleanReport Add(string profile, CleanupReport result, DateTime timestamp)
    {
        var report = new AutoCleanReport
        {
            Timestamp = timestamp,
            Profile = profile,
            DeletedCount = result.DeletedCount,
            FreedBytes = result.FreedBytes,
            SkippedCount = result.Skipped.Count,
            DeletedPaths = result.Deleted.Take(MaxPathsPerReport).ToList(),
            PathsTruncated = result.Deleted.Count > MaxPathsPerReport
        };

        SaveJson(report);
        AppendLog(report);
        return report;
    }

    private void SaveJson(AutoCleanReport report)
    {
        try
        {
            EnsureDir(JsonPath);
            var history = new List<AutoCleanReport>(Load());
            history.Insert(0, report);
            if (history.Count > MaxReports)
                history.RemoveRange(MaxReports, history.Count - MaxReports);
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(history, JsonOptions));
        }
        catch
        {
            // Nicht schreibbar -> Historie bleibt unverändert.
        }
    }

    private void AppendLog(AutoCleanReport report)
    {
        try
        {
            EnsureDir(LogPath);
            string stamp = report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            string line =
                $"{stamp}  [{report.Profile}]  {report.DeletedCount} Objekt(e) gelöscht, " +
                $"{ByteFormatter.Format(report.FreedBytes)} freigegeben" +
                (report.SkippedCount > 0 ? $", {report.SkippedCount} übersprungen" : "") +
                Environment.NewLine;
            File.AppendAllText(LogPath, line);
        }
        catch
        {
            // Protokoll ist optional -> Fehler still verwerfen.
        }
    }

    private static void EnsureDir(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
