namespace OpenClean.Services.DriveHealth;

/// <summary>Ein einzelnes SMART-Attribut aus dem Vendor-Blob.</summary>
/// <param name="Id">Attribut-Kennung, z. B. 0x05 für neu zugewiesene Sektoren.</param>
/// <param name="Normalized">Aktueller normalisierter Wert (100 = wie neu, 0 = am Limit).</param>
/// <param name="Raw">Roher Zählerstand (6 Byte, little endian).</param>
public sealed record SmartAttribute(byte Id, byte Normalized, long Raw);

/// <summary>Alle aus einem Blob dekodierten Attribute, per Id abfragbar.</summary>
public sealed class SmartAttributeSet
{
    private readonly IReadOnlyDictionary<byte, SmartAttribute> _byId;

    internal SmartAttributeSet(IReadOnlyDictionary<byte, SmartAttribute> byId) => _byId = byId;

    /// <summary>Liefert das Attribut oder <c>null</c>, wenn das Laufwerk es nicht meldet.</summary>
    public SmartAttribute? Find(byte id) => _byId.TryGetValue(id, out SmartAttribute? a) ? a : null;

    /// <summary>True, wenn der Blob kein einziges verwertbares Attribut enthielt.</summary>
    public bool IsEmpty => _byId.Count == 0;
}

/// <summary>
/// Dekodiert den rohen 512-Byte-Blob aus <c>MSStorageDriver_ATAPISmartData.VendorSpecific</c>.
///
/// <para><b>Aufbau:</b> 2 Byte Version, danach bis zu 30 Einträge à 12 Byte:
/// Id (1), Flags (2), Normalized (1), Worst (1), Raw (6, little endian), Reserved (1).</para>
///
/// <para><b>Bewusst reine Funktion</b> ohne WMI-Berührung: die Zuordnung von Bytes zu Bedeutung
/// ist die fehleranfälligste Stelle des Features und muss ohne Hardware testbar sein.</para>
///
/// <para><b>Umfang:</b> Der offene Kern dekodiert hier ALLE Attribute generisch, wertet aber nur
/// die für die Ampel nötigen aus (<see cref="SmartHealthEvaluator"/>). Die herstellerbewusste
/// Deutung der übrigen liegt im Premium-Modul.</para>
/// </summary>
public static class SmartAttributeParser
{
    /// <summary>Reallocated Sectors Count – neu zugewiesene fehlerhafte Sektoren.</summary>
    public const byte IdReallocated = 0x05;

    /// <summary>Power-On Hours – Betriebsstunden.</summary>
    public const byte IdPowerOnHours = 0x09;

    /// <summary>Power Cycle Count – Einschaltzyklen.</summary>
    public const byte IdPowerCycles = 0x0C;

    /// <summary>Wear Leveling Count – Abnutzung (herstellerabhängig, meist Samsung).</summary>
    public const byte IdWearLeveling = 0xB1;

    /// <summary>Temperature – Laufwerkstemperatur in °C (im untersten Rohwert-Byte).</summary>
    public const byte IdTemperature = 0xC2;

    /// <summary>Current Pending Sector Count – schwebende Sektoren.</summary>
    public const byte IdPending = 0xC5;

    /// <summary>SSD Life Left – verbleibende Lebensdauer in Prozent (normalisierter Wert).</summary>
    public const byte IdSsdLifeLeft = 0xE7;

    /// <summary>Total LBAs Written – Basis für TBW (241).</summary>
    public const byte IdTotalLbaWritten = 0xF1;

    private const int HeaderLength = 2;
    private const int EntryLength = 12;
    private const int RawLength = 6;

    /// <summary>
    /// Dekodiert den Blob. Liefert bei <c>null</c>, zu kurzem oder unbelegtem Blob ein leeres
    /// Set – nie eine Ausnahme: ein Laufwerk, das Unsinn zurückgibt, ist ein Anzeigefall
    /// („Nicht verfügbar“), kein Fehlerfall.
    /// </summary>
    public static SmartAttributeSet Parse(byte[]? blob)
    {
        var byId = new Dictionary<byte, SmartAttribute>();
        if (blob is null || blob.Length < HeaderLength + EntryLength)
            return new SmartAttributeSet(byId);

        for (int offset = HeaderLength; offset + EntryLength <= blob.Length; offset += EntryLength)
        {
            byte id = blob[offset];

            // Id 0 = nicht belegter Slot. Kein Attribut, kein Fehler.
            if (id == 0) continue;

            long raw = 0;
            for (int b = 0; b < RawLength; b++)
                raw |= (long)blob[offset + 5 + b] << (8 * b);

            // Erstes Vorkommen gewinnt; doppelte Ids kommen bei defekter Firmware vor.
            byId.TryAdd(id, new SmartAttribute(id, blob[offset + 3], raw));
        }

        return new SmartAttributeSet(byId);
    }
}
