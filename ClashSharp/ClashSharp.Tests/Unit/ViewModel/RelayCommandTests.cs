/*
 * Relay Command Tests
 * Verifies shared synchronous command behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/RelayCommandTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the shared synchronous relay command.</summary>
public sealed class RelayCommandTests
{
    /// <summary>Verifies command execution delegates to the supplied action.</summary>
    [Fact]
    public void Execute_WhenCanExecuteIsTrue_InvokesAction()
    {
        int executionCount = 0;
        RelayCommand command = new(() => executionCount++);

        command.Execute(null);

        Assert.Equal(1, executionCount);
    }

    /// <summary>Verifies command executability follows the supplied predicate.</summary>
    [Fact]
    public void CanExecute_UsesPredicate()
    {
        bool canExecute = false;
        RelayCommand command = new(() => { }, () => canExecute);

        Assert.False(command.CanExecute(null));

        canExecute = true;

        Assert.True(command.CanExecute(null));
    }

    /// <summary>Verifies explicit notifications raise <see cref="System.Windows.Input.ICommand.CanExecuteChanged"/>.</summary>
    [Fact]
    public void NotifyCanExecuteChanged_RaisesCanExecuteChanged()
    {
        RelayCommand command = new(() => { });
        int notificationCount = 0;
        command.CanExecuteChanged += (_, _) => notificationCount++;

        command.NotifyCanExecuteChanged();

        Assert.Equal(1, notificationCount);
    }
}
