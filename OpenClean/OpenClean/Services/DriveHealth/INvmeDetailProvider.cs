namespace OpenClean.Services.DriveHealth;

/// <summary>
/// Fähigkeit: dekodiert den rohen NVMe-Health-Log (512 Byte, Log-Page 0x02) zu den Detailwerten
/// der Pro-Ansicht. Analog zu <see cref="ISmartDetailProvider"/> (ATA) liegt die Auswertung
/// ausschließlich im geschlossenen Premium-Modul – die offene App liest den Log nur
/// (<see cref="INvmeHealthReader"/>) und delegiert die Dekodierung.
///
/// <para><b>Additiv, kein Vertragsbruch:</b> <c>PremiumContract.Version</c> bleibt 1. Ein älteres
/// Modul implementiert dieses Interface schlicht nicht – dann bleiben die exklusiven NVMe-Werte
/// (Einschaltzyklen, TBW, exakte Betriebsstunden) aus, und die Ansicht fällt auf die frei
/// verfügbaren Zuverlässigkeitszähler-Werte zurück. ATA-Details, Zeitplan und Batch-Deinstallation
/// bleiben unberührt.</para>
/// </summary>
public interface INvmeDetailProvider
{
    /// <summary>
    /// Dekodiert den NVMe-Health-Log zu <see cref="SmartDetails"/>. Prüft Lizenz und Feature
    /// selbst; ohne Berechtigung <c>null</c>. Liefert auch <c>null</c>, wenn der Log nichts
    /// Verwertbares enthält.
    /// </summary>
    SmartDetails? GetNvmeDetails(byte[] nvmeHealthLog, string model);
}
