#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.ViewModel;

/// <summary>Command implementation that tracks one asynchronous operation at a time.</summary>
/// <remarks>
/// Explicit execution propagates failures to its caller. ICommand execution records unexpected
/// failures and reports them through the injected application error sink.
/// </remarks>
internal sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<object?, CancellationToken, Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly IApplicationErrorSink _errorSink;
    private readonly string _operationName;

    private int _executionState;
    private Task? _executionTask;
    private Exception? _lastError;

    /// <summary>Initializes an asynchronous relay command.</summary>
    public AsyncRelayCommand(
        Func<CancellationToken, Task> executeAsync,
        Func<bool>? canExecute = null,
        IApplicationErrorSink? errorSink = null,
        string? operationName = null)
        : this(
            (_, cancellationToken) => executeAsync(cancellationToken),
            canExecute,
            errorSink,
            operationName)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
    }

    /// <summary>Initializes an asynchronous relay command with command-parameter support.</summary>
    public AsyncRelayCommand(
        Func<object?, CancellationToken, Task> executeAsync,
        Func<bool>? canExecute = null,
        IApplicationErrorSink? errorSink = null,
        string? operationName = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _errorSink = errorSink ?? NullApplicationErrorSink.Instance;
        _operationName = string.IsNullOrWhiteSpace(operationName)
            ? "async-command"
            : operationName;
    }

    /// <summary>Occurs when command availability may have changed.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Gets whether an asynchronous operation is active.</summary>
    public bool IsRunning => Volatile.Read(ref _executionState) != 0;

    /// <summary>Gets whether an asynchronous operation is active.</summary>
    public bool IsBusy => IsRunning;

    /// <summary>Gets the task created by the most recent ICommand invocation.</summary>
    public Task? ExecutionTask
    {
        get => _executionTask;
        private set => SetProperty(ref _executionTask, value);
    }

    /// <summary>Gets the most recent unexpected ICommand failure.</summary>
    public Exception? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        return !IsRunning && (_canExecute?.Invoke() ?? true);
    }

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (!TryBeginExecution())
        {
            return;
        }

        LastError = null;
        ExecutionTask = ExecuteObservedAsync(parameter);
    }

    /// <summary>Executes explicitly without caller cancellation.</summary>
    public Task ExecuteAsync(object? parameter)
    {
        return ExecuteAsync(parameter, CancellationToken.None);
    }

    /// <summary>Executes explicitly and preserves callback failure and cancellation for the caller.</summary>
    public Task ExecuteAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (!TryBeginExecution())
        {
            return Task.CompletedTask;
        }

        return ExecuteStartedAsync(parameter, cancellationToken);
    }

    private async Task ExecuteStartedAsync(object? parameter, CancellationToken cancellationToken)
    {
        try
        {
            await _executeAsync(parameter, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _executionState, 0);
            NotifyExecutionStateChanged();
        }
    }

    /// <summary>Raises the command availability event.</summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteObservedAsync(object? parameter)
    {
        try
        {
            await ExecuteStartedAsync(parameter, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LastError = exception;
            try
            {
                await _errorSink.ReportAsync(
                    new ApplicationError(_operationName, exception),
                    CancellationToken.None);
            }
            catch (Exception sinkException)
            {
                LastError = new AggregateException(exception, sinkException);
            }
        }
    }

    private bool TryBeginExecution()
    {
        if (_canExecute is not null && !_canExecute())
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _executionState, 1, 0) != 0)
        {
            return false;
        }

        NotifyExecutionStateChanged();
        return true;
    }

    private void NotifyExecutionStateChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsBusy));
        NotifyCanExecuteChanged();
    }
}
