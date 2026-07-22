namespace OpenClean.Services.UI;

/// <summary>
/// Abstraktion über sämtliche modalen UI-Dialoge und Systemdialoge, die ViewModels benötigen.
/// Kapselt WPF-Abhängigkeiten (<c>MessageBox</c>, die In-App-Dialoge <c>ConfirmDialog</c>/
/// <c>ActivationDialog</c>, <c>OpenFolderDialog</c> sowie das Owner-Fensterhandle), damit die
/// ViewModels ohne laufende <c>Application</c> testbar bleiben.
///
/// <para>Bewusst getrennte Methoden für die native <c>MessageBox</c>-Optik
/// (<see cref="ShowError"/>/<see cref="ShowWarning"/>/<see cref="ShowInfo"/>/<see cref="AskYesNo"/>)
/// und die gethemten In-App-Dialoge (<see cref="ConfirmThemed"/>/<see cref="ActivateLicense"/>),
/// damit bestehende Aufrufstellen 1:1 – also optikerhaltend – umgestellt werden können.</para>
/// </summary>
public interface IDialogService
{
    /// <summary>Native Fehler-MessageBox (OK, Warn-Symbol) – Ersatz für den zentralen Command-Fehlerdialog.</summary>
    void ShowError(string message);

    /// <summary>Native Warn-MessageBox (OK).</summary>
    void ShowWarning(string message, string? title = null);

    /// <summary>Native Info-MessageBox (OK).</summary>
    void ShowInfo(string message, string? title = null);

    /// <summary>Native Ja/Nein-Rückfrage. True bei „Ja“.</summary>
    bool AskYesNo(string message, string title);

    /// <summary>
    /// Gethemter In-App-Bestätigungsdialog (<c>ConfirmDialog</c>). True, wenn der Nutzer bestätigt.
    /// Titel und Bestätigungstext optional überschreibbar.
    /// </summary>
    bool ConfirmThemed(string message, string? title = null, string? confirmText = null);

    /// <summary>
    /// Zeigt den Lizenz-Aktivierungsdialog (<c>ActivationDialog</c>) modal an.
    /// True, wenn eine Lizenz aktiviert wurde.
    /// </summary>
    bool ActivateLicense();

    /// <summary>
    /// Öffnet den Ordner-Auswahldialog. Liefert die gewählten Ordnerpfade (leer bei Abbruch).
    /// </summary>
    IReadOnlyList<string> PickFolders(string? title = null, bool multiselect = true);

    /// <summary>
    /// Fensterhandle (HWND) des Hauptfensters als Eigentümer für owner-modale Shell-Dialoge
    /// (z. B. der Papierkorb-Nuke-Warnungsdialog von <c>SHFileOperation</c>).
    /// <see cref="IntPtr.Zero"/>, wenn kein Fenster verfügbar ist.
    /// </summary>
    IntPtr OwnerHandle { get; }
}
