using System.Threading;

namespace OpenClean.Services;

/// <summary>
/// Führt Shell-Aktionen auf einem dedizierten STA-Thread aus. Viele Shell-APIs
/// (<c>SHFileOperation</c>, <c>SHEmptyRecycleBin</c>) verlangen ein STA-Apartment und pumpen
/// darin Fenster-Nachrichten; ein Hintergrund-/MTA-Thread genügt dafür nicht.
/// </summary>
internal static class StaThreadRunner
{
    /// <summary>
    /// Führt eine Shell-Aktion auf einem dedizierten STA-Thread aus und wartet höchstens
    /// <paramref name="timeoutMs"/> ms. Liefert false bei Zeitüberschreitung. Eine im Thread
    /// aufgetretene Ausnahme wird im Aufrufer erneut geworfen.
    /// </summary>
    internal static bool RunSta(Action action, int timeoutMs)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        })
        {
            IsBackground = true,
            Name = "RecycleBinShell"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeoutMs))
            return false;

        if (captured is not null)
            throw captured;
        return true;
    }

    /// <summary>
    /// Führt eine Shell-Aktion auf einem dedizierten STA-Thread aus und wartet UNBEGRENZT auf
    /// deren Ende. Bewusst ohne Zeitlimit: wird für Operationen benutzt, die auf eine menschliche
    /// Reaktion auf einen Windows-Dialog warten können (z. B. die Nuke-Warnung bei
    /// <see cref="RecycleBin.MoveToRecycleBin"/>) – ein Timeout würde hier nur den gemeldeten Status
    /// verfälschen, ohne den Dialog tatsächlich zu schließen. Eine im Thread aufgetretene
    /// Ausnahme wird im Aufrufer erneut geworfen.
    /// </summary>
    internal static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        })
        {
            IsBackground = true,
            Name = "RecycleBinShell"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
            throw captured;
    }
}
