/*
 * Relay Command
 * Provides shared synchronous command routing for MVVM view models
 *
 * @author: WaterRun
 * @file: ViewModel/RelayCommand.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Windows.Input;

namespace ClashSharp.ViewModel;

/// <summary>Command implementation that delegates execution and availability to supplied callbacks.</summary>
/// <remarks>
/// Invariants: The execute callback is non-null for the lifetime of the command.
/// Thread safety: Not thread-safe; intended for UI-thread command invocation.
/// Side effects: Executes the supplied delegate synchronously when <see cref="Execute"/> is called.
/// </remarks>
internal sealed class RelayCommand : ICommand
{
    /// <summary>Execution callback invoked by <see cref="Execute"/>.</summary>
    private readonly Action _execute;

    /// <summary>Optional availability predicate invoked by <see cref="CanExecute"/>.</summary>
    private readonly Func<bool>? _canExecute;

    /// <summary>Initializes a synchronous relay command.</summary>
    /// <param name="execute">Execution callback. Must not be null.</param>
    /// <param name="canExecute">Optional availability predicate; null means the command is always executable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is null.</exception>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>Occurs when command availability may have changed.</summary>
    /// <remarks>
    /// The event fires synchronously on the caller's thread when <see cref="NotifyCanExecuteChanged"/> is called.
    /// Reentrancy is possible when subscribers query or execute the command from the handler.
    /// Subscribers should unsubscribe when their lifetime is shorter than the command lifetime.
    /// </remarks>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Determines whether the command can execute.</summary>
    /// <param name="parameter">Command parameter; ignored and may be null.</param>
    /// <returns>True when the command can execute; otherwise false.</returns>
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke() ?? true;
    }

    /// <summary>Executes the command callback.</summary>
    /// <param name="parameter">Command parameter; ignored and may be null.</param>
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _execute();
    }

    /// <summary>Raises <see cref="CanExecuteChanged"/> to refresh command availability in bound controls.</summary>
    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
