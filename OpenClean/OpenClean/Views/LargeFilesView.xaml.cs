using System.Windows.Controls;

namespace OpenClean.Views;

/// <summary>
/// Der Große-Dateien-Tab der Bereinigung. DataContext ist das LargeFilesViewModel
/// (gesetzt von CleanerView).
/// </summary>
public partial class LargeFilesView : UserControl
{
    public LargeFilesView() => InitializeComponent();
}
