using System.Configuration;
using System.Data;
using System.Windows;
using OpenClean.Services;

namespace OpenClean;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Beim Start das Windows-App-Modus-Theme (Hell/Dunkel) übernehmen,
        // bevor das Hauptfenster gerendert wird.
        ThemeService.ApplyTheme(ThemeService.DetectWindowsTheme());
    }
}
