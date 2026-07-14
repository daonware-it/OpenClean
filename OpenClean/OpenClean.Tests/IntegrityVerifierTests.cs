using System;
using System.IO;
using OpenClean.Services.Integrity;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Prüft den WinVerifyTrust-Interop gegen echte Dateien. Das ist der Teil, der sich nicht
/// wegargumentieren lässt: entweder die Interop-Struktur stimmt, oder Windows liefert Unsinn.
/// </summary>
public class IntegrityVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenClean.IntegrityTests." + Guid.NewGuid().ToString("N"));

    public IntegrityVerifierTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Eine Datei mit EINGEBETTETER Authenticode-Signatur. Bewusst nicht notepad.exe & Co.:
    /// die meisten Windows-Systemdateien sind nur katalogsigniert und haben gar keine Signatur
    /// in der Datei selbst – die prüft WinVerifyTrust mit WTD_CHOICE_FILE (korrekterweise) nicht.
    /// OpenClean.exe und das Premium-Modul sind eingebettet signiert, genau wie dotnet.exe.
    /// </summary>
    private static string EmbeddedSignedFile
    {
        get
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "dotnet.exe");
            Assert.True(File.Exists(path), $"Erwartete signierte Testdatei fehlt: {path}");
            return path;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen darf nie den Test kippen. */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Eine gültig eingebettet signierte Datei muss als solche erkannt werden.</summary>
    [Fact]
    public void SignierteDatei_IstValid()
    {
        var result = IntegrityVerifier.Verify(EmbeddedSignedFile);

        Assert.Equal(IntegritySignatureStatus.Valid, result.Status);
        // Herausgeber und Fingerabdruck müssen mitkommen – daraus entscheidet der Guard,
        // ob die Signatur von DaonWare stammt.
        Assert.NotEmpty(result.Publisher);
        Assert.NotEmpty(result.Thumbprint);
    }

    /// <summary>
    /// Eine nur katalogsignierte Datei (die meisten Windows-Systemdateien) hat keine Signatur in
    /// der Datei selbst und gilt hier als unsigniert. Bewusst so: OpenClean prüft ausschließlich
    /// eigene, eingebettet signierte Binärdateien – Katalogunterstützung wäre unnötige Komplexität.
    /// Wichtig ist nur, dass daraus KEIN Manipulationsverdacht wird.
    /// </summary>
    [Fact]
    public void NurKatalogsignierteDatei_GiltAlsUnsigned()
    {
        var result = IntegrityVerifier.Verify(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        Assert.Equal(IntegritySignatureStatus.Unsigned, result.Status);
    }

    /// <summary>Eine Datei ohne jede Signatur ist "unsigniert" – und ausdrücklich NICHT "manipuliert".</summary>
    [Fact]
    public void DateiOhneSignatur_IstUnsigned()
    {
        string file = Path.Combine(_dir, "unsigniert.exe");
        File.WriteAllText(file, "keine echte Binärdatei");

        var result = IntegrityVerifier.Verify(file);

        Assert.Equal(IntegritySignatureStatus.Unsigned, result.Status);
    }

    /// <summary>
    /// Der Kernfall von OPCL-20: eine signierte Datei wird nachträglich verändert. Die Signatur
    /// ist noch da, passt aber nicht mehr zum Inhalt -> Tampered (nicht Unsigned, nicht Untrusted).
    /// </summary>
    [Fact]
    public void SignierteAberVeraenderteDatei_IstTampered()
    {
        string copy = Path.Combine(_dir, "gepatcht.exe");
        File.Copy(EmbeddedSignedFile, copy);

        // Ein einzelnes Byte weit hinter dem PE-Header kippen – wie ein Hex-Editor-Patch.
        using (var fs = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = fs.Length / 2;
            int b = fs.ReadByte();
            fs.Position = fs.Length / 2;
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        var result = IntegrityVerifier.Verify(copy);

        Assert.True(
            result.Status == IntegritySignatureStatus.Tampered,
            $"Erwartet Tampered, war {result.Status} (HRESULT 0x{result.HResult:X8}).");
    }

    [Fact]
    public void NichtVorhandeneDatei_LiefertErrorUndWirftNicht()
    {
        var result = IntegrityVerifier.Verify(Path.Combine(_dir, "gibtesnicht.exe"));
        Assert.Equal(IntegritySignatureStatus.Error, result.Status);
    }

    [Fact]
    public void LeererPfad_LiefertErrorUndWirftNicht()
        => Assert.Equal(IntegritySignatureStatus.Error, IntegrityVerifier.Verify(null).Status);

    /// <summary>
    /// Wiederholte Prüfungen dürfen keinen Trust-Provider-Zustand lecken (WTD_STATEACTION_CLOSE).
    /// Der Test kann ein Leck nicht direkt messen, würde aber bei kaputtem Interop reproduzierbar
    /// abweichende Ergebnisse liefern.
    /// </summary>
    [Fact]
    public void MehrfachePruefung_IstStabil()
    {
        string file = EmbeddedSignedFile;
        for (int i = 0; i < 50; i++)
            Assert.Equal(IntegritySignatureStatus.Valid, IntegrityVerifier.Verify(file).Status);
    }
}
