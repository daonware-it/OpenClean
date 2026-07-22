using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using Xunit;

namespace OpenClean.Tests;

public sealed class ThemeLocalizationParityTests
{
    private static readonly string[] Codes = { "de", "en", "es", "fr", "pt", "ru", "pl" };
    private static readonly string[] NewKeys =
    {
        "theme.name.dark", "theme.name.light", "theme.name.cyberpunk", "theme.name.gamingRgb",
        "theme.name.nord", "theme.name.catppuccin", "theme.name.dracula",
        "theme.name.dev", "theme.name.windows11",
        "settings.appearance.palette", "settings.appearance.windowsAccent",
        "settings.appearance.mica", "settings.appearance.micaUnsupported",
        "settings.appearance.reduceMotion"
    };

    private static string StringsDir()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenClean.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "OpenClean", "Resources", "Strings");
    }

    [Fact]
    public void Alle_Sprachen_Enthalten_Die_Neuen_Theme_Keys()
    {
        var dir = StringsDir();
        foreach (var code in Codes)
        {
            var json = File.ReadAllText(Path.Combine(dir, $"{code}.json"));
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            foreach (var key in NewKeys)
                Assert.True(map.ContainsKey(key) && !string.IsNullOrWhiteSpace(map[key]),
                    $"{code}.json fehlt Key '{key}'");
        }
    }
}
