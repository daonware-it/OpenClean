using System.Windows;
using System.Windows.Interop;
using OpenClean.Services;
using OpenClean.Services.Safety;
using OpenClean.Views;

namespace OpenClean.ViewModels;

/// <summary>Ergebnis der interaktiven Sicherheits-Vorbereitung vor einer Löschaktion.</summary>
/// <param name="Proceed">True, wenn die Aktion fortgesetzt werden darf.</param>
/// <param name="Session">Backup-Sitzung für den Lauf (oder null, wenn Datei-Sicherung abgeschaltet).</param>
public readonly record struct SafetyPreparation(bool Proceed, BackupSession? Session);

/// <summary>
/// Interaktive Vorbereitung der Sicherheitsnetze für Löschaktionen aus dem UI. Erstellt – sofern
/// aktiviert – den Wiederherstellungspunkt und bietet bei Fehlschlag eine Rückfrage
/// („trotzdem fortfahren?") an. Danach wird die Backup-Sitzung eröffnet. Wird von allen
/// löschenden ViewModels (Bereinigung, Duplikate, Leftover, Privatsphäre) gemeinsam genutzt,
/// damit das Verhalten überall identisch ist.
/// </summary>
public static class SafetyPrompt
{
    /// <summary>
    /// Führt die Vorbereitung aus. <paramref name="status"/> erhält ggf. Zwischenmeldungen
    /// (z. B. „Wiederherstellungspunkt wird erstellt …"). Gibt <c>Proceed = false</c> zurück,
    /// wenn der Nutzer nach einem fehlgeschlagenen Wiederherstellungspunkt abbricht.
    /// </summary>
    public static async Task<SafetyPreparation> PrepareAsync(Window? owner, string area, Action<string>? status = null)
    {
        if (!await EnsureRestorePointAsync(owner, status))
            return new SafetyPreparation(false, null);

        BackupSession? session = null;
        if (SettingsService.Instance.Current.Safety.BackupBeforeDelete)
        {
            // Handle des Eigentümerfensters für einen etwaigen Papierkorb-Nuke-Dialog beschaffen.
            IntPtr hwnd = owner is not null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
            session = BackupService.Instance.BeginSession(area, hwnd);
        }

        return new SafetyPreparation(true, session);
    }

    /// <summary>
    /// Nur der Wiederherstellungspunkt-Teil (ohne Backup-Sitzung): erstellt – sofern aktiviert –
    /// den Punkt und fragt bei Fehlschlag „trotzdem fortfahren?". Gibt <c>false</c> zurück, wenn der
    /// Nutzer abbricht. Für Bereiche, die ihr Datei-Backup selbst kapseln (z. B. Privatsphäre-Provider).
    /// </summary>
    public static async Task<bool> EnsureRestorePointAsync(Window? owner, Action<string>? status = null)
    {
        if (!SettingsService.Instance.Current.Safety.CreateRestorePoint)
            return true;

        status?.Invoke(Loc.T("safety.restore.creating"));

        RestorePointResult result = await Task.Run(
            () => RestorePointService.Instance.TryCreate(Loc.T("safety.restore.description")));

        if (result.IsSafeToContinue)
            return true;

        return ConfirmDialog.Show(
            owner,
            Loc.T("safety.restore.gate.body", result.Message),
            Loc.T("safety.restore.gate.title"),
            Loc.T("safety.restore.gate.continue"));
    }
}
