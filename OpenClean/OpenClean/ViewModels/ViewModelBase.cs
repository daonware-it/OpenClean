using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OpenClean.Services.UI;

namespace OpenClean.ViewModels;

/// <summary>
/// Minimale MVVM-Basis: INotifyPropertyChanged ohne externe Abhängigkeiten.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// Gemeinsame Oberfläche der Relay-Commands: erlaubt es, eine Menge von Commands generisch
/// neu bewerten zu lassen (z. B. wenn sich der Busy-Zustand ändert), ohne den konkreten Typ zu kennen.
/// </summary>
public interface IRelayCommand : ICommand
{
    void RaiseCanExecuteChanged();
}

/// <summary>
/// Einfacher ICommand, synchron. Für asynchrone Aktionen mit <see cref="AsyncRelayCommand"/>.
/// </summary>
public sealed class RelayCommand : ICommand, IRelayCommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// ICommand für async Handler; blockiert Mehrfach-Ausführung während eine Aktion läuft.
/// </summary>
public sealed class AsyncRelayCommand : ICommand, IRelayCommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
        => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            // async void: eine unbehandelte Ausnahme würde die App beenden. Der Fehlerdialog
            // läuft über den zentralen Dialog-Service (kapselt MessageBox, testbar ersetzbar).
            DialogService.Default.ShowError(Loc.T("error.unexpected", ex.Message));
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
