using System.Text.Json;
using OpenClean.Services;
using OpenClean.ViewModels;
using Xunit;

namespace OpenClean.Tests;

public sealed class ThemeSettingsPersistenceTests
{
    [Theory]
    [InlineData(AppTheme.Windows11, true, true)]
    [InlineData(AppTheme.Windows11, false, false)]
    [InlineData(AppTheme.Dark, true, false)]
    [InlineData(AppTheme.Cyberpunk, true, false)]
    public void ShouldForceMica_Nur_Windows11_Bei_Unterstuetzung(AppTheme theme, bool supported, bool expected)
    {
        Assert.Equal(expected, SettingsViewModel.ShouldForceMica(theme, supported));
    }

    [Fact]
    public void Modifier_Defaults_Sind_False()
    {
        var s = new AppSettings();
        Assert.False(s.UseWindowsAccent);
        Assert.False(s.UseMicaBackdrop);
        Assert.False(s.ReduceMotion);
    }

    [Fact]
    public void Modifier_Ueberleben_Json_RoundTrip()
    {
        var s = new AppSettings { Theme = "Dracula", UseWindowsAccent = true, UseMicaBackdrop = true, ReduceMotion = true };
        string json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("Dracula", back.Theme);
        Assert.True(back.UseWindowsAccent);
        Assert.True(back.UseMicaBackdrop);
        Assert.True(back.ReduceMotion);
    }

    [Fact]
    public void Alte_Json_Ohne_Modifier_Laedt_Mit_Defaults()
    {
        var back = JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"Dark\"}")!;
        Assert.False(back.UseWindowsAccent);
        Assert.False(back.UseMicaBackdrop);
        Assert.False(back.ReduceMotion);
    }
}
