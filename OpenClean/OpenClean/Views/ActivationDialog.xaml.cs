using System.Windows;
using System.Windows.Input;
using OpenClean.ViewModels;

namespace OpenClean.Views;

/// <summary>
/// Modaler Dialog zur Lizenz-Aktivierung (Premium). Schließt sich bei Erfolg selbst;
/// der Aufrufer prüft danach den Lizenzzustand über <c>PremiumService</c>.
/// </summary>
public partial class ActivationDialog : Window
{
    private readonly LicenseViewModel _viewModel = new();

    private ActivationDialog()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Succeeded += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
    }

    /// <summary>Zeigt den Dialog modal an; true, wenn eine Lizenz aktiviert wurde.</summary>
    public static bool Show(Window? owner)
    {
        var dialog = new ActivationDialog();
        if (owner is not null && owner != dialog)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}
