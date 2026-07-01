using System;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;

namespace OpenClean.Views;

/// <summary>
/// Interaction logic for CleanerView.xaml
/// </summary>
public partial class CleanerView : UserControl
{
    public CleanerView()
    {
        InitializeComponent();
    }
}

/// <summary>
/// Liefert aus einem vollständigen Pfad nur den Datei-/Ordnernamen (letztes Segment)
/// für die fett dargestellte erste Zeile einer Vorschau-Zeile. Fällt bei Sonderfällen
/// (z. B. „Papierkorb (alle Laufwerke)“) auf den Originaltext zurück.
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string ?? "";
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
