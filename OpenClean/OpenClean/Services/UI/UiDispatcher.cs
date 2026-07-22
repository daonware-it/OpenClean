using System.Windows;
using System.Windows.Threading;

namespace OpenClean.Services.UI;

/// <summary>
/// Produktive <see cref="IUiDispatcher"/>-Implementierung über den WPF-<see cref="Dispatcher"/>
/// der Anwendung.
/// </summary>
public sealed class UiDispatcher : IUiDispatcher
{
    /// <summary>Gemeinsam genutzte Standardinstanz (Produktivbetrieb).</summary>
    public static IUiDispatcher Default { get; set; } = new UiDispatcher();

    private static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    // Ohne laufende Application (Shutdown/Tests) gilt „bereits auf dem UI-Thread“, damit der
    // Aufrufer inline ausführt statt zu marshallen – identisch zum vorherigen Direktverhalten.
    public bool CheckAccess() => Dispatcher?.CheckAccess() ?? true;

    public void Post(Action action) => Dispatcher?.BeginInvoke(action);

    public Task InvokeAsync(Action action) => Dispatcher?.InvokeAsync(action).Task ?? Task.CompletedTask;
}
