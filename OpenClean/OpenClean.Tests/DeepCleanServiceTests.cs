using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenClean.Services.DeepClean;
using Xunit;

namespace OpenClean.Tests;

/// <summary>
/// Deckt die Tiefen-Bereinigung ab, ohne das System anzufassen: Prozessaufrufe und Umgebung
/// laufen über Attrappen. Getestet wird das, was schiefgehen kann, ohne dass man es sähe –
/// die Auswertung der Werkzeugausgaben, die Rechteprüfung, die Fehlerabbildung und die
/// Berechnung des freigewordenen Speichers.
///
/// <para>Bewusst NICHT getestet: dass DISM tatsächlich Pakete entfernt oder dass ein Dienst
/// wirklich stoppt. Das verlangte Administratorrechte und eine echte Systemänderung – so etwas
/// gehört nicht in eine Unit-Test-Suite.</para>
/// </summary>
public class DeepCleanServiceTests
{
    // ===================================================================================
    //  Attrappen
    // ===================================================================================

    /// <summary>Prozess-Attrappe: liefert je Werkzeug/Argument vorbereitete Antworten und protokolliert die Aufrufe.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly List<(Func<string, IReadOnlyList<string>, bool> Match, ProcessResult Result)> _rules = new();

        /// <summary>Alle getätigten Aufrufe in Reihenfolge – Grundlage der Ablauf-Prüfungen.</summary>
        public List<string> Calls { get; } = new();

        public ProcessResult Default { get; set; } = new(0, "", "");

        /// <summary>Wirkung des Aufrufs auf die Umgebung (z. B. rd entfernt den Ordner).</summary>
        public Action<string, IReadOnlyList<string>>? OnCall { get; set; }

        /// <summary>Antwort für jeden Aufruf, dessen Argumente ALLE angegebenen Teilstrings enthalten.</summary>
        public FakeProcessRunner When(string fileName, string containsArgument, ProcessResult result)
        {
            _rules.Add(((f, a) =>
                f.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                a.Any(x => x.Contains(containsArgument, StringComparison.OrdinalIgnoreCase)), result));
            return this;
        }

        /// <summary>
        /// Wie <see cref="When(string,string,ProcessResult)"/>, aber ALLE Teilstrings müssen
        /// vorkommen. Nötig, um „sc query bits" von „sc query wuauserv" zu unterscheiden –
        /// ein Muster auf „bits" allein träfe auch „sc stop bits".
        /// </summary>
        public FakeProcessRunner WhenAll(string fileName, string[] containsArguments, ProcessResult result)
        {
            _rules.Add(((f, a) =>
                f.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                containsArguments.All(c => a.Any(x => x.Contains(c, StringComparison.OrdinalIgnoreCase))), result));
            return this;
        }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(fileName + " " + string.Join(" ", arguments));
            OnCall?.Invoke(fileName, arguments);

            foreach (var (match, result) in _rules)
            {
                if (match(fileName, arguments)) return Task.FromResult(result);
            }

