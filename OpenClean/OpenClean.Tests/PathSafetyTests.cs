using System;
using System.IO;
using OpenClean.Services;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Sichert die Lösch-Sperrliste ab: Systempfade dürfen nie löschbar sein,
/// gewöhnliche Nutzerpfade immer.
/// </summary>
public class PathSafetyTests
{
    [Fact]
    public void WindowsOrdner_IstNichtLoeschbar()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(PathSafety.IsDeletable(Path.Combine(windows, "explorer.exe")));
        Assert.False(PathSafety.IsDeletable(windows));
    }

    [Fact]
    public void ProgramFiles_IstNichtLoeschbar()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(PathSafety.IsDeletable(Path.Combine(pf, "Irgendwas", "app.exe")));
    }

    [Fact]
    public void Laufwerkswurzel_IstNichtLoeschbar()
    {
        Assert.False(PathSafety.IsDeletable(@"C:\"));
        Assert.False(PathSafety.IsDeletable(@"C:\"));
    }

    [Theory]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21\file.bin")]
    [InlineData(@"C:\System Volume Information\tracking.log")]
    public void Systemdateien_SindNichtLoeschbar(string path)
    {
        Assert.False(PathSafety.IsDeletable(path));
    }

    [Fact]
    public void GrossKleinschreibungUndTrailingSlash_SindEgal()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(PathSafety.IsDeletable(windows.ToUpperInvariant() + @"\"));
        Assert.False(PathSafety.IsDeletable(windows.ToLowerInvariant()));
    }

    [Fact]
    public void NutzerDatei_IstLoeschbar()
    {
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "grosse-datei.iso");
        Assert.True(PathSafety.IsDeletable(downloads));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LeererPfad_IstNichtLoeschbar(string? path)
    {
        Assert.False(PathSafety.IsDeletable(path));
    }

    [Fact]
    public void IsExcludedFolder_ErkenntWindowsUndProgramFiles()
    {
        Assert.True(PathSafety.IsExcludedFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(PathSafety.IsExcludedFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(PathSafety.IsExcludedFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }
}
