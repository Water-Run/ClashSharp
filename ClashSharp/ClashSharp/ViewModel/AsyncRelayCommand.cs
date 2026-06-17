/*
 * Async Relay Command
 * Provides shared asynchronous command routing for MVVM view models
 *
 * @author: WaterRun
 * @file: ViewModel/AsyncRelayCommand.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ClashSharp.ViewModel;

/// <summary>Command implementation that executes one asynchronous operation at a time.</summary>
/// <remarks>
/// Invariants: At most one execution is active per command instance.
/// Thread safety: Not thread-safe; intended for UI-thread command invocation.
/// Side effects: Executes the supplied asynchronous delegate and raises command availability notifications.
/// </remarks>
internal sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    /// <summary>Asynchronous execution callback invoked by <see cref="ExecuteAsync"/>.</summary>
    private readonly Func<object?, CancellationToken, Task> _executeAsync;

    /// <summary>Optional availability predicate invoked by <see cref="CanExecute"/>.</summary>
    private readonly Func<bool>? _canExecute;

    /// <summary>Tracks whether an asynchronous operation is currently running.</summary>
    private bool _isRunning;

    /// <summary>Initializes an asynchronous relay command.</summary>
    /// <param name="executeAsync">Asynchronous execution callback. Must not be null.</param>
    /// <param name="canExecute">Optional availability predicate; null means only running state controls availability.</param>
    /// <exception cref="ArgumentNullException"><paramref name="executeAsync"/> is null.</exception>
    public AsyncRelayCommand(Func<CancellationToken, Task> executeAsync, Func<bool>? canExecute = null)
        : this((_, cancellationToken) => executeAsync(cancellationToken), canExecute)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
    }

    /// <summary>Initializes an asynchronous relay command with command-parameter support.</summary>
    /// <param name="executeAsync">Asynchronous execution callback that receives the command parameter. Must not be null.</param>
    /// <param name="canExecute">Optional availability predicate; null means only running state controls availability.</param>
    /// <exception cref="ArgumentNullException"><paramref name="executeAsync"/> is null.</exception>
    public AsyncRelayCommand(Func<object?, CancellationToken, Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    /// <summary>Occurs when command availability may have changed.</summary>
    /// <remarks>
    /// The event fires synchronously on the caller's thread before and after asynchronous execution.
    /// Reentrancy is possible when subscribers query command state from the handler.
    /// Subscribers should unsubscribe when their lifetime is shorter than the command lifetime.
    /// </remarks>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Gets whether the command is currently executing.</summary>
    /// <value>True while an asynchronous operation is active; false by default and after completion.</value>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Determines whether the command can execute.</summary>
    /// <param name="parameter">Command parameter; ignored and may be null.</param>
    /// <returns>True when no execution is running and the optional predicate allows execution; otherwise false.</returns>
    public bool CanExecute(object? parameter)
    {
        return !IsRunning && (_canExecute?.Invoke() ?? true);
    }

    /// <summary>Starts command execution without awaiting completion.</summary>
    /// <param name="parameter">Command parameter; ignored and may be null.</param>
    public void Execute(object? parameter)
    {
        _ = ExecuteAsync(parameter);
    }

    /// <summary>Executes the asynchronous command callback when the command is available.</summary>
    /// <param name="parameter">Command parameter; ignored and may be null.</param>
    /// <returns>A task that completes after the callback completes or immediately when execution is blocked.</returns>
    /// <remarks>
    /// Cancellation semantics: Uses <see cref="CancellationToken.None"/> because WinUI command invocation does not provide a token.
    /// Thread / reentrancy: Reentrant calls complete immediately while an execution is active.
    /// </remarks>
    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            IsRunning = true;
            await _executeAsync(parameter, CancellationToken.None);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Raises <see cref="CanExecuteChanged"/> to refresh command availability in bound controls.</summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
