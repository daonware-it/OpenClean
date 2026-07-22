using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using OpenClean.Services;
using OpenClean.ViewModels;
using OpenClean.Views;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// XAML-Instanziierungs-Harness (Task 12): XAML-Fehler sind Laufzeitfehler, der Build sieht sie
/// nicht. Dieser Test lädt <see cref="App"/> (App.xaml-Ressourcen inkl. Theme-Styles), wendet
/// jede der 7 Paletten an und baut <see cref="SettingsView"/> auf einem STA-Thread auf
/// (Measure/Arrange), um fehlende Ressourcen-Keys oder falsche Binding-Pfade zu fangen.
/// </summary>
public sealed class ThemeXamlInstantiationTests
{
    /// <summary>
    /// Der Test-Host (VSTest) ist die Entry-Assembly, nicht OpenClean.dll: die relativen
    /// Paket-URIs von <c>ThemeService.PaletteUri</c> (z. B. "Themes/DarkTheme.xaml") lösen
    /// gegen <see cref="Application.ResourceAssembly"/> auf und würden sonst mit "Ressource
    /// kann nicht gefunden werden" scheitern. Der Getter setzt beim allerersten Zugriff durch
    /// den Testadapter bereits einen Default (Entry-Assembly), sodass der öffentliche Setter
    /// danach mit "Eigenschaft kann nicht geändert werden" verweigert — deshalb wird das
    /// private Backing-Field direkt überschrieben. Reines Testinfrastruktur-Detail, keine
    /// Änderung an App.xaml/SettingsView.
    /// </summary>
    private static void EnsureResourceAssembly()
    {
        var target = typeof(App).Assembly;
        if (ReferenceEquals(Application.ResourceAssembly, target)) return;

        var field = typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static);
        if (field is not null)
        {
            field.SetValue(null, target);
        }
        else
        {
            // Fallback, falls sich der interne Feldname in einer künftigen .NET-Version ändert.
            Application.ResourceAssembly = target;
        }
    }

    private static void OnSta(Action action)
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try
            {
                EnsureResourceAssembly();
                if (Application.Current is null)
                {
                    var app = new App();
                    app.InitializeComponent(); // lädt App.xaml (Merged Themes + Styles)
                }
                action();
            }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void SettingsView_Baut_In_Jeder_Palette_Fehlerfrei_Auf()
    {
        OnSta(() =>
        {
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            {
                ThemeService.ApplyAll(theme, useWindowsAccent: false, useMica: false, reduceMotion: true);
                var view = new SettingsView
                {
                    DataContext = new SettingsViewModel(new LicensePageViewModel())
                };
                view.Measure(new Size(900, 760));
                view.Arrange(new Rect(0, 0, 900, 760));
            }
        });
    }
}
