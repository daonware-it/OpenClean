namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Schwellenwerte der Gesundheits-Ampel. Bewusst als Konstanten an EINER Stelle statt im
/// Auswerte-Code verteilt: die Werte sind Erfahrungswerte und müssen ohne Eingriff in die
/// Logik nachjustierbar sein.
/// </summary>
public static class SmartThresholds
{
    /// <summary>Ab dieser Anzahl neu zugewiesener Sektoren: „Beobachten“ (Gelb).</summary>
    public const long ReallocatedWarn = 1;

    /// <summary>Ab dieser Anzahl neu zugewiesener Sektoren: „Ausfall droht“ (Rot).</summary>
    public const long ReallocatedCritical = 10;

    /// <summary>Ab dieser Anzahl schwebender Sektoren: „Beobachten“ (Gelb).</summary>
    public const long PendingWarn = 1;

    /// <summary>Ab dieser Temperatur (°C): „Beobachten“ (Gelb).</summary>
    public const int TemperatureWarnCelsius = 55;

    /// <summary>Restlebensdauer (%) ab der abwärts „Beobachten“ (Gelb) gilt.</summary>
    public const int WearWarnPercent = 20;

    /// <summary>Restlebensdauer (%) ab der abwärts „Ausfall droht“ (Rot) gilt.</summary>
    public const int WearCriticalPercent = 5;
}
