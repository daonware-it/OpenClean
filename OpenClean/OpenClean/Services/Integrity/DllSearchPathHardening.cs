using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenClean.Services.Integrity;

/// <summary>
/// Härtet den nativen DLL-Suchpfad des Prozesses (OPCL-20).
///
/// Windows durchsucht beim Laden einer DLL standardmäßig auch das Verzeichnis der EXE und das
/// aktuelle Arbeitsverzeichnis. Eine dort abgelegte Datei mit einem Namen, den Windows ohnehin
/// sucht (<c>version.dll</c>, <c>dwmapi.dll</c>, …), wird dann anstelle der echten Systemdatei
/// geladen – klassisches DLL-Hijacking. Für OpenClean ist das besonders heikel: Die portable
/// Fassung läuft aus beschreibbaren Orten (USB-Stick, Downloads-Ordner) und mit Adminrechten.
///
/// Hier wird die Suche deshalb auf System32 beschränkt. EXE-Verzeichnis, Arbeitsverzeichnis und
/// PATH fallen heraus.
///
/// WICHTIG – warum <see cref="ModuleInitializerAttribute"/> und nicht App.OnStartup:
/// Ein Modulinitialisierer läuft, bevor irgendein Code dieser Assembly ausgeführt wird, also vor
/// dem generierten WPF-Einstiegspunkt. OnStartup käme zu spät – da hat WPF bereits die ersten
/// nativen Bibliotheken geladen.
///
/// GRENZE DES SCHUTZES: Der .NET-Host (hostfxr, hostpolicy, coreclr) lädt seine eigenen nativen
/// Bibliotheken, BEVOR verwalteter Code überhaupt anläuft. Diese Ladevorgänge kann kein
/// verwalteter Code mehr absichern.
/// </summary>
internal static class DllSearchPathHardening
{
    // Nur System32 und explizit hinzugefügte Verzeichnisse durchsuchen. Das EXE-Verzeichnis
    // (LOAD_LIBRARY_SEARCH_APPLICATION_DIR, 0x200) fehlt hier absichtlich – genau darum geht es.
    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    /// <summary>
    /// Läuft automatisch beim Laden der Assembly – vor <c>Main</c>. Darf unter keinen Umständen
    /// eine Ausnahme auslösen: Eine Ausnahme hier würde als TypeInitializationException den
    /// Prozessstart verhindern. Und sie darf nichts aus der App benutzen (Loc, SettingsService,
    /// AppPaths …), weil davon zu diesem Zeitpunkt noch nichts initialisiert ist.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            // Notausgang ohne Neu-Build: Sollte die Härtung in freier Wildbahn eine
            // Fremdkomponente ausbremsen, lässt sich das per Umgebungsvariable abschalten.
            string? mode = Environment.GetEnvironmentVariable("OPENCLEAN_DLL_HARDENING");
            if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)) return;

            if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS))
            {
                // Sollte ab Windows 8 nie passieren. Dann wenigstens das Arbeitsverzeichnis aus
                // der herkömmlichen Suchreihenfolge nehmen – besser als gar nichts.
                SetDllDirectory("");
                return;
            }

            // Ab hier gilt die neue Suchreihenfolge: EXE-Verzeichnis, Arbeitsverzeichnis und PATH
            // sind bereits ausgeschlossen. SetDllDirectory wird deshalb NICHT mehr aufgerufen –
            // beide Mechanismen zu mischen empfiehlt Microsoft ausdrücklich nicht.

            // Abgeschwächter Modus: EXE-Verzeichnis wieder ladbar machen (Arbeitsverzeichnis und
            // PATH bleiben draußen). Rückfallebene, falls eine native Abhängigkeit doch neben der
            // EXE erwartet wird.
            if (string.Equals(mode, "soft", StringComparison.OrdinalIgnoreCase))
                AddDllDirectory(AppContext.BaseDirectory);
        }
        catch (Exception)
        {
            // Der Prozessstart darf hieran niemals scheitern.
        }
    }
}
