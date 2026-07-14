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

    private Task<DiskScanResult> ScanAsync(int maxDepth = 3, long minFileBytes = 0, int maxFiles = 500)
        => new DiskScannerService().ScanAsync(
            new DiskScanOptions
            {
                RootPath = _root,
                MaxDepth = maxDepth,
                MinFileBytes = minFileBytes,
                MaxFiles = maxFiles
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
