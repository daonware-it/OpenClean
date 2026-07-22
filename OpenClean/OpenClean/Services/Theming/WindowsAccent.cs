using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenClean.Services.Theming;

/// <summary>
/// Modifier „Windows-Akzentfarbe übernehmen": liest die aktuelle Windows-Akzentfarbe,
/// leitet die abgeleiteten Töne (dunkler/weich) ab und meldet Live-Änderungen, wenn der
/// Nutzer die Akzentfarbe in den Windows-Einstellungen ändert.
/// </summary>
public static class WindowsAccent
{
    // Ein dauerhaft gehaltenes UISettings-Objekt: nur solange Referenzen bestehen, feuert
    // ColorValuesChanged zuverlässig.
    private static Windows.UI.ViewManagement.UISettings? _settings;
    private static Action? _onChanged;

    /// <summary>
    /// Leitet aus einer Akzentfarbe die in der App genutzten Varianten ab:
    /// dark = ~18 % abgedunkelt (voll deckend), soft = Akzent mit Alpha 0x26.
    /// </summary>
    public static (Color accent, Color dark, Color soft) Derive(Color accent)
    {
        Color dark = Color.FromArgb(0xFF,
            (byte)Math.Round(accent.R * 0.82), (byte)Math.Round(accent.G * 0.82), (byte)Math.Round(accent.B * 0.82));
        Color soft = Color.FromArgb(0x26, accent.R, accent.G, accent.B);
        return (accent, dark, soft);
    }

    /// <summary>Aktuelle Windows-Akzentfarbe; Fallback auf die Glasrahmenfarbe.</summary>
    public static Color GetSystemAccent()
    {
        try
        {
            var ui = _settings ??= new Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch
        {
            return SystemParameters.WindowGlassColor;
        }
    }

    /// <summary>Abonniert Windows-Akzentänderungen; <paramref name="onChanged"/> läuft auf dem UI-Dispatcher.</summary>
    public static void StartListening(Action onChanged)
    {
        _onChanged = onChanged;
        try
        {
            _settings ??= new Windows.UI.ViewManagement.UISettings();
            _settings.ColorValuesChanged -= OnColorValuesChanged;
            _settings.ColorValuesChanged += OnColorValuesChanged;
        }
        catch
        {
            // WinRT nicht verfügbar -> kein Live-Update; die einmalige Farbe bleibt.
        }
    }

    /// <summary>Beendet das Abonnement (Modifier deaktiviert).</summary>
    public static void StopListening()
    {
        _onChanged = null;
        if (_settings is not null)
            _settings.ColorValuesChanged -= OnColorValuesChanged;
    }

    private static void OnColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        var cb = _onChanged;
        if (cb is null) return;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, cb);
    }
}
