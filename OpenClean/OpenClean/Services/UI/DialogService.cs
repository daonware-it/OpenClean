using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using OpenClean.Views;

namespace OpenClean.Services.UI;

/// <summary>
/// Produktive <see cref="IDialogService"/>-Implementierung auf WPF-Basis. Einzige Stelle, an der
/// die ViewModel-Schicht (indirekt) <c>MessageBox</c>, die In-App-Dialoge und <c>OpenFolderDialog</c>
/// berührt. Das Eigentümerfenster wird stets aus <see cref="Application.Current"/> aufgelöst.
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <summary>
    /// Gemeinsam genutzte Standardinstanz. ViewModels injizieren im Test eine Attrappe,
    /// nutzen im Produktivbetrieb aber diesen Default (kein DI-Container im Projekt).
    /// </summary>
    public static IDialogService Default { get; set; } = new DialogService();

    private static Window? Owner => Application.Current?.MainWindow;

    public IntPtr OwnerHandle
        => Owner is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    public void ShowError(string message)
        => MessageBox.Show(message, "OpenClean", MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowWarning(string message, string? title = null)
        => MessageBox.Show(message, title ?? "OpenClean", MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowInfo(string message, string? title = null)
        => MessageBox.Show(message, title ?? "OpenClean", MessageBoxButton.OK, MessageBoxImage.Information);

    public bool AskYesNo(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ConfirmThemed(string message, string? title = null, string? confirmText = null)
        => ConfirmDialog.Show(Owner, message, title, confirmText);

    public bool ActivateLicense()
        => ActivationDialog.Show(Owner);

    public IReadOnlyList<string> PickFolders(string? title = null, bool multiselect = true)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = multiselect,
            Title = title ?? string.Empty
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderNames : Array.Empty<string>();
    }
}
