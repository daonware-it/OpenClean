namespace OpenClean.Services.Localization;

/// <summary>
/// Statische Kurzhilfe für die Lokalisierung aus C#-Code (ViewModels, Services, Models).
/// Delegiert an <see cref="LocalizationManager.Instance"/>.
/// </summary>
public static class Loc
{
    /// <summary>Übersetzung für <paramref name="key"/> mit optionalen <c>{0}</c>-Platzhaltern.</summary>
    public static string T(string key, params object[] args)
        => LocalizationManager.Instance.Get(key, args);

    /// <summary>Wird nach jedem Sprachwechsel ausgelöst (zum Neuberechnen von Texten).</summary>
    public static event System.EventHandler? LanguageChanged
    {
        add => LocalizationManager.Instance.LanguageChanged += value;
        remove => LocalizationManager.Instance.LanguageChanged -= value;
    }
}
