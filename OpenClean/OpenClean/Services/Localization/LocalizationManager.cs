using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace OpenClean.Services.Localization;

/// <summary>Eine auswählbare Sprache (Code + muttersprachlicher Anzeigename).</summary>
public sealed record LanguageOption(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

/// <summary>
/// Zentraler Lokalisierungs-Dienst. Lädt die Texte der aktiven Sprache aus einer
/// eingebetteten JSON-Sprachdatei (<c>Resources/Strings/&lt;code&gt;.json</c>) und
/// hält Englisch zusätzlich als Rückfall für fehlende Schlüssel bereit.
///
/// Analog zu <see cref="ThemeService"/>: die Sprache ist zur Laufzeit umschaltbar.
/// XAML bindet über die <c>{loc:Loc key}</c>-Markup-Erweiterung an den Indexer; beim
/// Sprachwechsel wird <c>PropertyChanged("Item[]")</c> ausgelöst, sodass alle Bindings
/// live neu ausgewertet werden. C#-berechnete Texte reagieren über <see cref="LanguageChanged"/>.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    // Rückfallsprache, wenn weder eine gespeicherte Auswahl noch eine unterstützte
    // Systemsprache vorliegt. Dient zugleich als Rückfall für fehlende Schlüssel.
    private const string FallbackCode = "en";

    /// <summary>Unterstützte Sprachen in Anzeige-Reihenfolge.</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("de", "Deutsch"),
        new LanguageOption("en", "English"),
        new LanguageOption("es", "Español"),
        new LanguageOption("fr", "Français"),
        new LanguageOption("pt", "Português"),
        new LanguageOption("ru", "Русский"),
        new LanguageOption("pl", "Polski")
    };

    /// <summary>Der prozessweite Singleton (auch aus XAML per {loc:Loc} genutzt).</summary>
    public static LocalizationManager Instance { get; } = new();

    private Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fallback;
    private string _currentCode = FallbackCode;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Wird nach jedem Sprachwechsel ausgelöst (für C#-berechnete Texte).</summary>
    public event EventHandler? LanguageChanged;

    private LocalizationManager()
    {
        _fallback = Load(FallbackCode);
        _current = _fallback;
    }

    /// <summary>Alle auswählbaren Sprachen (für die UI-Auswahl).</summary>
    public IReadOnlyList<LanguageOption> AvailableLanguages => Languages;

    /// <summary>Der Code der aktiven Sprache (z. B. "de", "en").</summary>
    public string CurrentLanguage
    {
        get => _currentCode;
        set => ChangeLanguage(value);
    }

    /// <summary>Die aktive Sprache als <see cref="LanguageOption"/> (für ComboBox-SelectedItem).</summary>
    public LanguageOption CurrentLanguageOption
    {
        get => Languages.FirstOrDefault(l => l.Code == _currentCode) ?? Languages[0];
        set { if (value is not null) ChangeLanguage(value.Code); }
    }

    /// <summary>Übersetzung für <paramref name="key"/> (aktive Sprache, sonst Deutsch, sonst der Schlüssel selbst).</summary>
    public string this[string key]
    {
        get
        {
            if (key is null) return string.Empty;
            if (_current.TryGetValue(key, out var value)) return value;
            if (_fallback.TryGetValue(key, out var fb)) return fb;
            return key;
        }
    }

    /// <summary>Übersetzung mit <see cref="string.Format(string, object[])"/>-Platzhaltern (<c>{0}</c> …).</summary>
    public string Get(string key, params object[] args)
    {
        string template = this[key];
        if (args is null || args.Length == 0) return template;
        try { return string.Format(CultureInfo.CurrentCulture, template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>
    /// Setzt die zu verwendende Sprache beim Start: gespeicherte Auswahl → sonst
    /// Windows-Anzeigesprache (falls unterstützt) → sonst Deutsch. Wird in
    /// <c>App.OnStartup</c> aufgerufen, bevor das Hauptfenster gerendert wird.
    /// </summary>
    public void InitializeStartupLanguage()
        => ChangeLanguage(DetectStartupLanguage());

    /// <summary>Wechselt die aktive Sprache und aktualisiert alle Bindings/berechneten Texte.</summary>
    public void ChangeLanguage(string? code)
    {
        code = Normalize(code);
        _current = code == FallbackCode ? _fallback : Load(code);
        _currentCode = code;
        SaveLanguage(code);

        // Alle XAML-Indexer-Bindings neu auswerten.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguageOption)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Laden / Persistenz -------------------------------------------------

    /// <summary>Lädt eine eingebettete Sprachdatei; bei Fehler eine leere Zuordnung.</summary>
    private static Dictionary<string, string> Load(string code)
    {
        string resource = $"OpenClean.Resources.Strings.{code}.json";
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using Stream? stream = asm.GetManifestResourceStream(resource);
            if (stream is null) return new(StringComparer.Ordinal);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return data is null
                ? new(StringComparer.Ordinal)
                : new Dictionary<string, string>(data, StringComparer.Ordinal);
        }
        catch
        {
            // Beschädigte/fehlende Ressource -> leer (führt zum Deutsch-/Schlüssel-Rückfall).
            return new(StringComparer.Ordinal);
        }
    }

    private static string DetectStartupLanguage()
    {
        string? saved = ReadSavedLanguage();
        if (saved is not null) return saved;

        // Windows-Anzeigesprache auf zweistelligen Code abbilden.
        string twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == twoLetter) ? twoLetter : FallbackCode;
    }

    private static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return FallbackCode;
        code = code.Trim().ToLowerInvariant();
        return Languages.Any(l => l.Code == code) ? code : FallbackCode;
    }

    private static string? ReadSavedLanguage()
    {
        string? saved = SettingsService.Instance.Current.Language;
        return saved is not null && Languages.Any(l => l.Code == saved) ? saved : null;
    }

    private static void SaveLanguage(string code)
    {
        SettingsService.Instance.Current.Language = code;
        SettingsService.Instance.Save();
    }
}
