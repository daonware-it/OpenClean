using System.IO;
using System.Threading;

namespace OpenClean.Services;

/// <summary>
/// Zeigt nach einer automatischen Reinigung eine Windows-Benachrichtigung.
///
/// Umsetzung über einen Infobereich-Ballon (<see cref="System.Windows.Forms.NotifyIcon"/>):
/// Diese erscheint – anders als die moderne WinRT-Toast-API – auch bei einer NICHT als
/// Paket installierten App zuverlässig (unter Windows 10/11 wird der Ballon ohnehin als
/// Benachrichtigung im Info-Center dargestellt) und benötigt KEINE registrierte
/// AppUserModelID. Der gesamte Vorgang ist gekapselt – schlägt er fehl, bleibt der
/// gespeicherte Bericht die verlässliche Informationsquelle.
///
/// Wird ausschließlich am Ende des unbeaufsichtigten Laufs (<c>--auto</c>) aufgerufen;
/// <see cref="Show"/> wartet daher kurz auf die Zustellung, bevor sich der Prozess beendet.
/// </summary>
public sealed class ToastService
{
    // Wie lange der Ballon angezeigt bzw. das Icon sichtbar gehalten wird (Zustellung).
    private static readonly TimeSpan DisplayTime = TimeSpan.FromSeconds(5);

    /// <summary>Zeigt eine Benachrichtigung mit Titel und Text (Fehler werden verschluckt).</summary>
    public void Show(string title, string message)
    {
        try
        {
            // NotifyIcon erfordert einen STA-Thread. Wir warten auf die Zustellung, weil der
            // --auto-Prozess sich unmittelbar danach beendet (sonst verschwindet der Ballon).
            var thread = new Thread(() => ShowBalloon(title, message))
            {
                IsBackground = true,
                Name = "OpenCleanToast"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(DisplayTime + TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Benachrichtigung ist optional -> nie fatal.
        }
    }

    private static void ShowBalloon(string title, string message)
    {
        System.Drawing.Icon? icon = null;
        try
        {
            icon = LoadAppIcon();
            using var notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText = message,
                BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info
            };

            notifyIcon.ShowBalloonTip((int)DisplayTime.TotalMilliseconds);
            // Icon sichtbar halten, bis der Ballon zugestellt wurde, dann sauber entfernen.
            Thread.Sleep(DisplayTime);
            notifyIcon.Visible = false;
        }
        catch
        {
            // Info-Center/Richtlinie nicht verfügbar -> still ignorieren.
        }
        finally
        {
            icon?.Dispose();
        }
    }

    /// <summary>Lädt das Programmsymbol (aus der laufenden EXE) mit systemweitem Rückfall.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch
        {
            // Rückfall unten.
        }
        return System.Drawing.SystemIcons.Application;
    }
}
