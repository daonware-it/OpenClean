using OpenClean.Models;

namespace OpenClean.Services.Duplicates;

/// <summary>
/// Fähigkeit: findet visuell ähnliche Bilder (Perceptual-Hash). Analog zu
/// <see cref="IBatchUninstallRunner"/> liegt die Ausführung ausschließlich im
/// geschlossenen Premium-Modul – die offene App delegiert nur.
///
/// Das Interface lebt bewusst hier (offene App) statt in <c>OpenClean.Contracts</c>: Es reicht
/// App-Modelle (<see cref="FuzzyDuplicateGroup"/>, <see cref="FuzzyScanOptions"/>) durch, und der
/// Vertrag darf keine App-Modelle referenzieren. Das Modul referenziert die App-Assembly ohnehin
/// direkt und implementiert dieses Interface.
///
/// Die exakte (inhaltsgleiche) Duplikatsuche bleibt kostenlos in der offenen App
/// (<see cref="DuplicateScannerService"/>); nur die Ähnlichkeitssuche ist Premium.
/// </summary>
public interface IFuzzyDuplicateRunner
{
    /// <summary>Findet visuell ähnliche Bilder (Perceptual-Hash) in den Ordnern. Nur im Premium-Modul implementiert.</summary>
    Task<IReadOnlyList<FuzzyDuplicateGroup>> ScanAsync(
        IReadOnlyList<string> folders, FuzzyScanOptions options,
        IProgress<DuplicateScanProgress>? progress, CancellationToken ct);
}
