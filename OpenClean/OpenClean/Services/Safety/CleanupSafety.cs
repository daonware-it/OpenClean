namespace OpenClean.Services.Safety;

/// <summary>
/// Bündelt die zu einem Bereinigungslauf gehörenden Sicherheitsobjekte, die von außen (z. B. vom
/// ViewModel, das den Wiederherstellungspunkt bereits mit UI-Rückfrage erstellt hat) in den
/// <see cref="CleanerService"/> hineingereicht werden.
/// </summary>
public sealed class CleanupSafetyContext
{
    /// <summary>Backup-Sitzung für diesen Lauf. Ist sie gesetzt, laufen alle Löschungen darüber (mit Undo).</summary>
    public BackupSession? Session { get; init; }

    /// <summary>
    /// True, wenn der Aufrufer möchte, dass der Dienst den Wiederherstellungspunkt selbst erstellt
    /// (unbeaufsichtigter Lauf ohne UI). Interaktive Aufrufer erledigen das vorab und lassen dies false.
    /// </summary>
    public bool CreateRestorePointHere { get; init; }
}

/// <summary>
/// Nicht-UI-Orchestrierung der Sicherheitsnetze. Liest die Einstellungen und stellt
/// Wiederherstellungspunkt bzw. Backup-Sitzung bereit – ohne Dialoge, daher aus jedem Kontext
/// (auch dem unbeaufsichtigten <c>--auto</c>-Lauf) heraus aufrufbar.
/// </summary>
public static class CleanupSafety
{
    /// <summary>
    /// Erstellt – sofern in den Einstellungen aktiviert – einen Wiederherstellungspunkt.
    /// Gibt <c>null</c> zurück, wenn die Funktion abgeschaltet ist; sonst das (evtl. fehlerhafte)
    /// Ergebnis. Blockiert bis zum Resultat; außerhalb des UI-Threads aufrufen.
    /// </summary>
    public static RestorePointResult? EnsureRestorePoint(string area)
    {
        if (!SettingsService.Instance.Current.Safety.CreateRestorePoint) return null;
        return RestorePointService.Instance.TryCreate(Loc.T("safety.restore.description"));
    }
}
