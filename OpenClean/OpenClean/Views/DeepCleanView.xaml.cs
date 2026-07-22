using System.Windows.Controls;

namespace OpenClean.Views;

/// <summary>
/// Ansicht der Tiefen-System-Bereinigung. Reines Layout – die gesamte Logik liegt in
/// <see cref="ViewModels.DeepCleanViewModel"/>.
/// </summary>
public partial class DeepCleanView : UserControl
{
    public DeepCleanView()
    {
        InitializeComponent();
    }
}
