namespace OpenClean.Services.DeepClean;

/// <summary>Die fünf Bereiche der Tiefen-System-Bereinigung.</summary>
public enum DeepCleanAreaKind
{
    /// <summary>Alte Windows-Installation (<c>C:\Windows.old</c>) nach einem Feature-Update.</summary>
    WindowsOld,

    /// <summary>Komponentenspeicher (WinSxS) – Superseded-Pakete via DISM.</summary>
    ComponentStore,

    /// <summary>Schattenkopien / Wiederherstellungspunkte (VSS).</summary>
    ShadowCopies,

    /// <summary>Zwischengespeicherte Windows-Update-Pakete (<c>SoftwareDistribution\Download</c>).</summary>
    UpdateCache,

    /// <summary>Cache der Übermittlungsoptimierung (Delivery Optimization).</summary>
    DeliveryOptimization
}

/// <summary>
/// Statische Beschreibung der Bereiche: Reihenfolge und i18n-Schlüssel. Bewusst getrennt von
/// <see cref="DeepCleanService"/> – die Anzeige braucht die Metadaten, ohne etwas zu analysieren.
/// </summary>
public static class DeepCleanAreas
{
    /// <summary>Alle Bereiche in Anzeige-Reihenfolge (harmlos → tiefgreifend).</summary>
    public static IReadOnlyList<DeepCleanAreaKind> All { get; } = new[]
    {
        DeepCleanAreaKind.UpdateCache,
        DeepCleanAreaKind.DeliveryOptimization,
        DeepCleanAreaKind.ComponentStore,
        DeepCleanAreaKind.ShadowCopies,
        DeepCleanAreaKind.WindowsOld
    };

    /// <summary>i18n-Schlüssel des Bereichsnamens.</summary>
    public static string NameKey(DeepCleanAreaKind kind) => "deepclean.area." + Slug(kind) + ".name";

    /// <summary>i18n-Schlüssel der Kurzbeschreibung.</summary>
    public static string DescriptionKey(DeepCleanAreaKind kind) => "deepclean.area." + Slug(kind) + ".description";

    /// <summary>i18n-Schlüssel des Warnhinweises im Bestätigungsdialog.</summary>
    public static string ConfirmKey(DeepCleanAreaKind kind) => "deepclean.area." + Slug(kind) + ".confirm";

    private static string Slug(DeepCleanAreaKind kind) => kind switch
    {
        DeepCleanAreaKind.WindowsOld => "windowsOld",
        DeepCleanAreaKind.ComponentStore => "componentStore",
        DeepCleanAreaKind.ShadowCopies => "shadowCopies",
        DeepCleanAreaKind.UpdateCache => "updateCache",
        DeepCleanAreaKind.DeliveryOptimization => "deliveryOptimization",
        _ => "unknown"
    };
}
