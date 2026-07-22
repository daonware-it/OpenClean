namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Die Werte, die für die Ampel zählen. Nullable heißt „vom Laufwerk nicht geliefert“.
/// </summary>
public sealed record SmartReadings(
    bool PredictFailure,
    int? TemperatureCelsius,
    int? WearRemainingPercent,
    long? ReallocatedSectors,
    long? PendingSectors);

/// <summary>
/// Leitet aus SMART-Werten die Ampel ab. Reine Funktion, keine WMI-Berührung.
///
/// <para><b>Grundregel:</b> nur BEKANNTE Überschreitungen lösen eine Warnung aus. Ein nicht
/// geliefertes Attribut ist kein Verdachtsmoment – sonst würde jedes USB-Gehäuse rot leuchten.</para>
///
/// <para><b>Reihenfolge ist bindend:</b> Rot schlägt Gelb. Wer eine Warnung sieht, obwohl das
/// Laufwerk ausfällt, sichert nicht.</para>
/// </summary>
public static class SmartHealthEvaluator
{
    /// <summary>
    /// Zieht die ampelrelevanten Werte aus dem dekodierten Blob. Die Temperatur steht im
    /// untersten Rohwert-Byte (obere Bytes halten je nach Firmware Min/Max).
    /// Restlebensdauer: <c>SsdLifeLeft</c> (0xE7) ist der verlässlichere Wert und gewinnt gegen
    /// <c>WearLeveling</c> (0xB1); beide stehen im normalisierten Wert, nicht im Rohwert.
    /// </summary>
    public static SmartReadings ReadAmpelValues(SmartAttributeSet set, bool predictFailure)
    {
        SmartAttribute? temperature = set.Find(SmartAttributeParser.IdTemperature);
        SmartAttribute? lifeLeft = set.Find(SmartAttributeParser.IdSsdLifeLeft)
                                   ?? set.Find(SmartAttributeParser.IdWearLeveling);

        return new SmartReadings(
            PredictFailure: predictFailure,
            TemperatureCelsius: temperature is null ? null : (int)(temperature.Raw & 0xFF),
            WearRemainingPercent: lifeLeft?.Normalized,
            ReallocatedSectors: set.Find(SmartAttributeParser.IdReallocated)?.Raw,
            PendingSectors: set.Find(SmartAttributeParser.IdPending)?.Raw);
    }

    /// <summary>
    /// Bildet die Werte auf die Ampelstufe ab.
    ///
    /// <para><b>Warnen ohne entwarnen zu können:</b> Erst Rot, dann Gelb, DANN erst die Frage,
    /// ob überhaupt ein aussagekräftiger Wert vorliegt. Ein Laufwerk, von dem wir nur 60 °C
    /// kennen, ergibt <c>Warning</c> – wir wissen, dass es zu heiß ist. Dasselbe Laufwerk mit
    /// 38 °C ergibt <c>Unknown</c> – wir wissen schlicht nichts. Das ist kein Widerspruch,
    /// sondern Absicht.</para>
    /// </summary>
    /// <param name="reported">Der von Windows gemeldete Zustand aus <c>MSFT_PhysicalDisk</c>.</param>
    public static SmartHealthLevel Evaluate(SmartReadings r, ReportedHealthStatus reported)
    {
        // Rot zuerst.
        if (r.PredictFailure) return SmartHealthLevel.Failing;
        if (reported == ReportedHealthStatus.Unhealthy) return SmartHealthLevel.Failing;
        if (r.ReallocatedSectors >= SmartThresholds.ReallocatedCritical) return SmartHealthLevel.Failing;
        if (r.WearRemainingPercent <= SmartThresholds.WearCriticalPercent
            && r.WearRemainingPercent.HasValue) return SmartHealthLevel.Failing;

        // Dann Gelb.
        if (reported == ReportedHealthStatus.Warning) return SmartHealthLevel.Warning;
        if (r.ReallocatedSectors >= SmartThresholds.ReallocatedWarn) return SmartHealthLevel.Warning;
        if (r.PendingSectors >= SmartThresholds.PendingWarn) return SmartHealthLevel.Warning;
        if (r.TemperatureCelsius >= SmartThresholds.TemperatureWarnCelsius) return SmartHealthLevel.Warning;
        if (r.WearRemainingPercent <= SmartThresholds.WearWarnPercent
            && r.WearRemainingPercent.HasValue) return SmartHealthLevel.Warning;

        // Grün nur, wenn ein ENTSCHEIDENDES Attribut vorliegt. Die Temperatur zählt bewusst
        // nicht dazu: sie sagt über die Ausfallnähe nichts aus. Eine Platte, deren Firmware im
        // Blob nur die Temperatur führt (USB-Bridges, manche Controller), bekäme sonst
        // „Gesund“ – auch mit tausenden neu zugewiesenen Sektoren, die wir nur nicht sehen.
        bool hasDecisiveReading = r.ReallocatedSectors.HasValue || r.PendingSectors.HasValue
                                  || r.WearRemainingPercent.HasValue
                                  || reported != ReportedHealthStatus.Unknown;

        return hasDecisiveReading ? SmartHealthLevel.Ok : SmartHealthLevel.Unknown;
    }
}
