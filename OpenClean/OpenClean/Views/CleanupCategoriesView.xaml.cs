using System.Windows.Controls;

namespace OpenClean.Views;

/// <summary>
/// Der Kategorien-Teil der Bereinigung (Ergebnis-Banner, Auswahl-Karte, Kategorie-Liste).
/// Aus <see cref="CleanerView"/> herausgelöst, damit dort nur noch Kopfzeile, Tab-Leiste
/// und Statusleiste stehen. DataContext ist das CleanerViewModel.
/// </summary>
public partial class CleanupCategoriesView : UserControl
{
    public CleanupCategoriesView() => InitializeComponent();
}
