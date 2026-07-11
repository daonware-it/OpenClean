using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenClean.Services;

/// <summary>
/// Extrahiert Programm-Icons für die Deinstallations-Liste aus dem DisplayIcon-Pfad
/// (Haupt-EXE bzw. .ico). Ergebnisse sind eingefroren (<c>Freeze()</c>), damit sie im
/// Hintergrund erzeugt und thread-übergreifend an die UI übergeben werden können.
/// Prozessweiter Cache verhindert wiederholte (teure) Extraktion.
/// </summary>
public static class AppIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icon für den Pfad, aus dem Cache oder frisch extrahiert. <c>null</c>, wenn nicht möglich.</summary>
    public static ImageSource? GetIcon(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        return Cache.GetOrAdd(iconPath, Extract);
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            // .ico direkt laden (mehrere Auflösungen; OnLoad = sofort dekodieren, Datei freigeben).
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                var frame = BitmapFrame.Create(new Uri(path),
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (frame.CanFreeze) frame.Freeze();
                return frame;
            }

            // .exe/.dll: assoziiertes Icon (GDI) → WPF-BitmapSource.
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null; // Icon nicht extrahierbar → Aufrufer zeigt Buchstaben-Avatar
        }
    }
}
