using System.Collections.Generic;
using System.Linq;
using OpenClean.Services;
using OpenClean.Services.Theming;

namespace OpenClean.ViewModels;

/// <summary>
/// ViewModel des zentralen Einstellungsbereichs (Zahnrad in der Titelleiste). Bündelt die
/// bereits bestehende Logik: Design-Umschaltung (<see cref="ThemeService"/>), Sprachauswahl
/// (<see cref="LocalizationManager"/>, direkt in der View an den Singleton gebunden) und die
/// Lizenzverwaltung (<see cref="LicensePageViewModel"/>). Es wird nichts neu implementiert –
/// dieser Bereich reicht die bestehenden Dienste/ViewModels nur zusammengefasst durch.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>
    /// Die aus dem <see cref="MainViewModel"/> durchgereichte Lizenz-Instanz. Bewusst dieselbe
    /// Instanz wie die separate Lizenz-Sektion, damit Aktivierung/Freigabe denselben Zustand teilen.
    /// </summary>
    public LicensePageViewModel License { get; }

    public SettingsViewModel(LicensePageViewModel license)
    {
        License = license;
    }

    /// <summary>Eine wählbare Palette mit lokalisiertem Anzeigenamen (für das Theme-Grid).</summary>
    public sealed record ThemeOption(AppTheme Theme, string DisplayName);

    private static string NameKeyFor(AppTheme t) => t switch
    {
        AppTheme.Dark => "theme.name.dark",
        AppTheme.Light => "theme.name.light",
        AppTheme.Cyberpunk => "theme.name.cyberpunk",
        AppTheme.GamingRgb => "theme.name.gamingRgb",
        AppTheme.Nord => "theme.name.nord",
        AppTheme.Catppuccin => "theme.name.catppuccin",
        AppTheme.Dracula => "theme.name.dracula",
        AppTheme.Dev => "theme.name.dev",
        AppTheme.Windows11 => "theme.name.windows11",
        _ => "theme.name.dark"
    };

    /// <summary>Alle wählbaren Paletten in Enum-Reihenfolge (lokalisierte Namen).</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions =>
        System.Enum.GetValues<AppTheme>()
            .Select(t => new ThemeOption(t, Loc.T(NameKeyFor(t))))
            .ToList();

    /// <summary>Aktive Palette; Setter wendet Palette + aktuelle Modifier an.</summary>
    public AppTheme SelectedTheme
    {
        get => ThemeService.Current;
        set
        {
            if (value == ThemeService.Current) return;
            // Windows 11 zieht den echten Mica-Backdrop automatisch mit (falls unterstützt).
            bool mica = UseMicaBackdrop || ShouldForceMica(value, IsMicaSupported);
            ThemeService.ApplyAll(value, UseWindowsAccent, mica, ReduceMotion);
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseMicaBackdrop));
        }
    }

    /// <summary>Modifier: Windows-Akzentfarbe übernehmen.</summary>
    public bool UseWindowsAccent
    {
        get => ThemeService.UseWindowsAccent;
        set
        {
            if (value == ThemeService.UseWindowsAccent) return;
            ThemeService.ApplyAll(ThemeService.Current, value, UseMicaBackdrop, ReduceMotion);
            OnPropertyChanged();
        }
    }

    /// <summary>Modifier: Mica/Acrylic-Fensterhintergrund (nur bei Unterstützung wirksam).</summary>
    public bool UseMicaBackdrop
    {
        get => ThemeService.UseMica;
        set
        {
            if (value == ThemeService.UseMica) return;
            ThemeService.ApplyAll(ThemeService.Current, UseWindowsAccent, value, ReduceMotion);
            OnPropertyChanged();
        }
    }

    /// <summary>Animationen reduzieren (Scanline + RGB-Rotation aus).</summary>
    public bool ReduceMotion
    {
        get => ThemeService.ReduceMotion;
        set
        {
            if (value == ThemeService.ReduceMotion) return;
            ThemeService.ApplyAll(ThemeService.Current, UseWindowsAccent, UseMicaBackdrop, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Ob dieses Windows Mica unterstützt (steuert den deaktivierten Zustand des Schalters).</summary>
    public bool IsMicaSupported => MicaBackdrop.IsSupported;

    /// <summary>Wahr, wenn die Palette-Wahl den Mica-Backdrop erzwingen soll (Windows-11-Theme, sofern unterstützt).</summary>
    internal static bool ShouldForceMica(AppTheme theme, bool micaSupported)
        => theme == AppTheme.Windows11 && micaSupported;

    /// <summary>
    /// Aktualisiert nach einem Sprachwechsel die berechneten Texte dieses Bereichs.
    /// Die durchgereichte <see cref="License"/>-Instanz wird bereits vom MainViewModel
    /// relokalisiert (dieselbe Instanz), daher hier nicht erneut.
    /// </summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(ThemeOptions));
    }
}
