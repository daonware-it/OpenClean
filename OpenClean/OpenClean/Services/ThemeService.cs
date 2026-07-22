using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenClean.Services.Theming;

namespace OpenClean.Services;

/// <summary>Verfügbare Basis-Paletten (Achse 1 des Theme-Systems).</summary>
public enum AppTheme
{
    Dark,
    Light,
    Cyberpunk,
    GamingRgb,
    Nord,
    Catppuccin,
    Dracula,
    Dev,
    Windows11
}

/// <summary>
/// Verwaltet das Laufzeit-Theme (Hell/Dunkel). Tauscht das aktive Theme-
/// ResourceDictionary in <see cref="Application.Resources"/> aus, sodass alle
/// über DynamicResource referenzierten Brushes live aktualisiert werden.
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _currentThemeDict;

    /// <summary>Wird nach jeder Palette-/Modifier-Anwendung ausgelöst (UI reagiert: Scanline, Mica, Accent).</summary>
    public static event Action? Changed;

    /// <summary>Das aktuell aktive Theme.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Aktueller Modifier-Zustand (gespiegelt für UI/Fenster).</summary>
    public static bool UseWindowsAccent { get; private set; }
    public static bool UseMica { get; private set; }
    public static bool ReduceMotion { get; private set; }

    private static IntPtr _micaHwnd = IntPtr.Zero;
    private static DispatcherTimer? _rgbTimer;
    private static double _rgbHue;
    private static DispatcherTimer? _neonTimer;
    private static double _neonPhase;

    /// <summary>Registriert das Fenster-Handle für den Mica-Backdrop (MainWindow meldet es).</summary>
    public static void SetMicaTargetHandle(IntPtr hwnd)
    {
        _micaHwnd = hwnd;
        MicaBackdrop.Apply(_micaHwnd, UseMica);
    }

    /// <summary>Wendet Palette + alle Modifier an, persistiert und feuert <see cref="Changed"/>.</summary>
    public static void ApplyAll(AppTheme theme, bool useWindowsAccent, bool useMica, bool reduceMotion)
    {
        UseWindowsAccent = useWindowsAccent;
        UseMica = useMica;
        ReduceMotion = reduceMotion;

        // 1) Palette (ersetzt das erste MergedDictionary, feuert bereits Changed am Ende).
        ApplyTheme(theme); // setzt Current, tauscht Palette-Dictionary

        // 2) WindowBackdropBrush: transparent bei Mica, sonst Palette-Hintergrund.
        ApplyWindowBackdrop();

        // 3) Windows-Akzent-Override (oder Palette-Akzent wiederherstellen).
        if (useWindowsAccent)
        {
            WindowsAccent.StartListening(RefreshWindowsAccent);
            RefreshWindowsAccent();
        }
        else
        {
            WindowsAccent.StopListening();
        }

        // 4) Gaming-RGB-Farbrotation: nur bei GamingRgb, ohne ReduceMotion, ohne Accent-Override.
        UpdateRgbAnimation(theme);

        // 4a) Neon-Puls des Akzent-Glows: nur bei Cyberpunk, ohne ReduceMotion.
        UpdateNeonPulse(theme);

        // 4b) Ist kein Accent-Override (weder Windows-Akzent noch laufender RGB-Timer) mehr aktiv,
        //     müssen zuvor gesetzte Top-Level-Overrides entfernt werden, damit wieder die
        //     Palette-Akzentfarbe aus dem Merged-Dictionary greift.
        bool accentOverridden = useWindowsAccent || (theme == AppTheme.GamingRgb && !reduceMotion && !useWindowsAccent);
        if (!accentOverridden)
            ClearAccentOverride();

        // 5) Mica auf das Fenster anwenden (falls Handle bekannt).
        if (_micaHwnd != IntPtr.Zero)
            MicaBackdrop.Apply(_micaHwnd, useMica);

        // 6) Persistenz.
        var s = SettingsService.Instance.Current;
        s.Theme = theme.ToString();
        s.UseWindowsAccent = useWindowsAccent;
        s.UseMicaBackdrop = useMica;
        s.ReduceMotion = reduceMotion;
        SettingsService.Instance.Save();

        Changed?.Invoke();
    }

    private static void ApplyWindowBackdrop()
    {
        var app = Application.Current;
        if (app is null) return;
        if (UseMica && MicaBackdrop.IsSupported)
        {
            app.Resources["WindowBackdropBrush"] = Brushes.Transparent;
        }
        else if (app.Resources["BgBrush"] is Brush bg)
        {
            app.Resources["WindowBackdropBrush"] = bg;
        }
    }

    /// <summary>Überschreibt Accent/AccentDark/AccentSoft (Color + Brush) mit der Windows-Akzentfarbe.</summary>
    public static void RefreshWindowsAccent()
    {
        var app = Application.Current;
        if (app is null || !UseWindowsAccent) return;

        var (accent, dark, soft) = WindowsAccent.Derive(WindowsAccent.GetSystemAccent());
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentDarkColor"] = dark;
        app.Resources["AccentSoftColor"] = soft;
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentDarkBrush"] = new SolidColorBrush(dark);
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(soft);
    }

    /// <summary>
    /// Entfernt die sechs Top-Level-Accent-Overrides (Color + Brush) aus <see cref="Application.Resources"/>,
    /// falls vorhanden. Danach fällt DynamicResource wieder auf die Werte des gemergten Palette-Dictionaries
    /// zurück, sodass ein zuvor per Windows-Akzent oder Gaming-RGB gesetzter Override nicht dauerhaft
    /// die Palette-Akzentfarbe überschattet.
    /// </summary>
    private static void ClearAccentOverride()
    {
        var app = Application.Current;
        if (app is null) return;

        app.Resources.Remove("AccentColor");
        app.Resources.Remove("AccentDarkColor");
        app.Resources.Remove("AccentSoftColor");
        app.Resources.Remove("AccentBrush");
        app.Resources.Remove("AccentDarkBrush");
        app.Resources.Remove("AccentSoftBrush");
    }

    private static void UpdateRgbAnimation(AppTheme theme)
    {
        bool shouldRun = theme == AppTheme.GamingRgb && !ReduceMotion && !UseWindowsAccent;
        if (shouldRun)
        {
            if (_rgbTimer is null)
            {
                _rgbTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(60)
                };
                _rgbTimer.Tick += (_, _) => RgbTick();
            }
            _rgbTimer.Start();
        }
        else
        {
            _rgbTimer?.Stop();
        }
    }

    private static void RgbTick()
    {
        var app = Application.Current;
        if (app is null) return;
        _rgbHue = (_rgbHue + 1.2) % 360.0;
        var accent = HueToColor(_rgbHue);
        var (_, dark, soft) = WindowsAccent.Derive(accent);
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentDarkColor"] = dark;
        app.Resources["AccentSoftColor"] = soft;
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentDarkBrush"] = new SolidColorBrush(dark);
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(soft);
    }

    private static void UpdateNeonPulse(AppTheme theme)
    {
        bool shouldRun = theme == AppTheme.Cyberpunk && !ReduceMotion;
        if (shouldRun)
        {
            if (_neonTimer is null)
            {
                _neonTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _neonTimer.Tick += (_, _) => NeonTick();
            }
            _neonTimer.Start();
        }
        else
        {
            _neonTimer?.Stop();
            // Statischen Default wiederherstellen, sonst bliebe der letzte Pulswert stehen.
            if (Application.Current is { } app)
                app.Resources["NeonGlowOpacity"] = 0.28;
        }
    }

    private static void NeonTick()
    {
        var app = Application.Current;
        if (app is null) return;
        _neonPhase = (_neonPhase + 0.06) % (2 * Math.PI);
        // Sanfte Sinus-Pulsation zwischen ~0.15 und ~0.55.
        app.Resources["NeonGlowOpacity"] = 0.35 + 0.20 * Math.Sin(_neonPhase);
    }

    /// <summary>Voll gesättigte, helle Farbe (HSV, S=0.85, V=0.98) für den RGB-Zyklus.</summary>
    public static Color HueToColor(double hueDegrees)
    {
        double h = (hueDegrees % 360.0) / 60.0;
        double s = 0.85, v = 0.98;
        double c = v * s;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        switch ((int)Math.Floor(h) % 6)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            case 5: r = c; b = x; break;
        }
        return Color.FromArgb(0xFF,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Relativer Pfad zur Palette-XAML für ein Theme (erstes MergedDictionary).</summary>
    public static Uri PaletteUri(AppTheme theme) => new(
        $"Themes/{theme}Theme.xaml", UriKind.Relative);

    /// <summary>
    /// Wendet das angegebene Theme an. Das bisherige Theme-Dictionary wird an
    /// gleicher Position durch das neue ersetzt.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var newDict = new ResourceDictionary { Source = PaletteUri(theme) };
        var dicts = app.Resources.MergedDictionaries;

        // Referenz auf das bisherige Theme-Dictionary bevorzugen ...
        var oldDict = _currentThemeDict;

        // ... andernfalls anhand der Source ein vorhandenes Theme-Dictionary finden (beliebige Palette).
        oldDict ??= dicts.FirstOrDefault(d =>
            d.Source is { } s &&
            s.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase) &&
            s.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase));

        if (oldDict != null && dicts.Contains(oldDict))
        {
            int idx = dicts.IndexOf(oldDict);
            dicts[idx] = newDict;
        }
        else
        {
            // Theme-Dictionary muss das erste sein.
            dicts.Insert(0, newDict);
        }

        _currentThemeDict = newDict;
        Current = theme;
        // Kein Changed hier: ApplyTheme ist ein interner Helfer von ApplyAll, das das Event
        // erst NACH dem Anwenden aller Modifier einmal mit dem finalen Zustand auslöst.
    }

    /// <summary>
    /// Ermittelt das beim Start anzuwendende Theme: gespeicherte Wahl (settings.json) →
    /// sonst die Windows-App-Modus-Einstellung.
    /// </summary>
    public static AppTheme DetectStartupTheme()
    {
        if (Enum.TryParse(SettingsService.Instance.Current.Theme, out AppTheme saved))
            return saved;
        return DetectWindowsTheme();
    }

    /// <summary>
    /// Liest die Windows-App-Modus-Einstellung (Hell/Dunkel) aus der Registry.
    /// AppsUseLightTheme = 1 => Hell, 0 => Dunkel. Standard: Dunkel.
    /// </summary>
    public static AppTheme DetectWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1 ? AppTheme.Light : AppTheme.Dark;
        }
        catch
        {
            // Registry nicht lesbar -> Standard verwenden.
        }

        return AppTheme.Dark;
    }
}
