using OpenClean.Models;

namespace OpenClean.Services;

/// <summary>Fortschritt eines Batch-Deinstallationslaufs (fertige Anzahl, Gesamt, aktueller Name).</summary>
public readonly record struct BatchUninstallProgress(int Done, int Total, string CurrentName);

/// <summary>
/// Fähigkeit: führt die Batch-Deinstallation MEHRERER Programme AUS. Analog zu
/// <see cref="OpenClean.Contracts.IScheduledCleanupRunner"/> liegt die Ausführung
/// ausschließlich im geschlossenen Premium-Modul – die offene App delegiert nur.
///
/// Das Interface lebt bewusst hier (offene App) statt in <c>OpenClean.Contracts</c>: Es reicht
/// <see cref="InstalledApp"/> durch, und der Vertrag darf keine App-Modelle referenzieren. Das
/// Modul referenziert die App-Assembly ohnehin direkt und implementiert dieses Interface.
///
/// Die kostenlose EINZEL-Deinstallation bleibt in der offenen App; nur die Schleife über
/// mehrere Programme ist Premium und existiert nur im Modul.
/// </summary>
public interface IBatchUninstallRunner
{
    /// <summary>
    /// Deinstalliert die übergebenen Programme nacheinander (still). Prüft Lizenz und
    /// Batch-Feature selbst; ohne gültige Berechtigung wird nichts entfernt. Meldet den
    /// Fortschritt und liefert die tatsächlich entfernten Programme zurück.
    /// </summary>
    Task<IReadOnlyList<InstalledApp>> RunBatchUninstallAsync(
        IReadOnlyList<InstalledApp> apps, IProgress<BatchUninstallProgress>? progress);
}
