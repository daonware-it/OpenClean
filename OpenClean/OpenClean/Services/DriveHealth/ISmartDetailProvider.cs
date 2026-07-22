namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Die Detailwerte der Pro-Ansicht. Nullable heißt „vom Laufwerk nicht geliefert“.
/// </summary>
/// <param name="TotalBytesWritten">Insgesamt geschriebene Datenmenge (TBW), nur bei SSDs.</param>
public sealed record SmartDetails(
    int? TemperatureCelsius,
    int? WearRemainingPercent,
    long? PowerOnHours,
    long? PowerCycles,
    long? ReallocatedSectors,
    long? PendingSectors,
    long? TotalBytesWritten);

/// <summary>
/// Fähigkeit: dekodiert den rohen SMART-Blob zu den Detailwerten der Pro-Ansicht.
/// Analog zu <c>IBatchUninstallRunner</c> liegt die Auswertung ausschließlich im
/// geschlossenen Premium-Modul – die offene App delegiert nur.
///
/// <para>Das Interface lebt bewusst hier (offene App) statt in <c>OpenClean.Contracts</c>: Es
/// reicht <see cref="DriveMediaType"/> durch, und der Vertrag darf keine App-Modelle
/// referenzieren. Das Modul referenziert die App-Assembly ohnehin direkt.</para>
///
/// <para><b>Additiv, kein Vertragsbruch:</b> <c>PremiumContract.Version</c> bleibt bei 1. Ein
/// älteres Modul implementiert dieses Interface schlicht nicht – dann zeigt die Detailansicht
/// „nicht verfügbar“, während Zeitplan und Batch-Deinstallation unberührt weiterlaufen. Ein
/// Versions-Bump würde stattdessen das ausgelieferte Modul komplett ablehnen.</para>
///
/// <para><b>Bekannte Grenze:</b> Die vier Ampel-Werte (Temperatur, Abnutzung, neu zugewiesene
/// und schwebende Sektoren) dekodiert der offene Kern für die Ampel ohnehin selbst. Exklusiv
/// aus dem Modul kommen Betriebsstunden, Einschaltzyklen und TBW.</para>
/// </summary>
public interface ISmartDetailProvider
{
    /// <summary>
    /// Dekodiert den Blob. Prüft Lizenz und Feature selbst; ohne Berechtigung <c>null</c>.
    /// Liefert auch <c>null</c>, wenn der Blob nichts Verwertbares enthält.
    /// </summary>
    SmartDetails? GetDetails(byte[] rawSmartBlob, string model, DriveMediaType mediaType);
}
