using System;
using System.IO;
using System.Security.Cryptography;
using OpenClean.Services.Privacy;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt den Rohzugriff aufs Volume ab (<see cref="RawVolumeReader.TryCopyRaw"/>).
///
/// Hintergrund: Chromium hält <c>Cookies</c> mit <c>FileShare.None</c> offen, solange der Browser
/// läuft – ein normales <c>File.Copy</c> scheitert dort mit <see cref="IOException"/>. Der frühere
/// Weg über eine VSS-Schattenkopie funktionierte zwar, verwarf aber alle Windows-Wiederherstellungs-
/// punkte. Der Rohzugriff liest stattdessen die Cluster der Datei direkt vom Volume.
///
/// WICHTIG: Der Rohzugriff verlangt Adminrechte. Ohne erhöhte Rechte kann
/// <see cref="RawVolumeReader.TryCopyRaw"/> nur <c>false</c> liefern, deshalb steigt jeder Test
/// zu Beginn sauber aus, wenn <see cref="RawVolumeReader.IsAvailable"/> false ist
/// (xUnit 2.9.3 kennt noch kein <c>Assert.Skip</c>, daher ein frühes <c>return</c>).
/// Die Tests erzeugen KEINE Schattenkopie und ändern nichts am System.
/// </summary>
public class RawVolumeReaderTests : IDisposable
{
    private readonly string _dir;

    public RawVolumeReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OpenCleanTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- Hilfen --------------------------------------------------------------

    /// <summary>Deterministisches Byte-Muster, damit Original und Kopie exakt vergleichbar sind.</summary>
    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)((i * 31 + (i >> 8) * 7) & 0xFF);
        return data;
    }

    /// <summary>
    /// Schreibt die Datei und erzwingt das Durchschreiben auf die Platte. Ohne <c>Flush(true)</c>
    /// könnten die Daten noch im Cache liegen – der Rohzugriff liest aber die Platte.
    /// </summary>
    private string NewFile(string name, byte[] content)
    {
        string path = Path.Combine(_dir, name);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(content, 0, content.Length);
            fs.Flush(flushToDisk: true);
        }
        return path;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ---- 1) Freie Datei: byte-identisch und exakt gekürzt --------------------

    [Fact]
    public void TryCopyRaw_kopiert_Datei_byte_identisch_und_mit_exakter_Laenge()
    {
        if (!RawVolumeReader.IsAvailable) return; // ohne Adminrechte nicht prüfbar

        // Krumme Größe: der letzte Cluster ist auf der Platte aufgefüllt, die Kopie
        // muss trotzdem exakt auf die echte Dateigröße gekürzt sein.
        byte[] original = Pattern(8 * 1024 + 137);
        string src = NewFile("muster.bin", original);
        string dst = Path.Combine(_dir, "muster.copy.bin");

        Assert.True(RawVolumeReader.TryCopyRaw(src, dst));

        byte[] copy = File.ReadAllBytes(dst);
        Assert.Equal(original.Length, copy.Length);
        Assert.Equal(original, copy);
    }

    // ---- 2) KERNFALL: exklusiv gesperrte Datei -------------------------------

    [Fact]
    public void TryCopyRaw_liest_exklusiv_gesperrte_Datei_die_File_Copy_verweigert()
    {
        if (!RawVolumeReader.IsAvailable) return; // ohne Adminrechte nicht prüfbar

        byte[] original = Pattern(64 * 1024 + 11);
        string src = NewFile("Cookies", original);
        string rawDst = Path.Combine(_dir, "Cookies.raw");
        string plainDst = Path.Combine(_dir, "Cookies.plain");

        // Genau das tut Chromium mit seiner Cookies-DB: kein Sharing.
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Gegenprobe: der normale Weg scheitert an der Sperre …
            Assert.Throws<IOException>(() => File.Copy(src, plainDst));
            Assert.False(File.Exists(plainDst));

            // … der Rohzugriff kommt trotzdem an den vollständigen Inhalt.
            Assert.True(RawVolumeReader.TryCopyRaw(src, rawDst));
        }

        byte[] copy = File.ReadAllBytes(rawDst);
        Assert.Equal(original.Length, copy.Length);
        Assert.Equal(original, copy);
    }

    // ---- 3) Fehlende Quelle --------------------------------------------------

    [Fact]
    public void TryCopyRaw_liefert_false_und_legt_keine_Zieldatei_an_wenn_Quelle_fehlt()
    {
        if (!RawVolumeReader.IsAvailable) return; // ohne Adminrechte nicht prüfbar

        string src = Path.Combine(_dir, "gibtsnicht.bin");
        string dst = Path.Combine(_dir, "gibtsnicht.copy.bin");

        Assert.False(RawVolumeReader.TryCopyRaw(src, dst));
        Assert.False(File.Exists(dst));
    }

    // ---- 4) Leere Datei ------------------------------------------------------

    [Fact]
    public void TryCopyRaw_bricht_bei_leerer_Datei_sauber_ab_ohne_Zieldatei()
    {
        if (!RawVolumeReader.IsAvailable) return; // ohne Adminrechte nicht prüfbar

        // Tatsächliches Verhalten: bei Dateigröße 0 gibt es keine Cluster zu lesen,
        // TryCopyRaw steigt vor dem Anlegen der Zieldatei mit false aus (kein Absturz).
        string src = NewFile("leer.bin", Array.Empty<byte>());
        string dst = Path.Combine(_dir, "leer.copy.bin");

        Assert.False(RawVolumeReader.TryCopyRaw(src, dst));
        Assert.False(File.Exists(dst));
    }

    // ---- 5) Große Datei über mehrere Blöcke/Extents --------------------------

    [Fact]
    public void TryCopyRaw_kopiert_grosse_Datei_ueber_mehrere_Bloecke_hash_identisch()
    {
        if (!RawVolumeReader.IsAvailable) return; // ohne Adminrechte nicht prüfbar

        // > 2 MiB, damit die 1-MiB-Blocklogik mehrfach läuft und ggf. mehrere Extents anfallen.
        byte[] original = Pattern(2 * 1024 * 1024 + 4096 + 55);
        string src = NewFile("gross.bin", original);
        string dst = Path.Combine(_dir, "gross.copy.bin");

        Assert.True(RawVolumeReader.TryCopyRaw(src, dst));

        Assert.Equal(original.Length, new FileInfo(dst).Length);
        Assert.Equal(Sha256(src), Sha256(dst));
    }
}
