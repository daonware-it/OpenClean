using System.Configuration;
using System.Data;
using System.Windows;
using OpenClean.Services;
using OpenClean.Services.Localization;

namespace OpenClean;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Sprache festlegen (gespeicherte Auswahl → Windows-Sprache → Deutsch),
        // bevor das Hauptfenster gerendert wird.
        LocalizationManager.Instance.InitializeStartupLanguage();

        // Beim Start das gespeicherte Theme (settings.json) anwenden, sonst das
        // Windows-App-Modus-Theme (Hell/Dunkel) – bevor das Hauptfenster gerendert wird.
        ThemeService.ApplyTheme(ThemeService.DetectStartupTheme());
    }
}
