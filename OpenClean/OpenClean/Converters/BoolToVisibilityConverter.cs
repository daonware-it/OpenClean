using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenClean.Converters;

/// <summary>
/// True -> Visible, False -> Collapsed. Set Invert=true (via parameter "invert") to reverse.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            null => false,
            _ => true
        };
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible;
    }
}
