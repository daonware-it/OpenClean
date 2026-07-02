using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace OpenClean.Services.Localization;

/// <summary>
/// XAML-Markup-Erweiterung für lokalisierte Texte: <c>{loc:Loc nav.overview}</c>.
///
/// Liefert eine <see cref="Binding"/> auf den Indexer von
/// <see cref="LocalizationManager.Instance"/> (<c>[key]</c>). Dadurch aktualisiert sich
/// der Text bei einem Sprachwechsel automatisch, ohne Neustart – der Manager löst beim
/// Wechsel <c>PropertyChanged("Item[]")</c> aus.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    /// <summary>Der Ressourcen-Schlüssel (z. B. "nav.overview").</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
