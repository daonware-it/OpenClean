using System.Windows.Input;

namespace OpenClean.ViewModels;

/// <summary>
/// Basis für ViewModels mit einem laufenden, abbrechbaren Hintergrundvorgang (Scan/Bereinigung).
/// Bündelt das bislang in jedem Scan-ViewModel copy-paste-te Gerüst an einer Stelle:
/// <list type="bullet">
/// <item>den <see cref="IsBusy"/>-Zustand samt automatischer Neubewertung der abhängigen Commands,</item>
/// <item>den Lebenszyklus der <see cref="CancellationTokenSource"/> und</item>
/// <item>das <see cref="CancelCommand"/> (aktiv nur, solange etwas läuft).</item>
/// </list>
/// Abgeleitete ViewModels registrieren ihre busy-abhängigen Commands über
/// <see cref="RegisterBusyCommands"/> und starten ihre Arbeit über <see cref="RunCancellableAsync"/>.
/// </summary>
public abstract class ScanViewModelBase : ViewModelBase
{
    private readonly List<IRelayCommand> _busyCommands = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;

    protected ScanViewModelBase()
    {
        var cancel = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        CancelCommand = cancel;
        _busyCommands.Add(cancel);
    }

    /// <summary>True, während ein Hintergrundvorgang läuft. Steuert die Command-Verfügbarkeit.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnBusyChanged();
            foreach (var command in _busyCommands)
                command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Bricht den laufenden Vorgang ab. Nur ausführbar, solange <see cref="IsBusy"/> true ist.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>
    /// Registriert Commands, deren <c>CanExecute</c> vom <see cref="IsBusy"/>-Zustand abhängt und
    /// die deshalb bei jeder Zustandsänderung neu bewertet werden müssen. Das <see cref="CancelCommand"/>
    /// ist bereits registriert.
    /// </summary>
    protected void RegisterBusyCommands(params IRelayCommand?[] commands)
    {
        foreach (var command in commands)
            if (command is not null)
                _busyCommands.Add(command);
    }

    /// <summary>Hook nach jeder Änderung von <see cref="IsBusy"/> (Standard: nichts). Für abgeleitete Zusatzlogik.</summary>
    protected virtual void OnBusyChanged() { }

    /// <summary>
    /// Führt eine abbrechbare Aktion mit einheitlichem Busy-/CTS-Lebenszyklus aus: setzt
    /// <see cref="IsBusy"/>, erzeugt ein frisches Abbruch-Token, fängt einen Nutzer-Abbruch
    /// (<see cref="OperationCanceledException"/>) ab und räumt am Ende garantiert auf. Re-entrant-sicher
    /// (kehrt sofort zurück, wenn bereits ein Vorgang läuft). <paramref name="onCancelled"/> erlaubt
    /// eine bereichsspezifische Reaktion auf den Abbruch (z. B. eine Statusmeldung).
    /// </summary>
    protected async Task RunCancellableAsync(Func<CancellationToken, Task> action, Action? onCancelled = null)
    {
        if (IsBusy) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Vom Nutzer abgebrochen – kein Fehlerfall.
            onCancelled?.Invoke();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }
}
