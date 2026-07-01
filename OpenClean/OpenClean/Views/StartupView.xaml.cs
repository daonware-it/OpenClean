using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace OpenClean.Views;

/// <summary>
/// Interaction logic for StartupView.xaml
/// </summary>
public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
    }
}

/// <summary>
/// Wandelt einen Enum-Wert für Segment-/RadioButtons in bool um.
/// IsChecked = (Wert == Parameter). Beim Auswählen wird der Parameter-Wert zurückgegeben.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Nur beim Anwählen (true) den zugehörigen Enum-Wert setzen; sonst Binding nicht ändern.
        if (value is bool b && b && parameter is not null)
        {
            try
            {
                return Enum.Parse(targetType, parameter.ToString()!);
            }
            catch
            {
                return Binding.DoNothing;
            }
        }

        return Binding.DoNothing;
    }
}
