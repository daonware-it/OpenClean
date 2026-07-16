using System.Diagnostics;

namespace OpenClean.Services.Integrity;

/// <summary>
/// Orchestriert die Selbstprüfung beim Start (OPCL-20): eigene EXE verifizieren, Politik
/// anwenden, <see cref="IntegrityState"/> setzen. Wirft nie – ein Fehler in der Prüfung
/// selbst führt zu "vertrauenswürdig", nicht zu einer unbenutzbaren App.
/// </summary>
public static class IntegrityGuard
{
    /// <summary>Herausgeber, dem OpenClean vertraut (Subject-CN der Signatur).</summary>
    public const string OwnPublisher = "DaonWare";

    /// <summary>
    /// Optionale Fingerabdruck-Sperrliste. Bewusst LEER: Azure Trusted Signing stellt für jede
    /// Signierung ein frisches, nur wenige Tage gültiges Zertifikat aus. Ein fest verdrahteter
    /// Fingerabdruck würde jedes Release kurz nach der Veröffentlichung als "fremd signiert"
    /// abstempeln. Maßgeblich ist deshalb der Herausgebername.
    /// </summary>
    private static readonly string[] PinnedThumbprints = [];

    private static bool _initialized;

    /// <summary>
    /// Prüft die eigene EXE und setzt den prozessweiten Zustand. Idempotent.
    /// Liefert die Reaktion, damit der Aufrufer bei <see cref="IntegrityAction.Block"/> beenden kann.
    /// </summary>
    public static IntegrityAction Initialize()
    {
        if (_initialized) return CurrentAction();
        _initialized = true;

        try
        {
            bool devBuild = IsDeveloperBuild();

            // Environment.ProcessPath, NICHT Assembly.Location: im Single-File-Build ist
            // Location leer.
            var result = devBuild
                ? new IntegrityResult(IntegritySignatureStatus.Valid, OwnPublisher, "", 0)
                : IntegrityVerifier.Verify(Environment.ProcessPath);

            var ctx = new IntegrityContext(result.Status, IsOwnPublisher(result), devBuild);
            var action = IntegrityPolicy.Decide(ctx);

            IntegrityState.Set(
                level: action == IntegrityAction.Allow ? IntegrityLevel.Trusted : IntegrityLevel.Degraded,
                blocked: action is IntegrityAction.WarnAndLock or IntegrityAction.Block,
                reasonKey: action == IntegrityAction.Allow ? "" : IntegrityPolicy.ReasonKey(result.Status),
                selfStatus: result.Status);

            return action;
        }
        catch (Exception)
        {
            // Die Prüfung darf den Start niemals verhindern.
            IntegrityState.Set(IntegrityLevel.Trusted, blocked: false, reasonKey: "", IntegritySignatureStatus.Error);
            return IntegrityAction.Allow;
        }
    }

    private static IntegrityAction CurrentAction()
    {
        if (!IntegrityState.IsDegraded) return IntegrityAction.Allow;
        return IntegrityState.IsBlocked ? IntegrityAction.WarnAndLock : IntegrityAction.Warn;
    }

    /// <summary>
    /// Prüft eine nachgeladene DLL (Premium-Modul): muss gültig UND von DaonWare signiert sein.
    /// </summary>
    public static bool IsTrustedModule(string path)
    {
        try
        {
            var result = IntegrityVerifier.Verify(path);
            return result.Status == IntegritySignatureStatus.Valid && IsOwnPublisher(result);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True, wenn die Signatur von DaonWare stammt.</summary>
    private static bool IsOwnPublisher(IntegrityResult result)
    {
        if (PinnedThumbprints.Length > 0 &&
            PinnedThumbprints.Contains(result.Thumbprint, StringComparer.OrdinalIgnoreCase))
            return true;

        return result.Publisher.Contains(OwnPublisher, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True, wenn OpenClean erkennbar aus einer Entwicklungsumgebung läuft. Dann wird nicht geprüft,
    /// denn ein lokaler Build ist naturgemäß unsigniert.
    /// </summary>
    public static bool IsDeveloperBuild()
    {
#if DEBUG
        return true;
#else
        return Debugger.IsAttached || LooksLikeDeveloperLayout(AppContext.BaseDirectory);
#endif
    }

    /// <summary>
    /// True, wenn der Pfad nach einem Build-Ausgabeordner aussieht (…\bin\Debug\…, …\bin\Release\…).
    /// Ausgeliefert wird nie aus einem solchen Ordner.
    /// </summary>
    internal static bool LooksLikeDeveloperLayout(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return false;

        // Normalisiert Trennzeichen und garantiert den abschließenden Trenner, damit ein Ordner,
        // der zufällig "…\bin" heißt, nicht fälschlich als Build-Ausgabe zählt.
        string path = baseDirectory.Replace('/', '\\');
        if (!path.EndsWith('\\')) path += '\\';

        return path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }
}
