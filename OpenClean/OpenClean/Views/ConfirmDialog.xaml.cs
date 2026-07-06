using System.Windows;
using System.Windows.Input;

namespace OpenClean.Views;

/// <summary>
/// Moderner, themefähiger In-App-Bestätigungsdialog (ersetzt die Standard-MessageBox
/// vor dem Bereinigen). Folgt dem aktiven Theme über DynamicResource-Brushes.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string message, string? title, string? confirmText)
    {
        InitializeComponent();
        MessageText.Text = message;

        if (!string.IsNullOrEmpty(title))
        {
            TitleText.Text = title;
            Title = title;
        }

        if (!string.IsNullOrEmpty(confirmText))
            ConfirmButton.Content = confirmText;
    }

    /// <summary>
    /// Zeigt den Dialog modal an und gibt true zurück, wenn der Nutzer „Bereinigen“ wählt.
    /// Optional lassen sich Titel und Bestätigungstext überschreiben.
    /// </summary>
    public static bool Show(Window? owner, string message, string? title = null, string? confirmText = null)
    {
        var dialog = new ConfirmDialog(message, title, confirmText);
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
