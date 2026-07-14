using System;
using System.Collections.Generic;
using System.IO;
using OpenClean.Models;
using OpenClean.Services;
using OpenClean.Services.Integrity;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Beweist, dass die Sperre im gesperrten Zustand wirklich greift: Der CleanerService darf
/// dann NICHTS löschen. Das ist der Choke Point, über den auch der unbeaufsichtigte
/// --auto-Lauf geht (AutoCleanService ruft Clean direkt auf, an jedem ViewModel vorbei).
///
/// IntegrityState ist prozessweit; die Tests setzen ihn und stellen ihn danach wieder her.
/// Die Parallelität ist dafür assembly-weit abgeschaltet (siehe AssemblyInfo.cs).
/// </summary>
public class IntegrityGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "OpenClean.GateTests." + Guid.NewGuid().ToString("N"));

    public IntegrityGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Zustand IMMER zurücksetzen, sonst laufen die übrigen Tests gesperrt.
        IntegrityState.Set(IntegrityLevel.Trusted, blocked: false, reasonKey: "",
            IntegritySignatureStatus.Valid);

        try { Directory.Delete(_dir, recursive: true); } catch { /* egal */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Legt eine echte Datei an und packt sie in eine ausgewählte Aufräum-Kategorie.</summary>
    private (string Path, List<CleanupCategory> Categories) MakeVictim()
    {
        string file = Path.Combine(_dir, "opfer.tmp");
        File.WriteAllText(file, new string('x', 1024));

        var category = new CleanupCategory
        {
            Key = "cat.windowsTemp",
            Kind = CleanupKind.FileDeletion,
            IsEnabled = true
        };
        category.Items.Add(new ScanItem { FullPath = file, SizeBytes = 1024 });

        return (file, [category]);
    }

    [Fact]
    public void Gesperrt_CleanerLoeschtNichts()
    {
        var (file, categories) = MakeVictim();

        IntegrityState.Set(IntegrityLevel.Degraded, blocked: true,
            reasonKey: "integrity.reason.tampered", IntegritySignatureStatus.Tampered);

        var report = new CleanerService().Clean(categories);

        Assert.True(report.Blocked, "Der Bericht muss die Sperre ausweisen.");
        Assert.Equal(0, report.DeletedCount);
        Assert.True(File.Exists(file), "Die Datei wurde trotz Sperre gelöscht!");
    }

    /// <summary>
    /// Gegenprobe: Ohne Sperre löscht derselbe Aufruf tatsächlich. Ohne diesen Test könnte der
    /// obige auch dann bestehen, wenn das Löschen aus einem ganz anderen Grund gar nicht liefe.
    /// </summary>
    [Fact]
    public void NichtGesperrt_CleanerLoeschtSehrWohl()
    {
        var (file, categories) = MakeVictim();

        IntegrityState.Set(IntegrityLevel.Trusted, blocked: false, reasonKey: "",
            IntegritySignatureStatus.Valid);

        var report = new CleanerService().Clean(categories);

        Assert.False(report.Blocked);
        Assert.Equal(1, report.DeletedCount);
        Assert.False(File.Exists(file), "Die Datei hätte gelöscht werden müssen.");
    }
}