            return Task.FromResult(Default);
        }
    }

    /// <summary>Umgebungs-Attrappe: Pfade und Größen frei steuerbar, kein Zugriff auf echte Ordner.</summary>
    private sealed class FakeEnvironment : IDeepCleanEnvironment
    {
        public bool IsElevated { get; set; } = true;
        public string WindowsOldPath { get; set; } = @"C:\Windows.old";
        public string UpdateCachePath { get; set; } = @"C:\Windows\SoftwareDistribution\Download";
        public string DeliveryOptimizationPath { get; set; } = @"C:\Windows\SoftwareDistribution\DeliveryOptimization";

        /// <summary>Existierende Ordner.</summary>
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Größe je Pfad; bei mehreren Werten liefert jeder Aufruf den nächsten (vorher/nachher).</summary>
        public Dictionary<string, Queue<long>> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int RestorePointCount { get; set; }
        public (int Removed, int Failed) RemoveResult { get; set; } = (0, 0);
        public int RemoveCalls { get; private set; }
        public int EmptyCalls { get; private set; }
        public int EmptyFailures { get; set; }

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public long GetDirectorySize(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!Sizes.TryGetValue(path, out var queue) || queue.Count == 0) return 0;
            // Letzter Wert bleibt stehen, damit wiederholte Messungen stabil sind.
            return queue.Count == 1 ? queue.Peek() : queue.Dequeue();
        }

        public int EmptyDirectory(string path, CancellationToken ct)
        {
            EmptyCalls++;
            return EmptyFailures;
        }

        public int CountRestorePoints() => RestorePointCount;

        public (int Removed, int Failed) RemoveOldRestorePoints()
        {
            RemoveCalls++;
            return RemoveResult;
        }

        /// <summary>Legt einen Ordner mit einer Folge von Messwerten an (vorher, nachher, …).</summary>
        public void SetFolder(string path, params long[] sizes)
        {
            Directories.Add(path);
            Sizes[path] = new Queue<long>(sizes);
        }
    }

    private static DeepCleanService Create(IProcessRunner runner, FakeEnvironment env)
        => new(runner, env);

    // Echte DISM-Ausgabe (/English), gekürzt auf die ausgewerteten Zeilen.
    private const string DismAnalyzeOutput = """
        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1

        Image Version: 10.0.26100.1

        [==========================100.0%==========================]

        Component Store (WinSxS) Information:

        Windows Explorer Reported Size of Component Store : 8.53 GB

        Actual Size of Component Store : 8.12 GB

            Shared with Windows : 6.05 GB
            Backups and Disabled Features : 1.50 GB
            Cache and Temporary Data : 512 MB

        Date of Last Cleanup : 2026-07-01 09:12:33

        Number of Reclaimable Packages : 7
        Component Store Cleanup Recommended : Yes

        The operation completed successfully.
        """;

    // ===================================================================================
    //  Größen-Parser
    // ===================================================================================

    [Theory]
    [InlineData("512 B", 512L)]
    [InlineData("1 KB", 1024L)]
    [InlineData("2 MB", 2L * 1024 * 1024)]
    [InlineData("1 GB", 1024L * 1024 * 1024)]
    [InlineData("1 TB", 1024L * 1024 * 1024 * 1024)]
    public void TryParseSize_ErkenntAlleEinheiten(string text, long expected)
    {
        Assert.True(DeepCleanParsers.TryParseSize(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void TryParseSize_AkzeptiertPunktUndKommaAlsDezimaltrenner()
    {
        Assert.True(DeepCleanParsers.TryParseSize("6.29 GB", out long dot));
        Assert.True(DeepCleanParsers.TryParseSize("6,29 GB", out long comma));

        Assert.Equal(dot, comma);
        Assert.Equal((long)Math.Round(6.29 * 1024 * 1024 * 1024), dot);
    }

    // Der Fall, der ohne die Trennzeichen-Erkennung um Faktor 1000 danebenläge: vssadmin gibt
    // auf einem deutschen Windows "1.234,5 MB" aus, auf einem englischen "1,234.5 MB".
    [Fact]
    public void TryParseSize_UnterscheidetTausenderVonDezimaltrenner()
    {
        Assert.True(DeepCleanParsers.TryParseSize("1.234,5 MB", out long german));
        Assert.True(DeepCleanParsers.TryParseSize("1,234.5 MB", out long english));

        long expected = (long)Math.Round(1234.5 * 1024 * 1024);
        Assert.Equal(expected, german);
        Assert.Equal(expected, english);
    }

    /// <summary>
    /// Französisch, Russisch und Polnisch gruppieren mit einem Leerzeichen statt mit Punkt oder
    /// Komma – je nach Windows-Version als normales, geschütztes (U+00A0) oder schmales
    /// geschütztes (U+202F). Ohne diese drei Zeichen läse der Parser nur die „1" von „1 234,5 GB"
    /// und meldete den belegten Platz um Faktor 1000 zu klein.
    /// </summary>
    [Theory]
    [InlineData("1 234,5 MB")]        // normales Leerzeichen
    [InlineData("1 234,5 MB")]   // geschütztes Leerzeichen
    [InlineData("1 234,5 MB")]   // schmales geschütztes Leerzeichen
    public void TryParseSize_ErkenntLeerzeichenAlsTausendertrenner(string text)
    {
        Assert.True(DeepCleanParsers.TryParseSize(text, out long bytes));
        Assert.Equal((long)Math.Round(1234.5 * 1024 * 1024), bytes);
    }

    /// <summary>
    /// DISM schreibt kleine Werte als „0 bytes" statt „0 B" – gemessen an der echten Ausgabe von
    /// <c>/AnalyzeComponentStore</c> („Cache and Temporary Data :  0 bytes"). Ohne die Langform
    /// las der Parser die Zeile gar nicht und das Feld blieb stumm auf 0; bei einem echten
    /// „512 bytes" wäre das eine falsche Zahl gewesen, nicht bloß eine fehlende.
    /// </summary>
    [Theory]
    [InlineData("0 bytes", 0L)]
    [InlineData("512 bytes", 512L)]
    [InlineData("1 byte", 1L)]
    [InlineData("Cache and Temporary Data :  0 bytes", 0L)]
    public void TryParseSize_ErkenntDieLangformBytes(string text, long expected)
    {
        Assert.True(DeepCleanParsers.TryParseSize(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    /// <summary>
    /// Die Langform darf die Kurzform nicht verdrängen: „5 B" bleibt fünf Byte, und „4 KB" darf
    /// nicht über das B von KB in die Byte-Regel rutschen.
    /// </summary>
    [Theory]
    [InlineData("5 B", 5L)]
    [InlineData("4 KB", 4096L)]
    public void TryParseSize_KurzformBleibtUnveraendert(string text, long expected)
    {
        Assert.True(DeepCleanParsers.TryParseSize(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    /// <summary>
    /// Die Leerzeichen-Gruppierung darf die schlichten Fälle nicht kaputt machen: „8 GB" ist
    /// acht Gigabyte, nicht der Anfang einer gruppierten Zahl.
    /// </summary>
    [Fact]
    public void TryParseSize_EinfacheZahlMitLeerzeichenVorEinheit_BleibtUnveraendert()
    {
        Assert.True(DeepCleanParsers.TryParseSize("8 GB", out long bytes));
        Assert.Equal(8L * 1024 * 1024 * 1024, bytes);
    }

    /// <summary>
    /// vssadmin kennt keinen <c>/English</c>-Schalter: die Beschriftungen SIND übersetzt. Die
    /// Auswertung darf deshalb nur an Position, Zahl, Einheit und Prozentwert hängen. Block 1 ist
    /// echte deutsche Ausgabe dieses Systems, Block 2 zeigt übersetzte Beschriftungen mit
    /// Leerzeichen-Gruppierung.
    ///
    /// <para>Die Einheiten stehen hier bewusst als „GB"/„MB": dass vssadmin sie in manchen
    /// Sprachen übersetzt (frz. „Mo"), ließ sich nicht belegen – ein Test darauf wäre geraten.
    /// Träfe es zu, läse der Parser die Zeile nicht und meldete 0 statt einer falschen Zahl.</para>
    /// </summary>
    [Fact]
    public void ParseUsedShadowStorage_KommtMitUebersetztenBeschriftungenUndFormatenZurecht()
    {
        const string localized = """
            Schattenkopie-Speicherassoziation
               Für Volume: (C:)\\?\Volume{11111111-1111-1111-1111-111111111111}\
               Verwendeter Schattenkopie-Speicherbereich: 7,52 GB (0%)
               Zugewiesener Schattenkopie-Speicherbereich: 11,4 GB (0%)
               Max. Schattenkopie-Speicherbereich: 37,2 GB (1%)

            Association de stockage de cliché instantané
               Pour le volume: (D:)\\?\Volume{22222222-2222-2222-2222-222222222222}\
               Espace de stockage de cliché instantané utilisé: 1 024,5 MB (1%)
               Espace de stockage de cliché instantané alloué: 2 048 MB (2%)
            """;

        long used = DeepCleanParsers.ParseUsedShadowStorage(localized);

        // 7,52 GB (belegt, Block 1) + 1 024,5 MB (belegt, Block 2) – „Zugewiesen"/„Max." zählen nie.
        long expected = (long)Math.Round(7.52 * 1024 * 1024 * 1024) + (long)Math.Round(1024.5 * 1024 * 1024);
        Assert.Equal(expected, used);
    }

    [Fact]
    public void TryParseSize_OhneEinheit_LiefertFalse()
    {
        Assert.False(DeepCleanParsers.TryParseSize("Zugriff verweigert", out _));
        Assert.False(DeepCleanParsers.TryParseSize("", out _));
        Assert.False(DeepCleanParsers.TryParseSize(null, out _));
    }

    // ===================================================================================
    //  DISM-Auswertung
    // ===================================================================================

    [Fact]
    public void ParseDismAnalyze_LiestAlleFelder()
    {
        var parsed = DeepCleanParsers.ParseDismAnalyze(DismAnalyzeOutput);

        Assert.True(parsed.IsValid);
        Assert.Equal((long)Math.Round(8.12 * 1024 * 1024 * 1024), parsed.ActualSizeBytes);
        Assert.Equal(7, parsed.ReclaimablePackages);
        Assert.True(parsed.CleanupRecommended);
    }

    /// <summary>
    /// Die wichtigste Zusicherung der ganzen DISM-Auswertung: „Shared with Windows" (6,05 GB)
    /// darf NICHT als freigebbar gelten – das ist fest verbautes Betriebssystem. Freigebbar sind
    /// nur Backups (1,5 GB) + Cache (512 MB).
    /// </summary>
    [Fact]
    public void ParseDismAnalyze_ZaehltNurBackupsUndCacheAlsFreigebbar()
    {
        var parsed = DeepCleanParsers.ParseDismAnalyze(DismAnalyzeOutput);

        long expected = (long)Math.Round(1.50 * 1024 * 1024 * 1024) + 512L * 1024 * 1024;
        Assert.Equal(expected, parsed.ReclaimableBytes);
        Assert.True(parsed.ReclaimableBytes < parsed.ActualSizeBytes);
    }

    [Fact]
    public void ParseDismAnalyze_CleanupNichtEmpfohlen_WirdErkannt()
    {
        string output = DismAnalyzeOutput.Replace(
            "Component Store Cleanup Recommended : Yes",
            "Component Store Cleanup Recommended : No");

        Assert.False(DeepCleanParsers.ParseDismAnalyze(output).CleanupRecommended);
    }

    [Fact]
    public void ParseDismAnalyze_UnbrauchbareAusgabe_IstUngueltig()
    {
        Assert.False(DeepCleanParsers.ParseDismAnalyze("Error: 87\r\nThe command is unknown.").IsValid);
        Assert.False(DeepCleanParsers.ParseDismAnalyze("").IsValid);
        Assert.False(DeepCleanParsers.ParseDismAnalyze(null).IsValid);
    }

    // ===================================================================================
    //  vssadmin-Auswertung
    // ===================================================================================

    // Echte Ausgabe von "vssadmin list shadowstorage" mit zwei Zuordnungsblöcken.
    private const string ShadowStorageOutput = """
        vssadmin 1.1 - Volume Shadow Copy Service administrative command-line tool
        (C) Copyright 2001-2013 Microsoft Corp.

        Shadow Copy Storage association
           For volume: (C:)\\?\Volume{11111111-1111-1111-1111-111111111111}\
           Shadow Copy Storage volume: (C:)\\?\Volume{11111111-1111-1111-1111-111111111111}\
           Used Shadow Copy Storage space: 8 GB (2%)
           Allocated Shadow Copy Storage space: 9 GB (3%)
           Maximum Shadow Copy Storage space: 25 GB (7%)

        Shadow Copy Storage association
           For volume: (D:)\\?\Volume{22222222-2222-2222-2222-222222222222}\
           Shadow Copy Storage volume: (D:)\\?\Volume{22222222-2222-2222-2222-222222222222}\
           Used Shadow Copy Storage space: 2 GB (1%)
           Allocated Shadow Copy Storage space: 3 GB (1%)
           Maximum Shadow Copy Storage space: UNBOUNDED (100%)
        """;

    /// <summary>
    /// Je Block zählt NUR die erste Größe (belegt). „Allocated" und „Maximum" sind Reservierung
    /// bzw. Obergrenze – sie mitzusummieren würde den freigebbaren Platz grotesk überschätzen.
    /// </summary>
    [Fact]
    public void ParseUsedShadowStorage_SummiertNurDenBelegtenPlatzJeBlock()
    {
        long used = DeepCleanParsers.ParseUsedShadowStorage(ShadowStorageOutput);

        Assert.Equal(10L * 1024 * 1024 * 1024, used); // 8 GB + 2 GB
    }

    [Fact]
    public void ParseUsedShadowStorage_OhneBloecke_LiefertNull()
    {
        Assert.Equal(0, DeepCleanParsers.ParseUsedShadowStorage("No items found that satisfy the query."));
        Assert.Equal(0, DeepCleanParsers.ParseUsedShadowStorage(""));
        Assert.Equal(0, DeepCleanParsers.ParseUsedShadowStorage(null));
    }

    // ===================================================================================
    //  sc-Auswertung
    // ===================================================================================

    [Fact]
    public void TryParseServiceState_LiestLaufendenDienst()
    {
        const string output = """
            SERVICE_NAME: wuauserv
                    TYPE               : 20  WIN32_SHARE_PROCESS
                    STATE              : 4  RUNNING
                                            (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)
            """;

        Assert.True(DeepCleanParsers.TryParseServiceState(output, out int state));
        Assert.Equal(DeepCleanParsers.ServiceRunning, state);
    }

    /// <summary>
    /// Auf einem deutschen Windows ist die Beschriftung übersetzt („STATUS"), die
    /// Zustandskonstante rechts bleibt englisch. Genau deshalb hängt das Muster an ihr –
    /// diese Zeile ist der Grund für die ganze Regex.
    /// </summary>
    [Fact]
    public void TryParseServiceState_FunktioniertMitUebersetzterBeschriftung()
    {
        const string german = """
            SERVICE_NAME: wuauserv
                    TYP                : 20  WIN32_SHARE_PROCESS
                    STATUS             : 1  STOPPED
            """;

        Assert.True(DeepCleanParsers.TryParseServiceState(german, out int state));
        Assert.Equal(DeepCleanParsers.ServiceStopped, state);
    }

    [Fact]
    public void TryParseServiceState_UnbekannterDienst_LiefertFalse()
    {
        Assert.False(DeepCleanParsers.TryParseServiceState(
            "[SC] EnumQueryServicesStatus:OpenService FAILED 1060:", out _));
        Assert.False(DeepCleanParsers.TryParseServiceState(null, out _));
    }

    // ===================================================================================
    //  Rechteprüfung
    // ===================================================================================

    [Theory]
    [InlineData(DeepCleanAreaKind.WindowsOld)]
    [InlineData(DeepCleanAreaKind.ComponentStore)]
    [InlineData(DeepCleanAreaKind.ShadowCopies)]
    [InlineData(DeepCleanAreaKind.UpdateCache)]
    [InlineData(DeepCleanAreaKind.DeliveryOptimization)]
    public async Task Analyse_OhneAdminRechte_MeldetAccessDeniedFuerJedenBereich(DeepCleanAreaKind kind)
    {
        var runner = new FakeProcessRunner();
        var env = new FakeEnvironment { IsElevated = false };

        var analysis = await Create(runner, env).AnalyzeAsync(kind, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.AccessDenied, analysis.Status);
        Assert.Equal(0, analysis.SizeBytes);
        // Ohne Rechte darf kein Werkzeug überhaupt gestartet worden sein.
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Bereinigung_OhneAdminRechte_TutNichts()
    {
        var runner = new FakeProcessRunner();
        var env = new FakeEnvironment { IsElevated = false };
        env.SetFolder(@"C:\Windows.old", 5_000_000_000);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.AccessDenied, outcome.Status);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.Empty(runner.Calls);
    }

    // ===================================================================================
    //  Windows.old
    // ===================================================================================

    [Fact]
    public async Task WindowsOld_NichtVorhanden_MeldetUnavailable()
    {
        var env = new FakeEnvironment();   // Ordner bewusst nicht angelegt

        var analysis = await Create(new FakeProcessRunner(), env)
            .AnalyzeAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Unavailable, analysis.Status);
        Assert.False(analysis.IsCleanable);
    }

    [Fact]
    public async Task WindowsOld_Vorhanden_MeldetGroesse()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000);

        var analysis = await Create(new FakeProcessRunner(), env)
            .AnalyzeAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Ready, analysis.Status);
        Assert.Equal(12_000_000_000, analysis.SizeBytes);
        Assert.True(analysis.IsCleanable);
    }

    /// <summary>
    /// Windows.old gehört TrustedInstaller – ohne takeown/icacls scheitert das Löschen an
    /// „Zugriff verweigert". Der Ablauf ist deshalb Teil des Vertrags, nicht Beiwerk.
    /// </summary>
    [Fact]
    public async Task WindowsOld_Bereinigen_NimmtBesitzSetztRechteUndLoescht()
    {
        var runner = new FakeProcessRunner();
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000, 0);

        await Create(runner, env).CleanAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.Equal(3, runner.Calls.Count);
        Assert.Contains("icacls.exe", runner.Calls[0]);
        Assert.Contains("/setowner", runner.Calls[0]);
        Assert.Contains("icacls.exe", runner.Calls[1]);
        Assert.Contains("/grant", runner.Calls[1]);
        Assert.Contains("rd", runner.Calls[2]);
    }

    /// <summary>
    /// takeown.exe darf NICHT zurückkehren: sein Schalter <c>/d</c> erwartet die lokalisierte
    /// Bestätigung („J" auf deutschem Windows), ein festes „Y" scheitert dort mit „Ungültige
    /// Syntax" – und damit scheiterte die ganze Kette. Der Besitzwechsel läuft deshalb über die
    /// SID der Administratoren, die auf jeder Systemsprache dieselbe ist.
    /// </summary>
    [Fact]
    public async Task WindowsOld_Bereinigen_LaeuftSprachneutralUeberDieAdminSid()
    {
        var runner = new FakeProcessRunner();
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000, 0);

        await Create(runner, env).CleanAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        string calls = string.Join(" | ", runner.Calls);
        Assert.DoesNotContain("takeown", calls, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*S-1-5-32-544", calls);
        // Kein übersetzbarer Gruppenname darf sich einschleichen.
        Assert.DoesNotContain("Administrators", calls, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Administratoren", calls, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows.old enthält Junctions, die aus dem Ordner heraus zeigen können. Ohne <c>/l</c>
    /// folgte icacls ihnen bei <c>/t</c> und setzte AUSSERHALB von Windows.old Besitz und
    /// Vollzugriff für Administratoren – eine stille Rechteänderung am restlichen System.
    /// </summary>
    [Fact]
    public async Task WindowsOld_Bereinigen_FolgtKeinenReparsePunkten()
    {
        var runner = new FakeProcessRunner();
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000, 0);

        await Create(runner, env).CleanAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.All(runner.Calls.Where(c => c.StartsWith("icacls.exe", StringComparison.OrdinalIgnoreCase)),
            c => Assert.Contains(" /l", c));
    }

    [Fact]
    public async Task WindowsOld_VollstaendigGeloescht_MeldetFreigewordenenSpeicher()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000);

        // rd räumt den Ordner wirklich weg: danach greift der Zweig „Ordner existiert nicht
        // mehr" (after = 0, ohne Nachmessen) – der eigentliche Erfolgsfall, den sonst kein
        // Test berührt.
        var runner = new FakeProcessRunner
        {
            OnCall = (_, args) => { if (args.Contains("rd")) env.Directories.Remove(@"C:\Windows.old"); }
        };

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Success, outcome.Status);
        Assert.Equal(12_000_000_000, outcome.FreedBytes);
        Assert.False(env.DirectoryExists(@"C:\Windows.old"));
    }

    /// <summary>
    /// Bleibt ein Rest liegen (gesperrte Datei), darf das NICHT als Erfolg durchgehen – und die
    /// gemeldete Zahl muss der echten Differenz entsprechen, nicht der erhofften.
    /// </summary>
    [Fact]
    public async Task WindowsOld_TeilweiseGeloescht_MeldetPartialMitEchterDifferenz()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000, 2_000_000_000);

        var outcome = await Create(new FakeProcessRunner(), env)
            .CleanAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Partial, outcome.Status);
        Assert.Equal(10_000_000_000, outcome.FreedBytes);
    }

    [Fact]
    public async Task WindowsOld_NichtsGeloescht_MeldetFehlerStattErfolg()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000, 12_000_000_000);

        var outcome = await Create(new FakeProcessRunner(), env)
            .CleanAsync(DeepCleanAreaKind.WindowsOld, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, outcome.FreedBytes);
    }

    // ===================================================================================
    //  Komponentenspeicher
    // ===================================================================================

    [Fact]
    public async Task ComponentStore_Analyse_RuftDismMitEnglishAufUndMeldetFreigebbares()
    {
        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "AnalyzeComponentStore", new ProcessResult(0, DismAnalyzeOutput, ""));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Ready, analysis.Status);
        Assert.Equal(7, analysis.ItemCount);
        // /English ist die Grundlage der ganzen Auswertung – ohne den Schalter wäre sie geraten.
        Assert.Contains("/English", runner.Calls[0]);
    }

    /// <summary>
    /// <c>/English</c> muss an JEDEM DISM-Aufruf stehen, nicht nur an der ersten Analyse: die
    /// Auswertung sucht nach englischen Bezeichnern („Actual Size of Component Store"). Fehlte
    /// der Schalter an der Vorher-/Nachher-Messung, wäre der gemeldete freigewordene Speicher
    /// auf jedem nicht-englischen Windows still 0.
    /// </summary>
    [Fact]
    public async Task ComponentStore_JederDismAufruf_TraegtEnglish()
    {
        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "AnalyzeComponentStore", new ProcessResult(0, DismAnalyzeOutput, ""));

        await Create(runner, new FakeEnvironment()).CleanAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        var dismCalls = runner.Calls.Where(c => c.StartsWith("dism.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, dismCalls.Count);   // Messung vorher, Bereinigung, Messung nachher
        Assert.All(dismCalls, c => Assert.Contains("/English", c));
    }

    // Hinweis zur Testbarkeit: Die Meldungstexte selbst kommen aus den Sprachdateien und werden
    // hier NICHT geprüft – ein Test gegen "DISM meldete Fehler 87" würde nur die Übersetzung
    // festnageln und bei jeder Umformulierung brechen. Geprüft wird die Entscheidung dahinter
    // (welcher Schlüssel, welche Argumente) über ToolFailureInfo; die Übersetzung ist Anzeige.

    [Fact]
    public void ToolFailureInfo_WerkzeugFehlt_WaehltToolMissingMitWerkzeugname()
    {
        var (key, args) = DeepCleanService.ToolFailureInfo(
            new ProcessResult(ProcessRunner.LaunchFailedExitCode, "", ""), "DISM");

        Assert.Equal("deepclean.error.toolMissing", key);
        Assert.Equal(new object[] { "DISM" }, args);
    }

    [Fact]
    public void ToolFailureInfo_Zeitlimit_WaehltTimeout()
    {
        var (key, args) = DeepCleanService.ToolFailureInfo(
            new ProcessResult(ProcessRunner.TimeoutExitCode, "", ""), "DISM");

        Assert.Equal("deepclean.error.timeout", key);
        Assert.Equal(new object[] { "DISM" }, args);
    }

    /// <summary>
    /// Der Exit-Code MUSS in der Meldung landen: „DISM ist fehlgeschlagen" ohne Code ist für
    /// eine Fehlersuche wertlos – 87 (falscher Parameter) und 1726 (RPC) verlangen völlig
    /// unterschiedliche Reaktionen.
    /// </summary>
    [Fact]
    public void ToolFailureInfo_SonstigerFehler_ReichtExitCodeDurch()
    {
        var (key, args) = DeepCleanService.ToolFailureInfo(new ProcessResult(87, "", "Error: 87"), "DISM");

        Assert.Equal("deepclean.error.exitCode", key);
        Assert.Equal(new object[] { "DISM", 87 }, args);
    }

    [Fact]
    public async Task ComponentStore_DismFehlt_MeldetFehlerMitMeldung()
    {
        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "Cleanup-Image",
            new ProcessResult(ProcessRunner.LaunchFailedExitCode, "", ""));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Failed, analysis.Status);
        Assert.NotEmpty(analysis.Message);
        Assert.False(analysis.IsCleanable);
    }

    [Fact]
    public async Task ComponentStore_DismExitcode_MeldetFehlschlag()
    {
        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "Cleanup-Image", new ProcessResult(87, "", "Error: 87"));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Failed, analysis.Status);
        Assert.Equal(0, analysis.SizeBytes);
    }

    [Fact]
    public async Task ComponentStore_Zeitlimit_WirdAlsSolchesGemeldet()
    {
        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "Cleanup-Image", new ProcessResult(ProcessRunner.TimeoutExitCode, "", ""));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Failed, analysis.Status);
    }

    [Fact]
    public async Task ComponentStore_NichtsFreigebbar_MeldetEmpty()
    {
        string output = DismAnalyzeOutput
            .Replace("Backups and Disabled Features : 1.50 GB", "Backups and Disabled Features : 0 B")
            .Replace("Cache and Temporary Data : 512 MB", "Cache and Temporary Data : 0 B");

        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "AnalyzeComponentStore", new ProcessResult(0, output, ""));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Empty, analysis.Status);
        Assert.False(analysis.IsCleanable);
    }

    /// <summary>
    /// DISM-Exitcode 3010 heißt „Erfolg, Neustart nötig". Als Fehler zu werten hieße, dem Nutzer
    /// eine erfolgreiche Bereinigung als Fehlschlag zu melden.
    /// </summary>
    [Fact]
    public async Task ComponentStore_Exitcode3010_GiltAlsErfolg()
    {
        string after = DismAnalyzeOutput.Replace(
            "Actual Size of Component Store : 8.12 GB",
            "Actual Size of Component Store : 6.12 GB");

        var runner = new FakeProcessRunner();
        var responses = new Queue<ProcessResult>(new[]
        {
            new ProcessResult(0, DismAnalyzeOutput, ""),   // Messung vorher
            new ProcessResult(3010, "", ""),               // Bereinigung: Neustart nötig
            new ProcessResult(0, after, "")                // Messung nachher
        });
        runner.Default = new ProcessResult(0, "", "");
        var service = Create(new SequenceRunner(responses), new FakeEnvironment());

        var outcome = await service.CleanAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Success, outcome.Status);
        Assert.Equal((long)Math.Round(2.0 * 1024 * 1024 * 1024), outcome.FreedBytes);
    }

    /// <summary>Prozess-Attrappe, die eine feste Antwortfolge abspielt (für Vorher/Nachher-Messungen).</summary>
    private sealed class SequenceRunner : IProcessRunner
    {
        private readonly Queue<ProcessResult> _responses;
        public SequenceRunner(Queue<ProcessResult> responses) => _responses = responses;

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new ProcessResult(0, "", ""));
    }

    [Fact]
    public async Task ComponentStore_BereinigungFehlgeschlagen_MeldetKeinenFreienSpeicher()
    {
        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "AnalyzeComponentStore", new ProcessResult(0, DismAnalyzeOutput, ""));
        runner.When("dism.exe", "StartComponentCleanup", new ProcessResult(1726, "", "RPC failed"));

        var outcome = await Create(runner, new FakeEnvironment())
            .CleanAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.NotEmpty(outcome.Message);
    }

    // ===================================================================================
    //  Schattenkopien
    // ===================================================================================

    [Fact]
    public async Task Schattenkopien_Analyse_MeldetBelegtenPlatzUndPunktzahl()
    {
        var runner = new FakeProcessRunner();
        runner.When("vssadmin.exe", "shadowstorage", new ProcessResult(0, ShadowStorageOutput, ""));
        var env = new FakeEnvironment { RestorePointCount = 4 };

        var analysis = await Create(runner, env).AnalyzeAsync(DeepCleanAreaKind.ShadowCopies, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Ready, analysis.Status);
        Assert.Equal(10L * 1024 * 1024 * 1024, analysis.SizeBytes);
        Assert.Equal(4, analysis.ItemCount);
    }

    /// <summary>
    /// Der jüngste Punkt bleibt immer stehen. Bei genau einem Punkt gibt es deshalb nichts zu
    /// tun – auch wenn vssadmin Gigabytes an belegtem Platz meldet. Eine „Ready"-Anzeige wäre
    /// hier ein Versprechen, das die Bereinigung nicht halten kann.
    /// </summary>
    [Fact]
    public async Task Schattenkopien_NurEinPunkt_MeldetEmptyTrotzBelegtemPlatz()
    {
        var runner = new FakeProcessRunner();
        runner.When("vssadmin.exe", "shadowstorage", new ProcessResult(0, ShadowStorageOutput, ""));
        var env = new FakeEnvironment { RestorePointCount = 1 };

        var analysis = await Create(runner, env).AnalyzeAsync(DeepCleanAreaKind.ShadowCopies, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Empty, analysis.Status);
        Assert.False(analysis.IsCleanable);
    }

    [Fact]
    public async Task Schattenkopien_NurEinPunkt_BereinigungRuehrtNichtsAn()
    {
        var env = new FakeEnvironment { RestorePointCount = 1 };

        var outcome = await Create(new FakeProcessRunner(), env)
            .CleanAsync(DeepCleanAreaKind.ShadowCopies, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.NothingToDo, outcome.Status);
        Assert.Equal(0, env.RemoveCalls);
    }

    [Fact]
    public async Task Schattenkopien_Bereinigen_MeldetEntfernteUndFreigewordenes()
    {
        var runner = new FakeProcessRunner();
        var responses = new Queue<ProcessResult>(new[]
        {
            new ProcessResult(0, ShadowStorageOutput, ""),   // vorher: 10 GB
            new ProcessResult(0, ShadowStorageOutput.Replace("Used Shadow Copy Storage space: 8 GB (2%)",
                                                             "Used Shadow Copy Storage space: 1 GB (1%)"), "")
        });
        var env = new FakeEnvironment { RestorePointCount = 5, RemoveResult = (4, 0) };

        var outcome = await Create(new SequenceRunner(responses), env)
            .CleanAsync(DeepCleanAreaKind.ShadowCopies, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Success, outcome.Status);
        Assert.Equal(7L * 1024 * 1024 * 1024, outcome.FreedBytes);   // 10 GB -> 3 GB
        Assert.Equal(1, env.RemoveCalls);
    }

    [Fact]
    public async Task Schattenkopien_TeilweiseEntfernt_MeldetPartial()
    {
        var env = new FakeEnvironment { RestorePointCount = 5, RemoveResult = (2, 2) };

        var outcome = await Create(new FakeProcessRunner(), env)
            .CleanAsync(DeepCleanAreaKind.ShadowCopies, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Partial, outcome.Status);
    }

    [Fact]
    public async Task Schattenkopien_KeinerEntfernt_MeldetFehler()
    {
        var env = new FakeEnvironment { RestorePointCount = 5, RemoveResult = (0, 4) };

        var outcome = await Create(new FakeProcessRunner(), env)
            .CleanAsync(DeepCleanAreaKind.ShadowCopies, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Failed, outcome.Status);
    }

    // ===================================================================================
    //  Update-Zwischenspeicher / Übermittlungsoptimierung
    // ===================================================================================

    [Fact]
    public async Task UpdateCache_Analyse_MeldetOrdnergroesse()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_500_000_000);

        var analysis = await Create(new FakeProcessRunner(), env)
            .AnalyzeAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Ready, analysis.Status);
        Assert.Equal(3_500_000_000, analysis.SizeBytes);
    }

    /// <summary>
    /// Ein FEHLENDER Ordner ist „nicht verfügbar", nicht „leer". „Leer" hat den falschen
    /// DO-Pfad als „nichts zu tun" getarnt – dieser Test hält den Unterschied fest.
    /// </summary>
    [Theory]
    [InlineData(DeepCleanAreaKind.UpdateCache)]
    [InlineData(DeepCleanAreaKind.DeliveryOptimization)]
    public async Task Ordnerbereich_PfadExistiertNicht_MeldetUnavailableStattEmpty(DeepCleanAreaKind kind)
    {
        var env = new FakeEnvironment();   // kein Ordner angelegt

        var analysis = await Create(new FakeProcessRunner(), env).AnalyzeAsync(kind, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Unavailable, analysis.Status);
        Assert.False(analysis.IsCleanable);
    }

    [Fact]
    public async Task UpdateCache_LeererOrdner_MeldetEmpty()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 0);

        var analysis = await Create(new FakeProcessRunner(), env)
            .AnalyzeAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Empty, analysis.Status);
    }

    /// <summary>
    /// wuauserv und bits halten die Dateien offen. Werden sie nicht gestoppt, löscht die
    /// Bereinigung fast nichts – und der Nutzer sähe einen „Erfolg" ohne Wirkung.
    /// </summary>
    [Fact]
    public async Task UpdateCache_Bereinigen_StopptDiensteLeertUndStartetSieWieder()
    {
        var runner = new FakeProcessRunner();
        runner.When("sc.exe", "query", new ProcessResult(0, "STATE : 1  STOPPED", ""));
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_000_000_000, 0);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Success, outcome.Status);
        Assert.Equal(3_000_000_000, outcome.FreedBytes);
        Assert.Equal(1, env.EmptyCalls);

        string calls = string.Join(" | ", runner.Calls);
        Assert.Contains("sc.exe stop wuauserv", calls);
        Assert.Contains("sc.exe stop bits", calls);
        Assert.Contains("sc.exe start wuauserv", calls);
        Assert.Contains("sc.exe start bits", calls);
    }

    /// <summary>
    /// Lässt sich ein Dienst nicht stoppen, wird NICHT gelöscht: das Löschen offener Dateien
    /// würde Windows Update beschädigen statt Platz zu schaffen.
    /// </summary>
    [Fact]
    public async Task UpdateCache_DienstStopptNicht_LoeschtNichts()
    {
        var runner = new FakeProcessRunner();
        runner.When("sc.exe", "stop", new ProcessResult(5, "", "Access is denied."));
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_000_000_000, 0);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, outcome.FreedBytes);
        // Der Kern der Zusicherung: es wurde NICHTS gelöscht, solange der Dienst die Dateien hält.
        Assert.Equal(0, env.EmptyCalls);
    }

    /// <summary>Ein bereits gestoppter Dienst (1062) ist genau der Zielzustand, kein Fehler.</summary>
    [Fact]
    public async Task UpdateCache_DienstBereitsGestoppt_LaeuftNormalWeiter()
    {
        var runner = new FakeProcessRunner();
        runner.When("sc.exe", "stop", new ProcessResult(1062, "", ""));
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_000_000_000, 0);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Success, outcome.Status);
        Assert.Equal(1, env.EmptyCalls);
    }

    [Fact]
    public async Task DeliveryOptimization_Bereinigen_StopptDoSvc()
    {
        var runner = new FakeProcessRunner();
        runner.When("sc.exe", "query", new ProcessResult(0, "STATE : 1  STOPPED", ""));
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\DeliveryOptimization", 1_500_000_000, 0);

        var outcome = await Create(runner, env)
            .CleanAsync(DeepCleanAreaKind.DeliveryOptimization, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Success, outcome.Status);
        Assert.Equal(1_500_000_000, outcome.FreedBytes);

        string calls = string.Join(" | ", runner.Calls);
        Assert.Contains("sc.exe stop DoSvc", calls);
        Assert.Contains("sc.exe start DoSvc", calls);
    }

    /// <summary>
    /// Auch wenn beim Leeren Dateien gesperrt waren, MUSS der Dienst wieder laufen – ein
    /// dauerhaft gestopptes Windows Update wäre ein weit größerer Schaden als der belegte Platz.
    /// </summary>
    [Fact]
    public async Task UpdateCache_TeilweiseGeleert_StartetDiensteTrotzdemWieder()
    {
        var runner = new FakeProcessRunner();
        runner.When("sc.exe", "query", new ProcessResult(0, "STATE : 1  STOPPED", ""));
        var env = new FakeEnvironment { EmptyFailures = 3 };
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_000_000_000, 1_000_000_000);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Partial, outcome.Status);
        Assert.Equal(2_000_000_000, outcome.FreedBytes);

        string calls = string.Join(" | ", runner.Calls);
        Assert.Contains("sc.exe start wuauserv", calls);
        Assert.Contains("sc.exe start bits", calls);
    }

    /// <summary>
    /// Der gefährlichste Fall: <c>sc stop</c> GELINGT, aber der Dienst steht innerhalb der
    /// Wartezeit nicht (langsame Platte, wuauserv arbeitet an einem Update). Der Stopp-Befehl
    /// ist damit abgesetzt und wirkt weiter – der Dienst MUSS trotzdem wieder gestartet werden.
    /// Sonst bliebe Windows Update bis zum nächsten Neustart tot, ohne dass es jemand merkt.
    /// </summary>
    [Fact]
    public async Task UpdateCache_DienstStopptNichtRechtzeitig_StartetIhnTrotzdemWieder()
    {
        var runner = new FakeProcessRunner();
        // sc stop meldet Erfolg, sc query meldet den Dienst aber dauerhaft als laufend.
        runner.When("sc.exe", "query", new ProcessResult(0, "STATE : 4  RUNNING", ""));
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_000_000_000, 3_000_000_000);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, env.EmptyCalls);

        string calls = string.Join(" | ", runner.Calls);
        Assert.Contains("sc.exe stop wuauserv", calls);
        Assert.Contains("sc.exe start wuauserv", calls);
    }

    /// <summary>
    /// Szenario C: Der erste Dienst stoppt sauber, der zweite bleibt hängen. Beide sind
    /// angefasst worden, beide müssen wieder laufen – nicht nur der erste.
    /// </summary>
    [Fact]
    public async Task UpdateCache_ZweiterDienstStopptNicht_StartetBeideWieder()
    {
        var runner = new FakeProcessRunner();
        runner.WhenAll("sc.exe", new[] { "query", "wuauserv" }, new ProcessResult(0, "STATE : 1  STOPPED", ""));
        runner.WhenAll("sc.exe", new[] { "query", "bits" }, new ProcessResult(0, "STATE : 4  RUNNING", ""));
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_000_000_000, 3_000_000_000);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(0, env.EmptyCalls);

        string calls = string.Join(" | ", runner.Calls);
        Assert.Contains("sc.exe start wuauserv", calls);
        Assert.Contains("sc.exe start bits", calls);   // der hängengebliebene Dienst
    }

    /// <summary>Prozess-Attrappe, die beim ersten <c>sc query</c> den Abbruch auslöst.</summary>
    private sealed class CancelOnQueryRunner : IProcessRunner
    {
        private readonly CancellationTokenSource _cts;
        public CancelOnQueryRunner(CancellationTokenSource cts) => _cts = cts;

        public List<string> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(fileName + " " + string.Join(" ", arguments));

            if (arguments.Contains("query"))
            {
                _cts.Cancel();
                return Task.FromResult(new ProcessResult(0, "STATE : 4  RUNNING", ""));
            }

            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    /// <summary>
    /// Szenario B: Der Nutzer bricht ab, während auf den Stopp gewartet wird. Der Abbruch darf
    /// den bereits abgesetzten Stopp nicht einfach stehen lassen – der Neustart läuft deshalb
    /// bewusst ohne Abbruch-Token.
    /// </summary>
    [Fact]
    public async Task UpdateCache_AbbruchWaehrendWartezeit_StartetDienstTrotzdemWieder()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CancelOnQueryRunner(cts);
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_000_000_000, 3_000_000_000);

        var outcome = await Create(runner, env).CleanAsync(DeepCleanAreaKind.UpdateCache, cts.Token);

        Assert.Equal(DeepCleanOutcomeStatus.Canceled, outcome.Status);
        Assert.Equal(0, env.EmptyCalls);
        Assert.Contains(runner.Calls, c => c.Contains("sc.exe start wuauserv"));
    }

    // ===================================================================================
    //  Abbruch
    // ===================================================================================

    [Fact]
    public async Task Analyse_Abbruch_WirftOperationCanceled()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(new FakeProcessRunner(), env).AnalyzeAsync(DeepCleanAreaKind.WindowsOld, cts.Token));
    }

    /// <summary>
    /// Ein Abbruch der Bereinigung darf NICHT als Ausnahme beim Aufrufer landen: das ViewModel
    /// soll ihn als normalen Ausgang anzeigen können.
    /// </summary>
    [Fact]
    public async Task Bereinigung_Abbruch_MeldetCanceledStattAusnahme()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows.old", 12_000_000_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await Create(new FakeProcessRunner(), env)
            .CleanAsync(DeepCleanAreaKind.WindowsOld, cts.Token);

        Assert.Equal(DeepCleanOutcomeStatus.Canceled, outcome.Status);
        Assert.Equal(0, outcome.FreedBytes);
    }

    // ===================================================================================
    //  Bereichs-Metadaten
    // ===================================================================================

    // ===================================================================================
    //  Fortschritt: Balken-Parser
    // ===================================================================================

    /// <summary>
    /// Der Prozentwert steht MITTEN im Balken, und der Dezimaltrenner richtet sich nach der
    /// Systemsprache – auch mit <c>/English</c>. Beide Trenner müssen gelesen werden, sonst
    /// stünde der Balken auf einem deutschen Windows dauerhaft still.
    /// </summary>
    [Theory]
    [InlineData("[===                        5.9%                           ] ", 5.9)]
    [InlineData("[===                        5,9%                           ] ", 5.9)]
    [InlineData("[===========================60.0%==                        ] ", 60.0)]
    [InlineData("[==========================100.0%==========================] ", 100.0)]
    [InlineData("[=====                      42%                            ] ", 42.0)]   // ohne Nachkommastelle
    [InlineData("[=                          0.0%                           ] ", 0.0)]
    public void TryParseProgressPercent_LiestDenWertAusDerBalkenzeile(string segment, double expected)
    {
        Assert.True(DeepCleanParsers.TryParseProgressPercent(segment, out double percent));
        Assert.Equal(expected, percent, 3);
    }

    /// <summary>
    /// Alles, was kein Balken ist, darf KEINEN Fortschritt liefern: Sonst würde eine beliebige
    /// Zeile mit Prozentzeichen den Balken springen lassen.
    /// </summary>
    [Theory]
    [InlineData("Deployment Image Servicing and Management tool")]
    [InlineData("Actual Size of Component Store : 22.13 GB")]
    [InlineData("Image Version: 10.0.26200.8875")]
    [InlineData("The operation completed successfully.")]
    [InlineData("Used Shadow Copy Storage space: 8 GB (2%)")]   // Prozent, aber kein Balken
    [InlineData("")]
    [InlineData(null)]
    public void TryParseProgressPercent_OhneBalken_LiefertFalse(string? segment)
    {
        Assert.False(DeepCleanParsers.TryParseProgressPercent(segment, out _));
    }

    /// <summary>Über 100 % kann kein Fortschritt sein – so etwas stammt nicht aus einem Balken.</summary>
    [Fact]
    public void TryParseProgressPercent_UnsinnigerWert_LiefertFalse()
    {
        Assert.False(DeepCleanParsers.TryParseProgressPercent("[=== 350% ===]", out _));
    }

    // ===================================================================================
    //  Fortschritt: Monotonie
    // ===================================================================================

    /// <summary>
    /// DISM meldet denselben Wert mehrfach (nachgemessen: fünfmal 16,5 %, fünfmal 97,8 %). Eine
    /// Wiederholung ist kein Fortschritt – und ein Rückwärtssprung darf den Balken NIE
    /// zurücklaufen lassen, das liest sich wie „fängt von vorne an".
    /// </summary>
    [Fact]
    public void DismProgressTracker_IgnoriertWiederholungenUndRueckwaertsspruenge()
    {
        var tracker = new DismProgressTracker();

        Assert.True(tracker.TryAdvance("[= 5.9% ]", out double p1));
        Assert.Equal(5.9, p1, 3);

        // Derselbe Wert noch einmal -> kein Fortschritt, Stand bleibt.
        Assert.False(tracker.TryAdvance("[= 5.9% ]", out _));
        Assert.Equal(5.9, tracker.Percent, 3);

        // Rückwärts -> abgewiesen, Stand bleibt.
        Assert.False(tracker.TryAdvance("[= 2.0% ]", out _));
        Assert.Equal(5.9, tracker.Percent, 3);

        // Zeile ganz ohne Prozent -> abgewiesen, Stand bleibt.
        Assert.False(tracker.TryAdvance("The operation completed successfully.", out _));
        Assert.Equal(5.9, tracker.Percent, 3);

        Assert.True(tracker.TryAdvance("[= 12.5% ]", out double p2));
        Assert.Equal(12.5, p2, 3);
    }

    // ===================================================================================
    //  Fortschritt: echte DISM-Ausgabe
    // ===================================================================================

    /// <summary>
    /// Die 26 Balken einer ECHTEN Ausführung von
    /// <c>dism /Online /Cleanup-Image /AnalyzeComponentStore /English</c> auf diesem System
    /// (Dauer 32,6 s), Zeichen für Zeichen wie ausgegeben – samt der Wiederholungen.
    /// </summary>
    private static readonly string[] RealDismProgressBars =
    {
        "[===                        5.9%                           ] ",
        "[=======                    12.5%                          ] ",
        "[=========                  16.5%                          ] ",
        "[=========                  16.5%                          ] ",
        "[=========                  16.5%                          ] ",
        "[=========                  16.5%                          ] ",
        "[=========                  16.5%                          ] ",
        "[==========                 17.9%                          ] ",
        "[==========                 17.9%                          ] ",
        "[===========                19.1%                          ] ",
        "[=============              22.9%                          ] ",
        "[================           28.8%                          ] ",
        "[==================         34.1%                          ] ",
        "[=====================      39.4%                          ] ",
        "[========================   44.7%                          ] ",
        "[===========================51.9%                          ] ",
        "[===========================56.4%                          ] ",
        "[===========================60.0%==                        ] ",
        "[===========================76.1%===============           ] ",
        "[===========================76.9%================          ] ",
        "[===========================97.8%========================  ] ",
        "[===========================97.8%========================  ] ",
        "[===========================97.8%========================  ] ",
        "[===========================97.8%========================  ] ",
        "[===========================97.8%========================  ] ",
        "[==========================100.0%==========================] "
    };

    /// <summary>
    /// Baut den Strom so nach, wie DISM ihn WIRKLICH schreibt: je Balken ein alleinstehender
    /// Wagenrücklauf davor. Nachgemessen an der echten Ausführung: 75 CR gegen 49 LF – 26 Balken
    /// hängen an einem <c>\r</c>. Genau daran scheitert zeilenweises Lesen.
    /// </summary>
    private static string BuildRealDismStream()
    {
        var text = new System.Text.StringBuilder();
        text.Append("\r\nDeployment Image Servicing and Management tool\r\n");
        text.Append("Version: 10.0.26100.8737\r\n\r\nImage Version: 10.0.26200.8875\r\n\r\n");

        foreach (string bar in RealDismProgressBars)
            text.Append('\r').Append(bar).Append("\r\n");

        text.Append("\r\nComponent Store (WinSxS) information:\r\n\r\n");
        text.Append("Actual Size of Component Store : 22.13 GB\r\n\r\n");
        text.Append("    Shared with Windows : 7.93 GB\r\n");
        text.Append("    Backups and Disabled Features : 14.19 GB\r\n");
        text.Append("    Cache and Temporary Data : 1.88 MB\r\n\r\n");
        text.Append("Number of Reclaimable Packages : 10\r\n");
        text.Append("Component Store Cleanup Recommended : Yes\r\n\r\n");
        text.Append("The operation completed successfully.\r\n");
        return text.ToString();
    }

    /// <summary>
    /// Der Nachweis, um den es geht: Aus der ECHTEN DISM-Ausgabe wird eine streng steigende
    /// Reihe von 5,9 % bis 100 %. Das Lesen läuft dabei über denselben Weg wie im Betrieb –
    /// blockweise aus dem Strom, selbst getrennt an <c>\r</c> und <c>\n</c>.
    /// </summary>
    [Fact]
    public async Task ProcessRunner_EchteDismAusgabe_ErgibtMonotonSteigendeReihe()
    {
        var raw = new List<double>();
        string text = await ProcessRunner.ReadWithProgressAsync(
            new System.IO.StringReader(BuildRealDismStream()), p => raw.Add(p));

        // Jeder Balken wird gemeldet – auch die Wiederholungen: Sie sind die einzige Auskunft,
        // dass das Werkzeug noch lebt.
        Assert.Equal(26, raw.Count);

        // Der Text kommt unversehrt zurück; die Auswertung der Größen hängt daran.
        Assert.Contains("Actual Size of Component Store : 22.13 GB", text);
        Assert.True(DeepCleanParsers.ParseDismAnalyze(text).IsValid);

        // Und jetzt die eigentliche Zusicherung: monoton steigend, ohne einen Rückschritt.
        var tracker = new DismProgressTracker();
        var shown = new List<double>();
        foreach (double value in raw)
        {
            if (tracker.TryAdvance(value, out double percent)) shown.Add(percent);
        }

        Assert.Equal(17, shown.Count);                       // 26 Balken, 9 davon Wiederholungen
        Assert.Equal(5.9, shown[0], 3);
        Assert.Equal(100.0, shown[^1], 3);
        for (int i = 1; i < shown.Count; i++)
            Assert.True(shown[i] > shown[i - 1], $"Rückschritt bei {shown[i - 1]} -> {shown[i]}");
    }

    /// <summary>
    /// Der Fall, der zeilenweises Lesen erledigt: ein Strom GANZ OHNE Zeilenumbruch, in dem der
    /// Balken nur per Wagenrücklauf überschrieben wird. <c>ReadLine</c> lieferte hier bis zum
    /// Schluss nichts.
    /// </summary>
    [Fact]
    public async Task ProcessRunner_NurWagenruecklaeufe_MeldetTrotzdemJedenBalken()
    {
        const string stream = "\r[=   10.0%   ] \r[==  40.0%   ] \r[=== 90.0%   ] ";

        var raw = new List<double>();
        await ProcessRunner.ReadWithProgressAsync(new System.IO.StringReader(stream), p => raw.Add(p));

        Assert.Equal(new[] { 10.0, 40.0, 90.0 }, raw);
    }

    /// <summary>Ohne Rückruf bleibt das Auslesen, was es war: der vollständige Text.</summary>
    [Fact]
    public async Task ProcessRunner_OhneFortschrittsRueckruf_LiefertDenTextUnveraendert()
    {
        string expected = BuildRealDismStream();

        string actual = await ProcessRunner.ReadWithProgressAsync(
            new System.IO.StringReader(expected), onProgress: null);

        Assert.Equal(expected, actual);
    }

    // ===================================================================================
    //  Fortschritt: Restzeit
    // ===================================================================================

    /// <summary>
    /// Unterhalb des Sockels gibt es KEINE Zahl. Bei 1 % nach einer Sekunde stünde dort „noch
    /// 1:39", eine Sekunde später etwas völlig anderes – solche Sprünge sind schlimmer als gar
    /// keine Angabe.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0)]      // zu wenig Prozent UND zu wenig Zeit
    [InlineData(0.5, 30.0)]     // viel Zeit, aber zu wenig Prozent
    [InlineData(50.0, 0.5)]     // viel Prozent, aber zu wenig Zeit
    public void EtaEstimator_UnterhalbDesSockels_SchweigtLieber(double percent, double elapsedSeconds)
    {
        var eta = new EtaEstimator();

        DeepCleanEta result = eta.Update(TimeSpan.FromSeconds(elapsedSeconds), percent);

        Assert.Equal(DeepCleanEtaKind.None, result.Kind);
    }

    /// <summary>
    /// Über dem Sockel kommt eine Zahl – und sie muss zur Hochrechnung passen: 25 % in 10 s
    /// heißt rund 30 s Rest.
    /// </summary>
    [Fact]
    public void EtaEstimator_UeberDemSockel_RechnetRestzeitHoch()
    {
        var eta = new EtaEstimator();

        DeepCleanEta result = eta.Update(TimeSpan.FromSeconds(10), 25.0);

        Assert.Equal(DeepCleanEtaKind.Value, result.Kind);
        Assert.Equal(30.0, result.Remaining.TotalSeconds, 1);
    }

    /// <summary>
    /// Die angezeigte Restzeit darf nicht hochlaufen, solange die Lage nur leicht schlechter
    /// wird – eine wachsende Restzeit wirkt wie ein Hänger. Erst bei einer DEUTLICHEN
    /// Verschlechterung darf sie springen, sonst wäre sie eine Lüge nach unten.
    /// </summary>
    [Fact]
    public void EtaEstimator_LaeuftNichtZurueck_AusserBeiDeutlicherVerschlechterung()
    {
        var eta = new EtaEstimator();

        // Ausgangslage: 50 % in 10 s -> 10 s Rest.
        double first = eta.Update(TimeSpan.FromSeconds(10), 50.0).Remaining.TotalSeconds;
        Assert.Equal(10.0, first, 1);

        // Leicht schlechter (60 % in 13 s -> ~8,7 s Rest, geglättet ~9,3 s): darf nicht steigen.
        double second = eta.Update(TimeSpan.FromSeconds(13), 60.0).Remaining.TotalSeconds;
        Assert.True(second <= first + 0.001, $"Restzeit lief hoch: {first} -> {second}");

        // Deutlich schlechter: 51 % erst nach 60 s -> ~58 s Rest. Diese Verschlechterung MUSS
        // durchschlagen; sie festzuhalten hieße, dem Nutzer eine Zahl zu zeigen, die nie eintritt.
        var worse = new EtaEstimator();
        worse.Update(TimeSpan.FromSeconds(10), 50.0);
        double jumped = worse.Update(TimeSpan.FromSeconds(120), 51.0).Remaining.TotalSeconds;
        Assert.True(jumped > 20, $"Deutliche Verschlechterung wurde verschluckt: {jumped}");
    }

    /// <summary>
    /// Steht der Fortschritt, gibt es keine Zahl mehr, sondern ein ehrliches „noch einige Zeit".
    /// Die alte Zahl weiterzuzeigen (oder herunterzuzählen) wäre eine Behauptung ohne Deckung.
    /// </summary>
    [Fact]
    public void EtaEstimator_StehenderFortschritt_MeldetUnknownStattEinerZahl()
    {
        var eta = new EtaEstimator();

        Assert.Equal(DeepCleanEtaKind.Value, eta.Update(TimeSpan.FromSeconds(10), 50.0).Kind);

        // Derselbe Prozentwert, aber die Zeit läuft: nach 15 s ohne Fortschritt ist Schluss.
        Assert.Equal(DeepCleanEtaKind.Value, eta.Update(TimeSpan.FromSeconds(20), 50.0).Kind);
        Assert.Equal(DeepCleanEtaKind.Unknown, eta.Update(TimeSpan.FromSeconds(40), 50.0).Kind);

        // Geht es weiter, gibt es wieder eine Zahl.
        Assert.Equal(DeepCleanEtaKind.Value, eta.Update(TimeSpan.FromSeconds(41), 60.0).Kind);
    }

    /// <summary>Bei 100 % ist nichts mehr übrig – dort gehört keine Restzeit hin.</summary>
    [Fact]
    public void EtaEstimator_BeiHundertProzent_ZeigtKeineRestzeit()
    {
        var eta = new EtaEstimator();
        eta.Update(TimeSpan.FromSeconds(10), 50.0);

        Assert.Equal(DeepCleanEtaKind.None, eta.Update(TimeSpan.FromSeconds(20), 100.0).Kind);
    }

    // ===================================================================================
    //  Fortschritt: Durchreichen bis zum Aufrufer
    // ===================================================================================

    /// <summary>Prozess-Attrappe, die einen DISM-Lauf samt Fortschrittsbalken nachspielt.</summary>
    private sealed class ProgressRunner : IProcessRunner
    {
        private readonly IReadOnlyList<double> _percents;
        private readonly string _output;

        public ProgressRunner(IReadOnlyList<double> percents, string output)
        {
            _percents = percents;
            _output = output;
        }

        /// <summary>Aufrufe, bei denen der Aufrufer einen Fortschritt haben wollte.</summary>
        public List<string> ProgressCalls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, _output, ""));

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            Action<double>? onProgress, CancellationToken ct)
        {
            if (onProgress is not null)
            {
                ProgressCalls.Add(fileName + " " + string.Join(" ", arguments));
                foreach (double p in _percents) onProgress(p);
            }

            return Task.FromResult(new ProcessResult(0, _output, ""));
        }
    }

    /// <summary>
    /// Der Kern der Anzeige: Was DISM meldet, muss beim Aufrufer ankommen – monoton und ohne die
    /// Wiederholungen als Rückschritt. Der Kommentar, DISM melde keinen Fortschritt, war schlicht
    /// falsch.
    /// </summary>
    [Fact]
    public async Task ComponentStore_Analyse_ReichtDenDismFortschrittDurch()
    {
        var percents = new List<double>();
        foreach (string bar in RealDismProgressBars)
        {
            DeepCleanParsers.TryParseProgressPercent(bar, out double p);
            percents.Add(p);
        }

        var runner = new ProgressRunner(percents, DismAnalyzeOutput);
        var reported = new List<double>();

        // Progress<T> ohne SynchronizationContext meldet über den ThreadPool – deshalb hier
        // direkt gegen die Schnittstelle prüfen, statt auf den Rückruf zu warten.
        var direct = new DirectProgress<DeepCleanProgress>(p => reported.Add(p.Percent));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, direct, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Ready, analysis.Status);
        Assert.Single(runner.ProgressCalls);
        Assert.Contains("AnalyzeComponentStore", runner.ProgressCalls[0]);

        Assert.Equal(26, reported.Count);
        Assert.Equal(100.0, reported[^1], 3);
        for (int i = 1; i < reported.Count; i++)
            Assert.True(reported[i] >= reported[i - 1], $"Rückschritt bei {reported[i - 1]} -> {reported[i]}");
    }

    /// <summary>Meldet synchron im aufrufenden Thread – in Tests will man das Ergebnis sofort sehen.</summary>
    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public DirectProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    /// <summary>
    /// Bereiche OHNE bezifferbaren Fortschritt müssen schweigen. Eine Zahl zu melden, nur damit
    /// der Balken sich bewegt, wäre erfunden – und der Aufrufer könnte „unbestimmt" nicht mehr
    /// von „gemessen" unterscheiden.
    /// </summary>
    [Theory]
    [InlineData(DeepCleanAreaKind.WindowsOld)]
    [InlineData(DeepCleanAreaKind.ShadowCopies)]
    [InlineData(DeepCleanAreaKind.UpdateCache)]
    [InlineData(DeepCleanAreaKind.DeliveryOptimization)]
    public async Task Analyse_BereicheOhneMessbarenFortschritt_MeldenNichts(DeepCleanAreaKind kind)
    {
        var env = new FakeEnvironment { RestorePointCount = 3 };
        env.SetFolder(@"C:\Windows.old", 1_000);
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 1_000);
        env.SetFolder(@"C:\Windows\SoftwareDistribution\DeliveryOptimization", 1_000);

        var reported = new List<DeepCleanProgress>();
        var progress = new DirectProgress<DeepCleanProgress>(reported.Add);

        await Create(new FakeProcessRunner(), env).AnalyzeAsync(kind, progress, CancellationToken.None);

        Assert.Empty(reported);
    }

    // ===================================================================================
    //  Fortschritt: Messphasen der Bereinigung
    // ===================================================================================

    /// <summary>
    /// Prozess-Attrappe für den dreistufigen Ablauf der Komponentenspeicher-Bereinigung:
    /// Messen → Bereinigen (mit Balken) → Messen.
    /// </summary>
    private sealed class PhaseRunner : IProcessRunner
    {
        private readonly Queue<string> _analyzeOutputs;
        private readonly IReadOnlyList<double> _cleanupBars;
        private readonly int _cleanupExitCode;

        public PhaseRunner(Queue<string> analyzeOutputs, IReadOnlyList<double> cleanupBars, int cleanupExitCode = 0)
        {
            _analyzeOutputs = analyzeOutputs;
            _cleanupBars = cleanupBars;
            _cleanupExitCode = cleanupExitCode;
        }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
            => Handle(arguments, null);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            Action<double>? onProgress, CancellationToken ct)
            => Handle(arguments, onProgress);

        private Task<ProcessResult> Handle(IReadOnlyList<string> arguments, Action<double>? onProgress)
        {
            if (arguments.Any(a => a.Contains("StartComponentCleanup", StringComparison.OrdinalIgnoreCase)))
            {
                if (onProgress is not null)
                    foreach (double bar in _cleanupBars) onProgress(bar);

                return Task.FromResult(new ProcessResult(_cleanupExitCode, "", ""));
            }

            string output = _analyzeOutputs.Count > 0 ? _analyzeOutputs.Dequeue() : "";
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
    }

    private static Queue<string> BeforeAndAfterAnalyze()
        => new(new[]
        {
            DismAnalyzeOutput,
            DismAnalyzeOutput.Replace("Actual Size of Component Store : 8.12 GB",
                                      "Actual Size of Component Store : 6.12 GB")
        });

    /// <summary>
    /// Der Kern des Fehlerberichts: Die Bereinigung des Komponentenspeichers ist dreistufig, und
    /// die beiden Messläufe sind eigene DISM-Aufrufe von je 21–33 s (nachgemessen). Sie MÜSSEN
    /// angesagt werden – sonst behauptet die Anzeige nach dem 100%-Balken „fertig" und misst
    /// stumm weiter.
    /// </summary>
    [Fact]
    public async Task ComponentStore_Bereinigen_SagtBeideMessphasenAn()
    {
        var runner = new PhaseRunner(BeforeAndAfterAnalyze(), new[] { 10.0, 50.0, 100.0 });
        var reported = new List<DeepCleanProgress>();

        var outcome = await Create(runner, new FakeEnvironment()).CleanAsync(
            DeepCleanAreaKind.ComponentStore, new DirectProgress<DeepCleanProgress>(reported.Add),
            CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Success, outcome.Status);

        var phases = reported.Select(p => p.Phase).ToList();
        Assert.Equal(DeepCleanPhase.MeasuringBefore, phases[0]);
        Assert.Equal(new[] { DeepCleanPhase.Working, DeepCleanPhase.Working, DeepCleanPhase.Working },
            phases.Skip(1).Take(3));

        // Und die Zusicherung, um die es geht: Die LETZTE Meldung des Bereichs ist die
        // Nachmessung – nicht der 100%-Balken der Bereinigung.
        Assert.Equal(DeepCleanPhase.MeasuringAfter, phases[^1]);
    }

    /// <summary>
    /// Scheitert die Bereinigung, wird nicht nachgemessen – dann darf die Anzeige auch keine
    /// Messphase behaupten.
    /// </summary>
    [Fact]
    public async Task ComponentStore_BereinigungFehlgeschlagen_MeldetKeineNachmessung()
    {
        var runner = new PhaseRunner(BeforeAndAfterAnalyze(), new[] { 10.0 }, cleanupExitCode: 1726);
        var reported = new List<DeepCleanProgress>();

        var outcome = await Create(runner, new FakeEnvironment()).CleanAsync(
            DeepCleanAreaKind.ComponentStore, new DirectProgress<DeepCleanProgress>(reported.Add),
            CancellationToken.None);

        Assert.Equal(DeepCleanOutcomeStatus.Failed, outcome.Status);
        Assert.DoesNotContain(DeepCleanPhase.MeasuringAfter, reported.Select(p => p.Phase));
    }

    /// <summary>
    /// Die Analyse ist EINSTUFIG – ein einziger DISM-Lauf, der durchgehend Fortschritt meldet.
    /// Dort gibt es keine stumme Messphase, und es darf auch keine angesagt werden.
    /// </summary>
    [Fact]
    public async Task ComponentStore_Analyse_HatKeineMessphase()
    {
        var runner = new PhaseRunner(new Queue<string>(new[] { DismAnalyzeOutput }), Array.Empty<double>());
        var reported = new List<DeepCleanProgress>();

        await Create(runner, new FakeEnvironment()).AnalyzeAsync(
            DeepCleanAreaKind.ComponentStore, new DirectProgress<DeepCleanProgress>(reported.Add),
            CancellationToken.None);

        Assert.All(reported, p => Assert.Equal(DeepCleanPhase.Working, p.Phase));
    }

    // ===================================================================================
    //  Fortschritt: Anzeigeregel des ViewModels
    // ===================================================================================

    /// <summary>
    /// Der gemeldete Befund, als Test: Nach dem letzten 100%-Balken MUSS die Anzeige wieder
    /// unbestimmt werden und die Restzeit verschwinden. Vorher stand der Balken bestimmt auf
    /// 100 %, während die Nachmessung noch eine halbe Minute lief.
    /// </summary>
    [Fact]
    public void ViewModel_NachDemLetztenBalken_WirdWiederUnbestimmtUndOhneRestzeit()
    {
        var vm = new OpenClean.ViewModels.DeepCleanViewModel();

        // Bereinigung läuft, Balken bei 100 %, Restzeit vorhanden.
        vm.ApplyProgress(new DeepCleanProgress(
            100, new DeepCleanEta(DeepCleanEtaKind.Value, TimeSpan.FromSeconds(5))), 0, 1);

        Assert.False(vm.ProgressIsIndeterminate);
        Assert.NotEqual("", vm.EtaText);
        Assert.Equal(100, vm.ProgressPercent, 3);

        // Jetzt die Nachmessung.
        vm.ApplyProgress(new DeepCleanProgress(100, DeepCleanEta.None, DeepCleanPhase.MeasuringAfter), 0, 1);

        Assert.True(vm.ProgressIsIndeterminate);
        Assert.Equal("", vm.EtaText);
        Assert.Equal(OpenClean.Services.Localization.Loc.T("deepclean.progress.measuringAfter"), vm.ProgressText);
    }

    /// <summary>
    /// Der Balken darf dabei NICHT zurücklaufen: Jede Phase bringt ihren eigenen Zähler mit, die
    /// Nachmessung meldet ihren Sockel statt der 100 % davor.
    /// </summary>
    [Fact]
    public void ViewModel_Messphase_LaesstDenBalkenNichtZuruecklaufen()
    {
        var vm = new OpenClean.ViewModels.DeepCleanViewModel();

        vm.ApplyProgress(new DeepCleanProgress(100, DeepCleanEta.None), 0, 2);
        double afterWorking = vm.ProgressPercent;
        Assert.Equal(50, afterWorking, 3);   // Bereich 1 von 2 ist voll

        // Die Nachmessung meldet 0 % – der Balken muss trotzdem stehen bleiben.
        vm.ApplyProgress(new DeepCleanProgress(0, DeepCleanEta.None, DeepCleanPhase.MeasuringAfter), 0, 2);

        Assert.Equal(afterWorking, vm.ProgressPercent, 3);
        Assert.True(vm.ProgressIsIndeterminate);
    }

    /// <summary>
    /// Die Messung VOR der Bereinigung hat dasselbe Problem in Grün: Auch dort wartete der Nutzer
    /// blind, während darunter „Bereinige …" stand. Sie bekommt deshalb ihren eigenen Text.
    /// </summary>
    [Fact]
    public void ViewModel_MessungVorDerBereinigung_ZeigtEigenenTextStattBereinige()
    {
        var vm = new OpenClean.ViewModels.DeepCleanViewModel();

        vm.ApplyProgress(new DeepCleanProgress(0, DeepCleanEta.None, DeepCleanPhase.MeasuringBefore), 0, 1);

        Assert.True(vm.ProgressIsIndeterminate);
        Assert.Equal("", vm.EtaText);
        Assert.Equal(OpenClean.Services.Localization.Loc.T("deepclean.progress.measuringBefore"), vm.ProgressText);
        Assert.NotEqual(OpenClean.Services.Localization.Loc.T("deepclean.progress.measuringAfter"), vm.ProgressText);
    }

    // ===================================================================================
    //  „Belegt" gegen „freigebbar"
    // ===================================================================================

    /// <summary>
    /// Der Fall aus der Nutzerbeobachtung: genau ein Wiederherstellungspunkt, der jüngste bleibt
    /// stehen -> freigebbar 0 B. vssadmin meldet daneben aber Gigabytes an BELEGTEM Speicher.
    /// Beides muss getrennt ankommen; nur „0 B" zu zeigen, liest sich als „da liegt nichts".
    /// </summary>
    [Fact]
    public async Task Schattenkopien_NurEinPunkt_MeldetBelegtenPlatzTrotzNullFreigebbar()
    {
        var runner = new FakeProcessRunner();
        runner.When("vssadmin.exe", "shadowstorage", new ProcessResult(0, ShadowStorageOutput, ""));
        var env = new FakeEnvironment { RestorePointCount = 1 };

        var analysis = await Create(runner, env).AnalyzeAsync(DeepCleanAreaKind.ShadowCopies, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Empty, analysis.Status);
        Assert.Equal(0, analysis.SizeBytes);                                  // freigebbar
        Assert.Equal(10L * 1024 * 1024 * 1024, analysis.OccupiedBytes);       // belegt
    }

    /// <summary>
    /// Dieselbe Verwechslung beim Komponentenspeicher: 22,13 GB liegen dort, freigebbar sind nur
    /// Backups + Cache. Ein aufgeräumter Speicher zeigte sonst „0 B" und wirkte defekt.
    /// </summary>
    [Fact]
    public async Task ComponentStore_MeldetBelegtenSpeicherNebenDemFreigebbaren()
    {
        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "AnalyzeComponentStore", new ProcessResult(0, DismAnalyzeOutput, ""));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        long reclaimable = (long)Math.Round(1.50 * 1024 * 1024 * 1024) + 512L * 1024 * 1024;
        Assert.Equal(reclaimable, analysis.SizeBytes);
        Assert.Equal((long)Math.Round(8.12 * 1024 * 1024 * 1024), analysis.OccupiedBytes);
        Assert.True(analysis.OccupiedBytes > analysis.SizeBytes);
    }

    /// <summary>
    /// Ist NICHTS freigebbar, muss der belegte Platz erst recht ankommen – genau dann steht in
    /// der Spalte sonst die irreführende 0.
    /// </summary>
    [Fact]
    public async Task ComponentStore_NichtsFreigebbar_MeldetTrotzdemDenBelegtenSpeicher()
    {
        string output = DismAnalyzeOutput
            .Replace("Backups and Disabled Features : 1.50 GB", "Backups and Disabled Features : 0 B")
            .Replace("Cache and Temporary Data : 512 MB", "Cache and Temporary Data : 0 B");

        var runner = new FakeProcessRunner();
        runner.When("dism.exe", "AnalyzeComponentStore", new ProcessResult(0, output, ""));

        var analysis = await Create(runner, new FakeEnvironment())
            .AnalyzeAsync(DeepCleanAreaKind.ComponentStore, CancellationToken.None);

        Assert.Equal(DeepCleanStatus.Empty, analysis.Status);
        Assert.Equal(0, analysis.SizeBytes);
        Assert.Equal((long)Math.Round(8.12 * 1024 * 1024 * 1024), analysis.OccupiedBytes);
    }

    /// <summary>
    /// Bei den Ordner-Bereichen SIND belegt und freigebbar dieselbe Zahl – dort gibt es keine
    /// zweite Auskunft, und es darf auch keine erfunden werden.
    /// </summary>
    [Fact]
    public async Task Ordnerbereich_BelegtIstNichtBekannt_UndWirdNichtErfunden()
    {
        var env = new FakeEnvironment();
        env.SetFolder(@"C:\Windows\SoftwareDistribution\Download", 3_500_000_000);

        var analysis = await Create(new FakeProcessRunner(), env)
            .AnalyzeAsync(DeepCleanAreaKind.UpdateCache, CancellationToken.None);

        Assert.Equal(3_500_000_000, analysis.SizeBytes);
        Assert.Equal(0, analysis.OccupiedBytes);   // 0 = "keine Auskunft", nicht "nichts belegt"
    }

    [Fact]
    public void Bereiche_DeckenAlleFuenfAbUndHabenEigeneSchluessel()
    {
        Assert.Equal(5, DeepCleanAreas.All.Count);
        Assert.Equal(Enum.GetValues<DeepCleanAreaKind>().Length, DeepCleanAreas.All.Distinct().Count());

        var keys = DeepCleanAreas.All
            .SelectMany(k => new[]
            {
                DeepCleanAreas.NameKey(k), DeepCleanAreas.DescriptionKey(k), DeepCleanAreas.ConfirmKey(k)
            })
            .ToList();

        // Jeder Bereich braucht eigene Schlüssel; ein Duplikat hieße, zwei Bereiche zeigten
        // denselben Text – bei "Windows.old löschen" vs. "Update-Cache leeren" fatal.
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("deepclean.", k));
    }
}
