using System.Windows;
using System.Windows.Input;
using GxbrielZapret.Startup;

namespace GxbrielZapret.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?>? _execute;
    private readonly Func<object?, Task>? _executeAsync;
    private readonly Func<object?, bool>? _canExecute;
    private EventHandler? _internalCanExecuteChanged;
    private int _isExecuting;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
        : this(_ => executeAsync(), canExecute is null ? null : _ => canExecute()) { }

    public bool IsExecuting => Volatile.Read(ref _isExecuting) != 0;

    public bool CanExecute(object? parameter) => !IsExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter) || Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;
        RaiseCanExecuteChanged();
        try
        {
            if (_executeAsync is not null)
                await _executeAsync(parameter).ConfigureAwait(true);
            else
                _execute?.Invoke(parameter);
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
            RaiseCanExecuteChanged();
        }
    }

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            try
            {
                MessageBox.Show(ex.Message, "GXBRIEL_ZAPRET — ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _internalCanExecuteChanged += value;
            try { CommandManager.RequerySuggested += value; } catch { }
        }
        remove
        {
            _internalCanExecuteChanged -= value;
            try { CommandManager.RequerySuggested -= value; } catch { }
        }
    }

    public void RaiseCanExecuteChanged()
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            try
            {
                d.BeginInvoke(new Action(RaiseCanExecuteChanged));
                return;
            }
            catch { }
        }

        try { _internalCanExecuteChanged?.Invoke(this, EventArgs.Empty); } catch { }
        try { CommandManager.InvalidateRequerySuggested(); } catch { }
    }
}

