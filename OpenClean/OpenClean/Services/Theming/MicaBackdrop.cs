using System;
using System.Runtime.InteropServices;

namespace OpenClean.Services.Theming;

/// <summary>
/// Modifier „Mica/Acrylic-Fenster": setzt per DWM einen transluzenten System-Backdrop.
/// Windows 11 22H2+ nutzt die SystemBackdrop-API (Mica), Windows 11 21H2 das Legacy-
/// Mica-Attribut. Auf Windows 10/älter passiert nichts (stiller Fallback = opake Palette).
/// </summary>
public static class MicaBackdrop
{
    private const int DWMWA_MICA_EFFECT = 1029;         // undokumentiert, Win11 21H2 (Build 22000)
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;   // offiziell, Win11 22H2 (Build 22621)
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;            // Mica

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>True, wenn dieses Windows Mica unterstützt (Windows 11, Build ≥ 22000).</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Aktiviert/deaktiviert den Mica-Backdrop für das Fenster-Handle. No-op ohne Unterstützung.</summary>
    public static void Apply(IntPtr hwnd, bool enable)
    {
        if (!IsSupported)
            return;

        try
        {
            if (SupportsSystemBackdrop)
            {
                int value = enable ? DWMSBT_MAINWINDOW : DWMSBT_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
            }
            else
            {
                int value = enable ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref value, sizeof(int));
            }
        }
        catch
        {
            // DWM nicht verfügbar -> Fenster bleibt opak.
        }
    }
}
