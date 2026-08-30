using System.Threading;
using System.Windows.Input;

namespace ClashSharp.Installer.Presentation;

/// <summary>Runs one asynchronous command invocation at a time.</summary>
public sealed class AsyncDelegateCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action _onUnhandledFailure;
    private int _executing;

    /// <summary>Initializes a single-flight asynchronous command.</summary>
    public AsyncDelegateCommand(
        Func<Task> execute,
        Func<bool>? canExecute,
        Action onUnhandledFailure)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(onUnhandledFailure);
        _execute = execute;
        _canExecute = canExecute;
        _onUnhandledFailure = onUnhandledFailure;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _executing) == 0 && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter) => await ExecuteAsync();

    /// <summary>Executes the command and exposes completion for deterministic hosts and tests.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(parameter: null)
            || Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            return;
        }

        NotifyCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception exception)
            when (InstallerPresentationExceptionPolicy.IsRecoverable(exception))
        {
            // The callback deliberately receives no exception object, keeping raw errors out of the UI.
            _onUnhandledFailure();
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>Notifies command sources that availability has changed.</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
