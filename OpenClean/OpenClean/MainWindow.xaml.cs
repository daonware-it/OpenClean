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
    }

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
