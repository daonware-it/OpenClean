using System.Globalization;
using System.Windows.Data;
using OpenClean.Services;

namespace OpenClean.Converters;

/// <summary>Formatiert eine Byte-Zahl (long) für die Anzeige, z. B. „12,4 GB".</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is long bytes ? ByteFormatter.Format(bytes) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
