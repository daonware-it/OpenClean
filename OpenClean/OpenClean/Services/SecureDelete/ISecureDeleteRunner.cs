using OpenClean.Models;

namespace OpenClean.Services.SecureDelete;

/// <summary>
/// Fähigkeit: überschreibt Dateien mehrfach und löscht sie unwiderruflich. Analog zu
/// <see cref="IBatchUninstallRunner"/> liegt die Ausführung ausschließlich im geschlossenen
/// Premium-Modul – die offene App delegiert nur.
///
/// Das Interface lebt bewusst hier (offene App) statt in <c>OpenClean.Contracts</c>: Es reicht
/// App-Modelle (<see cref="SecureDeleteOptions"/>, <see cref="SecureDeleteResult"/>) durch, und der
/// Vertrag darf keine App-Modelle referenzieren. Das Modul referenziert die App-Assembly ohnehin
/// direkt und implementiert dieses Interface.
/// </summary>
public interface ISecureDeleteRunner
{
    /// <summary>Überschreibt Dateien mehrfach und löscht sie unwiderruflich. Nur im Premium-Modul implementiert.</summary>
    Task<SecureDeleteResult> SecureDeleteAsync(
        IReadOnlyList<string> paths, SecureDeleteOptions options,
        IProgress<SecureDeleteProgress>? progress, CancellationToken ct);
}
