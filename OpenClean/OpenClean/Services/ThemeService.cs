using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace OpenClean.Services;

/// <summary>Verfügbare Anwendungs-Themes.</summary>
public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Verwaltet das Laufzeit-Theme (Hell/Dunkel). Tauscht das aktive Theme-
/// ResourceDictionary in <see cref="Application.Resources"/> aus, sodass alle
/// über DynamicResource referenzierten Brushes live aktualisiert werden.
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _currentThemeDict;

    /// <summary>Das aktuell aktive Theme.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    private static Uri UriFor(AppTheme theme) => new(
        theme == AppTheme.Light ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml",
        UriKind.Relative);

    /// <summary>
    /// Wendet das angegebene Theme an. Das bisherige Theme-Dictionary wird an
    /// gleicher Position durch das neue ersetzt.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var newDict = new ResourceDictionary { Source = UriFor(theme) };
        var dicts = app.Resources.MergedDictionaries;

        // Referenz auf das bisherige Theme-Dictionary bevorzugen ...
        var oldDict = _currentThemeDict;

        // ... andernfalls anhand der Source ein vorhandenes Theme-Dictionary finden.
        oldDict ??= dicts.FirstOrDefault(d =>
            d.Source is { } s &&
            (s.OriginalString.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
             s.OriginalString.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)));

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
    }

    /// <summary>Schaltet zwischen Hell und Dunkel um und liefert das neue Theme.</summary>
    public static AppTheme Toggle()
    {
        ApplyTheme(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
        return Current;
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
