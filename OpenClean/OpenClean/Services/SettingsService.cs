using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClean.Services;

/// <summary>
/// Persistierte Benutzereinstellungen. Bewusst nur string-Felder (keine Enum-Kopplung
/// an andere Schichten), damit der Dienst schlank bleibt; die Aufrufer parsen selbst.
/// Nicht gesetzte Werte sind <c>null</c> – so ist ein „noch nie gestartet" erkennbar.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Sprachcode (z. B. "de", "en").</summary>
    public string? Language { get; set; }

    /// <summary>Theme-Name ("Dark" / "Light").</summary>
    public string? Theme { get; set; }

    /// <summary>Zuletzt aktiver Bereich (Enum-Name von AppSection, z. B. "Bereinigung").</summary>
    public string? LastSection { get; set; }

    /// <summary>
    /// Konfiguration der geplanten automatischen Reinigung (v0.8.0). Immer vorhanden
    /// (Default-Werte), damit ältere settings.json ohne diesen Block problemlos laden.
    /// </summary>
    public ScheduleSettings Schedule { get; set; } = new();
}

/// <summary>
/// Einstellungen der geplanten automatischen Reinigung. Bewusst mit einfachen
/// Werttypen/Strings gehalten, damit sie sich sauber als JSON persistieren lassen.
/// </summary>
public sealed class ScheduleSettings
{
    /// <summary>Ob die automatische Reinigung (geplante Aufgabe) aktiv ist.</summary>
    public bool Enabled { get; set; }

    /// <summary>Häufigkeit: "Daily", "Weekly" oder "Monthly".</summary>
    public string Frequency { get; set; } = "Weekly";

    /// <summary>Uhrzeit – Stunde (0–23).</summary>
    public int Hour { get; set; } = 20;

    /// <summary>Uhrzeit – Minute (0–59).</summary>
    public int Minute { get; set; }

    /// <summary>Wochentag für "Weekly" (0 = Sonntag … 6 = Samstag), analog zu <see cref="System.DayOfWeek"/>.</summary>
    public int DayOfWeek { get; set; } = 1;

    /// <summary>Tag des Monats für "Monthly" (1–28, aus Sicherheit nicht darüber).</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Reinigungsprofil: "TempOnly", "Full" oder "Custom".</summary>
    public string Profile { get; set; } = "TempOnly";

    /// <summary>Bei "Custom" die gewählten Kategorie-Schlüssel (z. B. "cat.windowsTemp").</summary>
    public List<string> CustomCategoryKeys { get; set; } = new();

    /// <summary>Ob nach einer automatischen Reinigung eine Windows-Benachrichtigung erscheinen soll.</summary>
    public bool Notify { get; set; } = true;
}

/// <summary>
/// Lädt/speichert <see cref="AppSettings"/> als <c>settings.json</c> im
/// Datenverzeichnis (<see cref="AppPaths.DataDirectory"/> – portabel neben der EXE,
/// sonst <c>%AppData%\OpenClean\</c>). Ersetzt die frühere Registry-Persistenz. Alle Datei-
/// operationen sind gekapselt – bei fehlender/kaputter Datei oder fehlendem Zugriff
/// wird nie eine Ausnahme durchgereicht (Rückfall auf leere Defaults).
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Prozessweiter Singleton.</summary>
    public static SettingsService Instance { get; } = new();

    private readonly string _filePath;
    private readonly AppSettings _settings;

    private SettingsService()
    {
        _filePath = AppPaths.GetDataFile("settings.json");
        _settings = Load();
    }

    /// <summary>Der aktuelle Einstellungsstand (in-memory, per <see cref="Save"/> persistiert).</summary>
    public AppSettings Current => _settings;

    /// <summary>Schreibt die aktuellen Einstellungen als JSON-Datei (Verzeichnis wird bei Bedarf angelegt).</summary>
    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_settings, JsonOptions));
        }
        catch
        {
            // Nicht schreibbar (Rechte/Datenträger) -> Auswahl bleibt nur für diese Sitzung.
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (data is not null)
                    return data;
            }
        }
        catch
        {
            // Fehlende/kaputte Datei -> leere Defaults.
        }

        return new AppSettings();
    }
}
