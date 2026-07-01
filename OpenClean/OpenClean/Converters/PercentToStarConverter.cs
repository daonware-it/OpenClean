using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenClean.Converters;

/// <summary>
/// Wandelt einen Prozentwert (0–100, double) in eine sternbasierte
/// <see cref="GridLength"/> um – für Auslastungsbalken mit zwei Spalten
/// (belegt / frei). Werte werden auf 0–100 begrenzt.
/// </summary>
public sealed class PercentToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => 0
        };
        if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0;
        pct = Math.Clamp(pct, 0, 100);

        // Parameter "inv" => Restanteil (100 - pct), z. B. für die "frei"-Spalte eines Balkens.
        if (parameter is string s && s.Equals("inv", StringComparison.OrdinalIgnoreCase))
            pct = 100 - pct;

        return new GridLength(pct, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
