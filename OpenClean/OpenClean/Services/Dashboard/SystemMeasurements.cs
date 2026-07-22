using System.Collections.Generic;

namespace OpenClean.Services.Dashboard;

/// <summary>
/// Roh-Messwerte eines einzelnen Laufwerks, wie sie Score und Empfehlungen lesen.
/// Bewusst WPF-frei und ohne Bezug auf <c>DriveUsage</c>: nur die tatsächlich
/// ausgewerteten Felder, damit die Rechen-Engines pur und testbar bleiben.
/// </summary>
public readonly struct DriveMeasurement
{
    public DriveMeasurement(string letter, double freePercent, double usedPercent, string freeDisplay)
    {
        Letter = letter;
        FreePercent = freePercent;
        UsedPercent = usedPercent;
        FreeDisplay = freeDisplay;
    }

    /// <summary>Laufwerksbuchstabe inkl. Doppelpunkt, z. B. "C:".</summary>
    public string Letter { get; }

    /// <summary>Frei in Prozent (0–100).</summary>
    public double FreePercent { get; }

    /// <summary>Belegt in Prozent (0–100).</summary>
    public double UsedPercent { get; }

    /// <summary>Vorformatierter freier Speicher, z. B. "152,26 GB".</summary>
    public string FreeDisplay { get; }
}

/// <summary>
/// Momentaufnahme aller Roh-Messwerte, aus denen sich Score und Empfehlungen ableiten.
/// Kapselt GENAU die Werte, die <see cref="SystemScoreCalculator"/> und
/// <see cref="RecommendationBuilder"/> lesen – nichts sonst. Rein datenhaltend, ohne
/// UI-Abhängigkeit, damit beide Engines deterministisch und ohne WPF prüfbar sind.
/// </summary>
public readonly struct SystemMeasurements
{
    public SystemMeasurements(
        IReadOnlyList<DriveMeasurement> drives,
        long tempBytes,
        int tempItems,
        int startupEnabled,
        int startupHighImpact,
        bool ramValid,
        double ramPercent)
    {
        Drives = drives;
        TempBytes = tempBytes;
        TempItems = tempItems;
        StartupEnabled = startupEnabled;
        StartupHighImpact = startupHighImpact;
        RamValid = ramValid;
        RamPercent = ramPercent;
    }

    /// <summary>Belegung der festen Laufwerke (Reihenfolge stabil, wie erfasst).</summary>
    public IReadOnlyList<DriveMeasurement> Drives { get; }

    /// <summary>Bereinigbare Temp-/Cache-Größe in Bytes (Ergebnis von <c>ScanAll</c>).</summary>
    public long TempBytes { get; }

    /// <summary>Anzahl der bereinigbaren Temp-Einträge.</summary>
    public int TempItems { get; }

    /// <summary>Anzahl aktiver Autostart-Einträge.</summary>
    public int StartupEnabled { get; }

    /// <summary>Anzahl aktiver Autostart-Einträge mit hoher Systemlast.</summary>
    public int StartupHighImpact { get; }

    /// <summary>True, wenn die RAM-Messung gültig ist (dwMemoryLoad verfügbar).</summary>
    public bool RamValid { get; }

    /// <summary>Echte RAM-Auslastung (dwMemoryLoad), 0–100.</summary>
    public double RamPercent { get; }
}
