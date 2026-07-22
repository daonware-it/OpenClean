using System.Windows;
using System.Windows.Controls;
using OpenClean.ViewModels;

namespace OpenClean.Views;

/// <summary>
/// Interaction logic for NetworkView.xaml
///
/// Die Live-Abfrage läuft ausschließlich, solange die Ansicht tatsächlich sichtbar ist:
/// beim Wechsel in einen anderen Bereich wird der Zwei-Sekunden-Takt gestoppt, damit im
/// Hintergrund keine Last entsteht.
/// </summary>
public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();

        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += (_, _) => (DataContext as NetworkViewModel)?.Stop();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is not NetworkViewModel vm) return;

        if (IsVisible) vm.Start();
        else vm.Stop();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Der alte Kontext darf nicht weiterlaufen, wenn die Ansicht neu bestückt wird.
        (e.OldValue as NetworkViewModel)?.Stop();

        if (IsVisible && e.NewValue is NetworkViewModel vm) vm.Start();
    }
}
