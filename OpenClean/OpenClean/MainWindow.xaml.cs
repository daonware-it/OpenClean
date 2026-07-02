using System.Windows;
using System.Windows.Controls.Primitives;
using OpenClean.Services;

namespace OpenClean;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new OpenClean.ViewModels.MainViewModel();

        // Toggle-Zustand am aktiven Theme ausrichten (beim Start aus Windows ermittelt).
        ThemeToggle.IsChecked = ThemeService.Current == AppTheme.Light;
        UpdateThemeLabel();

        // Maximiert ragt ein Fenster ohne Standard-Chrome sonst über den Bildschirmrand hinaus.
        StateChanged += (_, _) =>
            RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Toggle();
        UpdateThemeLabel();
    }

    private void UpdateThemeLabel()
    {
        ThemeLabel.Text = ThemeService.Current == AppTheme.Light ? "☀ Hell" : "🌙 Dunkel";
    }
}
