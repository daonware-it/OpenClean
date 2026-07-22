using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenClean.Services;
using OpenClean.ViewModels;

namespace OpenClean;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private Storyboard? _scanlineScroll;
    private Storyboard? _caretBlink;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // Maximiert ragt ein Fenster ohne Standard-Chrome sonst über den Bildschirmrand hinaus.
        StateChanged += (_, _) =>
            RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        // Fenster-Effekte an den ThemeService koppeln (Scanline, Mono-Kennzahlen).
        ThemeService.Changed += ApplyWindowEffects;
        Loaded += (_, _) => ApplyWindowEffects();

        // Mica braucht das native Handle -> nach Fenstererzeugung registrieren.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            ThemeService.SetMicaTargetHandle(hwnd);
        };
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>Öffnet den zentralen Einstellungsbereich (Zahnrad in der Titelleiste).</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CurrentSection = AppSection.Einstellungen;
    }

    /// <summary>Steuert die theme-abhängigen Fenster-Effekte: Cyberpunk-Scanline (sichtbar,
    /// mit Scroll außer bei ReduceMotion), Dev-Terminal-Cursor (blinkend außer bei ReduceMotion)
    /// und Monospace-Kennzahlen (Cyberpunk oder Dev).</summary>
    private void ApplyWindowEffects()
    {
        bool cyberpunk = ThemeService.Current == AppTheme.Cyberpunk;
        bool dev = ThemeService.Current == AppTheme.Dev;
        bool reduce = ThemeService.ReduceMotion;

        // Scanline: bei Cyberpunk immer sichtbar; Scroll nur ohne ReduceMotion.
        ScanlineOverlay.Visibility = cyberpunk ? Visibility.Visible : Visibility.Collapsed;
        if (cyberpunk && !reduce)
        {
            _scanlineScroll ??= BuildScanlineScroll();
            _scanlineScroll.Begin(this, true);
        }
        else
        {
            _scanlineScroll?.Stop(this);
        }

        // Dev-Cursor: bei Dev sichtbar; Blinken nur ohne ReduceMotion.
        DevCaret.Visibility = dev ? Visibility.Visible : Visibility.Collapsed;
        if (dev && !reduce)
        {
            _caretBlink ??= BuildCaretBlink();
            _caretBlink.Begin(this, true);
        }
        else
        {
            _caretBlink?.Stop(this);
        }

        // Monospace-Kennzahlen bei Cyberpunk ODER Dev.
        bool mono = cyberpunk || dev;
        if (Application.Current is { } app && app.Resources["MetricText"] is System.Windows.Style)
        {
            var key = mono ? "MonoFont" : "AppFont";
            if (app.Resources[key] is FontFamily ff)
                app.Resources["MetricFontFamily"] = ff;
        }
    }

    private Storyboard BuildScanlineScroll()
    {
        var anim = new DoubleAnimation(0, 3, new Duration(TimeSpan.FromSeconds(0.7)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(anim, ScanlineShift);
        Storyboard.SetTargetProperty(anim, new PropertyPath(TranslateTransform.YProperty));
        var sb = new Storyboard();
        sb.Children.Add(anim);
        return sb;
    }

    private Storyboard BuildCaretBlink()
    {
        var anim = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromSeconds(0.6)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(anim, DevCaret);
        Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(anim);
        return sb;
    }
}
