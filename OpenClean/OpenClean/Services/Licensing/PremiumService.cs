using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using OpenClean.Contracts;
using OpenClean.Services.Integrity;

namespace OpenClean.Services.Licensing;

/// <summary>
/// Orchestriert Lizenz + Premium-Modul: lädt bei gültiger Lizenz die
/// <c>OpenClean.Premium.dll</c> aus dem Datenverzeichnis (Unterordner <c>Modules\</c>)
/// und stellt die Premium-Bereiche für die Navigation bereit.
///
/// Das Laden erfolgt in den Default-<see cref="AssemblyLoadContext"/> (nicht isoliert),
/// damit die WPF-Ressourcen (BAML, pack-URIs) des Moduls und dessen Verweis auf die
/// App-Assembly im Single-File-Publish korrekt aufgelöst werden. Jeder Fehler beim
/// Laden führt still zum Free-Zustand – die App funktioniert immer auch ohne Modul.
/// </summary>
public sealed class PremiumService
{
    /// <summary>Prozessweiter Singleton.</summary>
    public static PremiumService Instance { get; } = new();

    private IPremiumModule? _module;
    private bool _loadAttempted;

    private PremiumService()
    {
        LicenseService.Instance.Changed += (_, _) =>
        {
            // Nach Aktivierung/Entfernen neu bewerten (Modul ggf. frisch laden).
            _loadAttempted = false;
            EnsureModuleLoaded();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Wird ausgelöst, wenn sich Lizenz- oder Modulzustand geändert hat.</summary>
    public event EventHandler? Changed;

    /// <summary>Verzeichnis, in dem das Premium-Modul liegt.</summary>
    public static string ModuleDirectory => Path.Combine(AppPaths.DataDirectory, "Modules");

    /// <summary>Vollständiger Pfad der Premium-DLL.</summary>
    public static string ModulePath => Path.Combine(ModuleDirectory, PremiumContract.ModuleFileName);

    /// <summary>True, wenn eine gültige Premium-Lizenz vorliegt.</summary>
    public bool IsPremium => LicenseService.Instance.State == LicenseState.Valid;

    /// <summary>True, wenn die Lizenz das Feature enthält (unabhängig vom Modul).</summary>
    public bool HasFeature(string feature) => LicenseService.Instance.HasFeature(feature);

    /// <summary>Das geladene Premium-Modul; null im Free-Zustand oder bei Ladefehler.</summary>
    public IPremiumModule? Module
    {
        get
        {
            EnsureModuleLoaded();
            return _module;
        }
    }

    /// <summary>Liefert den Premium-Bereich mit der Kennung (z. B. "Zeitplan") oder null.</summary>
    public IPremiumSection? GetSection(string sectionId)
        => Module?.Sections.FirstOrDefault(s =>
            string.Equals(s.SectionId, sectionId, StringComparison.Ordinal));

    /// <summary>
    /// Kompletter Aktivierungsablauf: Schlüssel beim Server aktivieren, Premium-Modul
    /// herunterladen, Token lokal speichern (löst <see cref="Changed"/> aus, wodurch das
    /// Modul geladen wird). Liefert <see cref="LicenseApiError.None"/> bei Erfolg.
    /// </summary>
    public async Task<LicenseApiError> ActivateAsync(string licenseKey)
    {
        var api = new LicenseApiClient();
        var result = await api.ActivateAsync(licenseKey);
        if (!result.Success)
            return result.Error == LicenseApiError.None ? LicenseApiError.Network : result.Error;
        if (result.PayloadBase64 is null || result.SignatureBase64 is null)
            return LicenseApiError.Network;

        // Modul VOR dem Token speichern: das Changed-Ereignis des Tokens lädt das Modul
        // sofort (inkl. Hash-Prüfung gegen den signierten Token-Inhalt).
        if (!string.IsNullOrEmpty(result.DownloadToken))
        {
            byte[]? bytes = await api.DownloadModuleAsync(result.DownloadToken);
            if (bytes is { Length: > 0 })
                TrySaveModule(bytes);
        }

        // Schlägt nur fehl, wenn Signatur oder Geräte-Hash nicht passen.
        return LicenseService.Instance.TrySaveToken(licenseKey, result.PayloadBase64, result.SignatureBase64)
            ? LicenseApiError.None
            : LicenseApiError.InvalidKey;
    }

    /// <summary>
    /// Erneuert Token (und lädt bei Bedarf das Modul nach) über den Validate-Endpunkt.
    /// Für „Modul fehlt/beschädigt"-Reparatur und den Hintergrund-Refresh.
    /// </summary>
    public async Task<bool> TryFetchModuleAsync()
    {
        string? key = LicenseService.Instance.LicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var api = new LicenseApiClient();
        var result = await api.ValidateAsync(key);
        if (!result.Success || result.PayloadBase64 is null || result.SignatureBase64 is null)
            return false;

        if (!string.IsNullOrEmpty(result.DownloadToken))
        {
            byte[]? bytes = await api.DownloadModuleAsync(result.DownloadToken);
            if (bytes is { Length: > 0 })
                TrySaveModule(bytes);
        }

        LicenseService.Instance.TrySaveToken(key, result.PayloadBase64, result.SignatureBase64);
        return System.IO.File.Exists(ModulePath);
    }

    /// <summary>
    /// Erneuert das Token im Hintergrund, wenn es älter als eine Woche ist. Wird beim
    /// Öffnen des Premium-Bereichs aufgerufen – nie blockierend.
    /// (Privacy-Prinzip: die App kontaktiert daonware.de nur, wenn eine Lizenz existiert.)
    /// </summary>
    public void RefreshInBackground()
    {
        if (!LicenseService.Instance.ShouldRefresh) return;
        if (string.IsNullOrWhiteSpace(LicenseService.Instance.LicenseKey)) return;

        _ = Task.Run(async () =>
        {
            try { await EnforceLicenseOnlineAsync(); }
            catch { /* Hintergrund-Refresh darf nie stören. */ }
        });
    }

    /// <summary>
    /// Startet die Online-Lizenzprüfung im Hintergrund (nicht blockierend). Sicher schon
    /// beim App-Start aufrufbar: OHNE vorhandene Lizenz passiert NICHTS – es wird also
    /// kein Netzwerkzugriff für Free-Nutzer ausgelöst (Privacy-Prinzip bleibt gewahrt).
    /// </summary>
    public void EnforceLicenseInBackground()
    {
        if (string.IsNullOrWhiteSpace(LicenseService.Instance.LicenseKey)) return;

        _ = Task.Run(async () =>
        {
            try { await EnforceLicenseOnlineAsync(); }
            catch { /* Der Start darf nie an der Lizenzprüfung hängen. */ }
        });
    }

    /// <summary>
    /// Prüft die vorhandene Lizenz gegen den Server und setzt das Ergebnis durch:
    /// <list type="bullet">
    ///   <item>Server bestätigt → Token wird erneuert (frische 30-Tage-Offline-Frist),
    ///   fehlendes/veraltetes Modul wird nachgeladen.</item>
    ///   <item>Server LEHNT den Schlüssel ab (gelöscht/widerrufen → <see cref="LicenseApiError.InvalidKey"/>)
    ///   → Lizenz wird lokal ENTFERNT, Premium sofort gesperrt.</item>
    ///   <item>Nur Netzwerkfehler (offline, Server nicht erreichbar) → Lizenz bleibt
    ///   unverändert; die 30-Tage-Offline-Frist entscheidet dann lokal.</item>
    /// </list>
    /// Liefert true, wenn ein Server-Kontakt zustande kam (bestätigt ODER abgelehnt);
    /// false bei reinem Netzwerkfehler oder ohne Lizenz.
    /// </summary>
    public async Task<bool> EnforceLicenseOnlineAsync()
    {
        string? key = LicenseService.Instance.LicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var api = new LicenseApiClient();
        var result = await api.ValidateAsync(key);

        if (result.Success && result.PayloadBase64 is not null && result.SignatureBase64 is not null)
        {
            // Modul NUR nachladen, wenn es lokal fehlt – sonst würde jeder Start das
            // Modul unnötig neu herunterladen. Ein vorhandenes, aber inkompatibles Modul
            // (Hash-Mismatch nach App-Update) repariert der Nutzer über den Locked-Bereich.
            if (!File.Exists(ModulePath) && !string.IsNullOrEmpty(result.DownloadToken))
            {
                byte[]? bytes = await api.DownloadModuleAsync(result.DownloadToken);
                if (bytes is { Length: > 0 })
                    TrySaveModule(bytes);
            }

            LicenseService.Instance.TrySaveToken(key, result.PayloadBase64, result.SignatureBase64);
            return true;
        }

        // Eindeutige Ablehnung durch den Server = Schlüssel existiert nicht mehr / widerrufen.
        // Nur DANN lokal entfernen – ein transienter Netzwerkfehler darf NIE die Lizenz löschen.
        if (result.Error == LicenseApiError.InvalidKey)
        {
            LicenseService.Instance.RemoveLicense();
            return true;
        }

        return false; // Network/DeviceLimit → Offline-Zustand belassen.
    }

    /// <summary>
    /// Gibt dieses Gerät frei: meldet den Schlüssel beim Server ab (der Geräte-Slot wird
    /// wieder frei) und entfernt danach Token und Modul lokal. Ein reiner Netzwerkfehler
    /// entfernt NICHTS – ob trotzdem nur lokal entfernt wird, entscheidet das UI
    /// (<see cref="RemoveLocally"/>). Liefert <see cref="LicenseApiError.None"/> bei Erfolg.
    /// </summary>
    public async Task<LicenseApiError> DeactivateAsync()
    {
        string? key = LicenseService.Instance.LicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return LicenseApiError.None; // nichts zu tun

        var result = await new LicenseApiClient().DeactivateAsync(key);

        // Erfolg ODER "Schlüssel unbekannt/widerrufen" (serverseitig ohnehin weg)
        // -> lokal entfernen; beides ist aus Nutzersicht eine erfolgreiche Freigabe.
        if (result.Success || result.Error == LicenseApiError.InvalidKey)
        {
            RemoveLocally();
            return LicenseApiError.None;
        }

        return result.Error == LicenseApiError.None ? LicenseApiError.Network : result.Error;
    }

    /// <summary>
    /// Entfernt Lizenz-Token und Modul-DLL NUR lokal (ohne Server-Kontakt) – der Geräte-Slot
    /// bleibt beim Server belegt. Fallback, wenn der Lizenzserver nicht erreichbar ist.
    /// Das bereits geladene Assembly bleibt im Prozess (Default-ALC kann nicht entladen),
    /// wird aber durch <see cref="IsPremium"/>/<see cref="HasFeature"/> sofort wirkungslos.
    /// </summary>
    public void RemoveLocally()
    {
        try
        {
            if (File.Exists(ModulePath)) File.Delete(ModulePath);
        }
        catch
        {
            // DLL evtl. geladen/gesperrt -> der Lizenzentzug wirkt trotzdem.
        }

        LicenseService.Instance.RemoveLicense(); // löst Changed aus (auch hier)
    }

    /// <summary>Speichert heruntergeladene Modul-Bytes an den festen Modulpfad.</summary>
    public static bool TrySaveModule(byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(ModuleDirectory);
            File.WriteAllBytes(ModulePath, bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- Laden ------------------------------------------------------------------

    private void EnsureModuleLoaded()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;

        // Ein einmal geladenes Assembly kann nicht entladen werden (Default-ALC);
        // bei entfernter Lizenz wird das Modul daher nur logisch deaktiviert.
        if (_module is not null) return;

        var license = LicenseService.Instance.License;
        if (license is null) return;

        _module = TryLoad(license, LicenseService.Instance.ModuleSha256);
    }

    private static IPremiumModule? TryLoad(LicenseInfo license, string? expectedSha256)
    {
        try
        {
            // Sperre bei erkannter Manipulation (OPCL-20). Steht bewusst VOR dem Dev-Override:
            // In einem manipulierten Prozess darf überhaupt kein nachgeladener Code mehr in den
            // Default-ALC gelangen – dieser läuft mit Adminrechten und lässt sich nicht entladen.
            if (IntegrityState.IsBlocked) return null;

#if DEBUG
            // Dev-Override (nur Debug-Builds): eine neben der EXE liegende Modul-DLL wird
            // OHNE Hash-Prüfung bevorzugt geladen. Das Premium-Repo kopiert sie per
            // Post-Build dorthin – sonst ließe sich ein lokaler Modul-Build mit echter
            // Lizenz nie testen (Token-Hash lehnt ihn ab, der Hintergrund-Refresh
            // überschreibt ihn wieder mit dem Server-Modul).
            string devPath = Path.Combine(AppContext.BaseDirectory, PremiumContract.ModuleFileName);
            if (File.Exists(devPath))
                return LoadFrom(devPath, license);
#endif
            if (!File.Exists(ModulePath)) return null;

            // Zwei unabhängige Prüfungen, BEIDE müssen bestehen (OPCL-20). Die DLL liegt in
            // einem beschreibbaren Ordner und wird mit Adminrechten in den Prozess geladen –
            // hier ist der einzige Weg, auf dem OpenClean fremden Code nachlädt.
            //
            // 1. Authenticode: Ist die DLL gültig von DaonWare signiert? Deckt auch den Fall ab,
            //    dass das Token gar keinen Hash mitbringt (früher entfiel die Prüfung dann ganz).
            // 2. SHA-256 gegen den Hash aus dem signierten Lizenz-Token: bindet die DLL an genau
            //    die Fassung, die der Lizenzserver für diese Lizenz vorgesehen hat.
            //
            // Die abgelehnte DLL wird bewusst NICHT gelöscht: Das Verweigern des Ladens ist der
            // gesamte Schutz. Ein Löschen brächte nichts dazu und würde eine Schleife erzeugen
            // (Server liefert dieselbe Datei erneut -> wieder gelöscht), außerdem zerstörte es
            // eine Datei, die der Nutzer nicht wiederherstellen kann.
            if (!IntegrityGuard.IsTrustedModule(ModulePath) || !HasExpectedHash(ModulePath, expectedSha256))
                return null;

            return LoadFrom(ModulePath, license);
        }
        catch
        {
            // Beschädigte/inkompatible DLL -> Free-Zustand, nie ein Crash.
            return null;
        }
    }

    /// <summary>
    /// True, wenn die Datei den im Lizenz-Token hinterlegten SHA-256 hat. Ein fehlender
    /// Soll-Hash zählt NICHT mehr als bestanden: Ohne Hash trägt allein die Signatur, und die
    /// wurde direkt davor geprüft – ein Token ohne Hash darf die Bindung nicht aufheben.
    /// </summary>
    private static bool HasExpectedHash(string path, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return true;

        using var stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lädt das Modul-Assembly, prüft die Contract-Version und initialisiert es.</summary>
    private static IPremiumModule? LoadFrom(string path, LicenseInfo license)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

        var moduleType = assembly.GetTypes().FirstOrDefault(t =>
            typeof(IPremiumModule).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass);
        if (moduleType is null) return null;

        if (Activator.CreateInstance(moduleType) is not IPremiumModule module) return null;
        if (module.ContractVersion != PremiumContract.Version) return null;

        module.Initialize(new Host(license));
        return module;
    }

    private sealed class Host : IPremiumHost
    {
        public Host(LicenseInfo license) => License = license;
        public LicenseInfo License { get; }
        public string DataDirectory => AppPaths.DataDirectory;
    }
}
