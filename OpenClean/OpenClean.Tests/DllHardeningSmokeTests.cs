using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Rauchtests für <c>DllSearchPathHardening</c>. Der Modulinitialisierer der OpenClean-Assembly
/// läuft auch hier, sobald das Testprojekt sie lädt – der Prozess hat also denselben gehärteten
/// DLL-Suchpfad wie die App. Geprüft wird deshalb genau das, was daran zerbrechen könnte:
/// das Nachladen nativer Bibliotheken.
///
/// Das ersetzt NICHT die manuelle Prüfung der Single-File-Portable-Fassung auf einer sauberen VM
/// (dort werden die Natives in einen Temp-Ordner entpackt) – es fängt aber genau die Regression
/// ab, die beim Härten am wahrscheinlichsten ist.
/// </summary>
public class DllHardeningSmokeTests
{
    /// <summary>
    /// Erzwingt, dass die OpenClean-Assembly (und damit der Modulinitialisierer) geladen ist.
    /// </summary>
    private static void EnsureHardeningApplied()
        => Assert.False(OpenClean.Services.Integrity.IntegrityState.IsBlocked);

    /// <summary>
    /// SQLite lädt die native e_sqlite3.dll. Sie wird für Browser-Verlauf und Cookies gebraucht –
    /// bricht der gehärtete Suchpfad das, ist der halbe Privatsphäre-Bereich tot.
    /// </summary>
    [Fact]
    public void NativeSqlite_LaedtTrotzGehaertetemSuchpfad()
    {
        EnsureHardeningApplied();

        string file = Path.Combine(Path.GetTempPath(), $"openclean-hardening-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={file}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('ok'); SELECT v FROM t;";
            Assert.Equal("ok", (string?)cmd.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch { /* Aufräumen darf den Test nie kippen. */ }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    /// <summary>
    /// DER eigentliche Nachweis: Eine neben der EXE abgelegte DLL darf NICHT mehr über ihren
    /// bloßen Namen geladen werden. Ohne diesen Test wären die beiden Rauchtests oben wertlos –
    /// sie würden auch dann bestehen, wenn die Härtung gar nicht griffe.
    ///
    /// Gegenprobe mit vollem Pfad: Dieselbe Datei lädt sich einwandfrei. Das Scheitern oben liegt
    /// also am Suchpfad und nicht an einer kaputten Datei.
    /// </summary>
    [Fact]
    public void DanebengelegteDll_WirdNichtMehrUeberDenNamenGeladen()
    {
        EnsureHardeningApplied();

        // Eine echte, ladbare native DLL als Attrappe verwenden. Der Ausgabeordner enthält
        // e_sqlite3.dll für MEHRERE Architekturen (win-x64, win-arm64, …) – es muss zwingend die
        // zur laufenden Architektur passende sein, sonst scheitert das Laden am Binärformat und
        // der Test würde aus dem falschen Grund fehlschlagen.
        string rid = $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        string source = Directory
            .GetFiles(AppContext.BaseDirectory, "e_sqlite3.dll", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains($@"\{rid}\", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"e_sqlite3.dll für {rid} nicht im Ausgabeordner gefunden.");

        string planted = Path.Combine(AppContext.BaseDirectory, "oc-hijack-probe.dll");
        File.Copy(source, planted, overwrite: true);

        try
        {
            // 1. Nur über den Namen: muss fehlschlagen – das Anwendungsverzeichnis wird nicht
            //    mehr durchsucht. Genau so würde eine untergeschobene version.dll geladen werden.
            IntPtr byName = LoadLibraryW("oc-hijack-probe.dll");
            if (byName != IntPtr.Zero) FreeLibrary(byName);

            Assert.True(byName == IntPtr.Zero,
                "Die danebengelegte DLL wurde über ihren Namen geladen – die Härtung greift nicht.");

            // 2. Gegenprobe mit vollem Pfad: muss klappen (die Datei ist in Ordnung).
            IntPtr byPath = LoadLibraryW(planted);
            int err = Marshal.GetLastWin32Error();
            Assert.True(byPath != IntPtr.Zero,
                $"Die Attrappe ließ sich nicht einmal über den vollen Pfad laden (Win32-Fehler {err}) – Test untauglich. Quelle: {source}");
            FreeLibrary(byPath);
        }
        finally
        {
            try { File.Delete(planted); } catch { /* geladen gehalten -> egal, Temp-Datei. */ }
        }
    }

    /// <summary>
    /// Rendern zwingt WPF, seine nativen Bibliotheken zu laden (wpfgfx_cor3 und deren
    /// Abhängigkeiten). Das ist der Fall, bei dem das Herausnehmen des EXE-Verzeichnisses aus
    /// dem Suchpfad am ehesten zuschlägt.
    /// </summary>
    [Fact]
    public void WpfRendering_LaedtNativesTrotzGehaertetemSuchpfad()
    {
        EnsureHardeningApplied();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(Brushes.CornflowerBlue, null, new System.Windows.Rect(0, 0, 32, 32));

        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        Assert.Equal(32, bitmap.PixelWidth);
    }
}
