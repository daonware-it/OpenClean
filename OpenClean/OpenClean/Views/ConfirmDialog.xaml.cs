using System.Windows;
using System.Windows.Input;

namespace OpenClean.Views;

/// <summary>
/// Moderner, themefähiger In-App-Bestätigungsdialog (ersetzt die Standard-MessageBox
/// vor dem Bereinigen). Folgt dem aktiven Theme über DynamicResource-Brushes.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    /// <summary>
    /// Zeigt den Dialog modal an und gibt true zurück, wenn der Nutzer „Bereinigen“ wählt.
    /// </summary>
    public static bool Show(Window? owner, string message)
    {
        var dialog = new ConfirmDialog(message);
        if (owner is not null && owner != dialog)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
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
