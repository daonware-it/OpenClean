using System.Runtime.InteropServices;

namespace OpenClean.Services;

/// <summary>
/// Zeigt (best effort) eine Windows-Benachrichtigung nach einer automatischen Reinigung.
/// Ohne installiertes App-Paket sind Toasts nicht auf jedem System garantiert; deshalb ist
/// der gesamte Vorgang gekapselt – schlägt er fehl, bleibt der gespeicherte Bericht die
/// verlässliche Informationsquelle und die Reinigung selbst ist davon unberührt.
/// </summary>
public sealed class ToastService
{
    // Eine stabile App-User-Model-ID, damit Windows die Benachrichtigung OpenClean zuordnet.
    private const string AppUserModelId = "OpenClean.SystemCleaner";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Zeigt eine einfache Benachrichtigung mit Titel und Text (Fehler werden verschluckt).</summary>
    public void Show(string title, string message)
    {
        try
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
            catch { /* nicht kritisch */ }

            string xml =
                "<toast><visual><binding template='ToastGeneric'>" +
                $"<text>{Escape(title)}</text>" +
                $"<text>{Escape(message)}</text>" +
                "</binding></visual></toast>";

            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);

            var toast = new Windows.UI.Notifications.ToastNotification(doc);
            Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier(AppUserModelId)
                .Show(toast);
        }
        catch
        {
            // Toasts nicht verfügbar (z. B. Richtlinie/unpackaged) -> still ignorieren.
        }
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? "";
}
