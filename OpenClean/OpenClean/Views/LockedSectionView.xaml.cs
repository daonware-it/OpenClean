using System.Windows.Controls;

namespace OpenClean.Views;

/// <summary>
/// Platzhalter für den Zeitplan-Bereich, solange Premium nicht freigeschaltet ist
/// (DataContext: <c>LockedScheduleViewModel</c>).
/// </summary>
public partial class LockedSectionView : UserControl
{
    public LockedSectionView()
    {
        InitializeComponent();
    }
}
