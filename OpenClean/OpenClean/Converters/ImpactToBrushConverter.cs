using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OpenClean.Models;

namespace OpenClean.Converters;

/// <summary>
/// Maps a <see cref="StartupImpact"/> to a pill brush. Pass parameter "bg" for the
/// translucent background brush, otherwise returns the solid foreground/accent brush.
/// </summary>
public sealed class ImpactToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush High = new(Color.FromRgb(0xF2, 0x6D, 0x5B));
    private static readonly SolidColorBrush Medium = new(Color.FromRgb(0xF0, 0xB4, 0x29));
    private static readonly SolidColorBrush Low = new(Color.FromRgb(0x2E, 0xCC, 0x9B));
    private static readonly SolidColorBrush Unknown = new(Color.FromRgb(0x9A, 0x9A, 0xA8));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        SolidColorBrush baseBrush = value switch
        {
            StartupImpact.High => High,
            StartupImpact.Medium => Medium,
            StartupImpact.Low => Low,
            _ => Unknown
        };

        bool wantBackground = parameter is string s && s.Equals("bg", StringComparison.OrdinalIgnoreCase);
        if (!wantBackground)
            return baseBrush;

        var c = baseBrush.Color;
        var bg = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        bg.Freeze();
        return bg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
