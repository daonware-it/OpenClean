using OpenClean.Services.UI;

namespace OpenClean.Services.Licensing;

/// <summary>
/// Gemeinsames Upsell-Gate für gesperrte Pro-Funktionen. Zentralisiert das bislang in mehreren
/// ViewModels wiederholte Muster „ist gesperrt → Bestätigungsdialog anbieten → bei Ja den
/// Aktivierungsdialog zeigen → danach erneut prüfen“, damit Aufrufer einheitlich entweder abbrechen
/// oder auf den kostenlosen Pfad zurückfallen können – nie hart blockieren.
/// </summary>
public static class PremiumGate
{
    /// <summary>
    /// Zeigt bei gesperrtem Feature den Upsell an und gibt zurück, ob das Feature DANACH noch
    /// gesperrt ist. Ist es bereits freigeschaltet, passiert nichts und es wird <c>false</c>
    /// zurückgegeben. <paramref name="isLocked"/> wird vor UND nach dem Dialog frisch ausgewertet
    /// (der Zustand kann sich durch eine Aktivierung im Dialog ändern).
    /// </summary>
    public static bool StillLockedAfterUpsell(IDialogService dialogs, Func<bool> isLocked, string lockedMessageKey)
    {
        if (!isLocked()) return false;

        if (dialogs.ConfirmThemed(
                Loc.T(lockedMessageKey), Loc.T("premium.locked.title"), Loc.T("premium.action.activate")))
            dialogs.ActivateLicense();

        return isLocked();
    }
}
