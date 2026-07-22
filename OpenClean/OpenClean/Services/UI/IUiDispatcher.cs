namespace OpenClean.Services.UI;

/// <summary>
/// Abstraktion über das Marshalling auf den UI-Thread. Ersetzt direkte Zugriffe auf
/// <c>Application.Current.Dispatcher</c> in ViewModels, damit Fortschritts-/Callback-Code
/// ohne laufende WPF-<c>Application</c> testbar bleibt.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True, wenn der aktuelle Thread bereits der UI-Thread ist.</summary>
    bool CheckAccess();

    /// <summary>Reiht die Aktion asynchron auf dem UI-Thread ein (feuern und vergessen).</summary>
    void Post(Action action);

    /// <summary>Führt die Aktion auf dem UI-Thread aus und wartet auf ihren Abschluss.</summary>
    Task InvokeAsync(Action action);
}
