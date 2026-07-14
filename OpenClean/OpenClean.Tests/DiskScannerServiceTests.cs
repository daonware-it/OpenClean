using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClean.Services;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt den gemeinsamen Scan ab: Baumaufbau, Byte-Summen, Tiefenbegrenzung,
/// Top-N-Dateiauswahl und Abbruch. Läuft gegen ein echtes temporäres Verzeichnis.
/// </summary>
public class DiskScannerServiceTests : IDisposable
{
    private readonly string _root;

    public DiskScannerServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "opencleantest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Aufräumen ist best effort */ }
    }

    private void WriteFile(long bytes, params string[] segments)
    {
        string path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private Task<DiskScanResult> ScanAsync(
        int maxDepth = 3, long minFileBytes = 0, int maxFiles = 500, bool excludeSystemFolders = false)
        => new DiskScannerService().ScanAsync(
            new DiskScanOptions
            {
                RootPath = _root,
                MaxDepth = maxDepth,
                MinFileBytes = minFileBytes,
                MaxFiles = maxFiles,
                ExcludeSystemFolders = excludeSystemFolders
            },
            progress: null,
            ct: CancellationToken.None);

    [Fact]
    public async Task Wurzel_SummiertAlleBytesRekursiv()
    {
        WriteFile(1000, "a.bin");
        WriteFile(2000, "unter", "b.bin");
        WriteFile(3000, "unter", "tiefer", "c.bin");

        var result = await ScanAsync();

        Assert.Equal(6000, result.Root.TotalBytes);
    }

    [Fact]
    public async Task Kinder_HabenEigeneSummenUndElternverweis()
    {
        WriteFile(1000, "a.bin");
        WriteFile(2000, "unter", "b.bin");

        var result = await ScanAsync();

        var unter = Assert.Single(result.Root.Children);
        Assert.Equal("unter", unter.Name);
        Assert.Equal(2000, unter.TotalBytes);
        Assert.Same(result.Root, unter.Parent);
    }

    [Fact]
    public async Task Tiefenbegrenzung_SchneidetAbUndSetztIsPartial()
    {
        // Tiefe 1 = "eins", Tiefe 2 = "zwei". Bei MaxDepth=1 darf "eins" keine Kinder haben.
        WriteFile(500, "eins", "zwei", "d.bin");

        var result = await ScanAsync(maxDepth: 1);

        var eins = Assert.Single(result.Root.Children);
        Assert.Equal(500, eins.TotalBytes);   // Bytes sind trotzdem vollständig aufsummiert
        Assert.Empty(eins.Children);          // aber der Teilbaum fehlt
        Assert.True(eins.IsPartial);
    }

    [Fact]
    public async Task GroessteDateien_SindAbsteigendSortiertUndAufMaxFilesBegrenzt()
    {
        WriteFile(100, "klein.bin");
        WriteFile(900, "gross.bin");
        WriteFile(500, "mittel.bin");

        var result = await ScanAsync(maxFiles: 2);

        Assert.Equal(2, result.LargestFiles.Count);
        Assert.Equal(900, result.LargestFiles[0].SizeBytes);
        Assert.Equal(500, result.LargestFiles[1].SizeBytes);
    }

    [Fact]
    public async Task MinFileBytes_FiltertKleineDateienAus()
    {
        WriteFile(100, "klein.bin");
        WriteFile(900, "gross.bin");

        var result = await ScanAsync(minFileBytes: 500);

        var only = Assert.Single(result.LargestFiles);
        Assert.EndsWith("gross.bin", only.FullPath);
    }

    [Fact]
    public async Task Abbruch_WirftOperationCanceled()
    {
        WriteFile(100, "a.bin");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiskScannerService().ScanAsync(
                new DiskScanOptions { RootPath = _root }, progress: null, ct: cts.Token));
    }

    // Hinweis zur Testbarkeit: PathSafety.IsExcludedFolder prüft nicht nur echte
    // Systempfade (C:\Windows, Program Files, …), sondern zusätzlich – laufwerksunabhängig –
    // ob irgendein Pfadsegment "$Recycle.Bin" oder "System Volume Information" heißt
    // (siehe ExcludedAnyDriveFolders in PathSafety.cs). Dieser zweite Zweig lässt sich mit
    // einem so benannten Unterordner im Temp-Testverzeichnis ehrlich testen, ohne echte
    // Systempfade der laufenden Maschine anzufassen oder Produktionscode zu ändern.

    [Fact]
    public async Task ExcludeSystemFolders_True_SchliesstDateienInGesperrtemOrdnerAusDenGroesstenDateienAus()
    {
        WriteFile(100, "normal.bin");
        WriteFile(9000, "$Recycle.Bin", "geloescht.bin");

        var result = await ScanAsync(excludeSystemFolders: true);

        var only = Assert.Single(result.LargestFiles);
        Assert.EndsWith("normal.bin", only.FullPath);
    }

    [Fact]
    public async Task ExcludeSystemFolders_True_GesperrterOrdnerFehltImBaum()
    {
        WriteFile(100, "normal.bin");
        WriteFile(9000, "$Recycle.Bin", "geloescht.bin");

        var result = await ScanAsync(excludeSystemFolders: true);

        Assert.DoesNotContain(result.Root.Children, c => c.Name == "$Recycle.Bin");
        // Die ausgeschlossenen Bytes dürfen nicht in die Gesamtsumme der Wurzel einfließen.
        Assert.Equal(100, result.Root.TotalBytes);
    }

    [Fact]
    public async Task ExcludeSystemFolders_False_GesperrterOrdnerWirdGanzNormalGescannt()
    {
        WriteFile(100, "normal.bin");
        WriteFile(9000, "$Recycle.Bin", "geloescht.bin");

        var result = await ScanAsync(excludeSystemFolders: false);

        Assert.Contains(result.Root.Children, c => c.Name == "$Recycle.Bin");
        Assert.Equal(2, result.LargestFiles.Count);
        Assert.Contains(result.LargestFiles, f => f.FullPath.EndsWith("geloescht.bin"));
        Assert.Equal(9100, result.Root.TotalBytes);
    }

    [Fact]
    public async Task UnbekannterPfad_LiefertLeerenBaumStattAusnahme()
    {
        var result = await new DiskScannerService().ScanAsync(
            new DiskScanOptions { RootPath = Path.Combine(_root, "gibtesnicht") },
            progress: null, ct: CancellationToken.None);

        Assert.Equal(0, result.Root.TotalBytes);
        Assert.Empty(result.LargestFiles);
    }
}
