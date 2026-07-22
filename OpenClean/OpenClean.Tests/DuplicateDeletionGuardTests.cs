using System;
using System.Collections.Generic;
using System.Linq;
using OpenClean.Services.Duplicates;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Tests für den „mindestens eine Kopie behalten“-Guard – die sicherheitskritische Kernregel
/// beim Löschen von Duplikaten.
/// </summary>
public class DuplicateDeletionGuardTests
{
    private sealed record File(string Path, DateTime Modified, bool Selected);

    private static List<File> Select(IReadOnlyCollection<File> files, out bool spared)
        => DuplicateDeletionGuard.SelectDeletable(files, f => f.Selected, f => f.Modified, out spared);

    [Fact]
    public void KeineMarkiert_LoeschtNichts_SchontNichts()
    {
        var files = new[]
        {
            new File("a", new DateTime(2024, 1, 1), Selected: false),
            new File("b", new DateTime(2024, 1, 2), Selected: false),
        };

        var toDelete = Select(files, out bool spared);

        Assert.Empty(toDelete);
        Assert.False(spared);
    }

    [Fact]
    public void TeilweiseMarkiert_LoeschtGenauDieMarkierten_SchontNichts()
    {
        var files = new[]
        {
            new File("a", new DateTime(2024, 1, 1), Selected: true),
            new File("b", new DateTime(2024, 1, 2), Selected: false),
            new File("c", new DateTime(2024, 1, 3), Selected: true),
        };

        var toDelete = Select(files, out bool spared);

        Assert.False(spared);
        Assert.Equal(new[] { "a", "c" }, toDelete.Select(f => f.Path));
    }

    [Fact]
    public void AlleMarkiert_SchontDieNeueste_UndMeldetSpared()
    {
        var files = new[]
        {
            new File("alt", new DateTime(2024, 1, 1), Selected: true),
            new File("mittel", new DateTime(2024, 6, 1), Selected: true),
            new File("neu", new DateTime(2024, 12, 1), Selected: true),
        };

        var toDelete = Select(files, out bool spared);

        Assert.True(spared);
        Assert.DoesNotContain(toDelete, f => f.Path == "neu");
        Assert.Equal(new[] { "alt", "mittel" }, toDelete.Select(f => f.Path).OrderBy(p => p));
    }

    [Fact]
    public void AlleMarkiert_ZweiKopien_LoeschtNurEine()
    {
        var files = new[]
        {
            new File("alt", new DateTime(2024, 1, 1), Selected: true),
            new File("neu", new DateTime(2024, 2, 1), Selected: true),
        };

        var toDelete = Select(files, out bool spared);

        Assert.True(spared);
        Assert.Single(toDelete);
        Assert.Equal("alt", toDelete[0].Path);
    }

    [Fact]
    public void LeereGruppe_LoeschtNichts()
    {
        var toDelete = Select(Array.Empty<File>(), out bool spared);

        Assert.Empty(toDelete);
        Assert.False(spared);
    }
}
