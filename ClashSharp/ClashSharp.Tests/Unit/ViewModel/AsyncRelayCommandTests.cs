/*
 * Async Relay Command Tests
 * Verifies shared asynchronous command behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/AsyncRelayCommandTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the shared asynchronous relay command.</summary>
public sealed class AsyncRelayCommandTests
{
    /// <summary>Verifies asynchronous execution invokes the supplied delegate.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenCanExecuteIsTrue_InvokesDelegate()
    {
        int executionCount = 0;
        AsyncRelayCommand command = new(async cancellationToken =>
        {
            await Task.Yield();
            Assert.False(cancellationToken.IsCancellationRequested);
            executionCount++;
        });

        await command.ExecuteAsync(null);

        Assert.Equal(1, executionCount);
    }

    /// <summary>Verifies reentrant execution is blocked while an operation is running.</summary>
    [Fact]
    public async Task ExecuteAsync_WhileRunning_BlocksReentrantExecution()
    {
        TaskCompletionSource started = new();
        TaskCompletionSource release = new();
        int executionCount = 0;
        AsyncRelayCommand command = new(async _ =>
        {
            executionCount++;
            started.SetResult();
            await release.Task;
        });

        Task firstExecution = command.ExecuteAsync(null);
        await started.Task;

        Assert.False(command.CanExecute(null));

        Task secondExecution = command.ExecuteAsync(null);
        release.SetResult();
        await Task.WhenAll(firstExecution, secondExecution);

        Assert.Equal(1, executionCount);
        Assert.True(command.CanExecute(null));
    }

    /// <summary>Verifies running-state changes raise command and property notifications.</summary>
    [Fact]
    public async Task ExecuteAsync_RaisesStateNotifications()
    {
        AsyncRelayCommand command = new(_ => Task.CompletedTask);
        int canExecuteNotifications = 0;
        List<string?> propertyNames = [];
        command.CanExecuteChanged += (_, _) => canExecuteNotifications++;
        command.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        await command.ExecuteAsync(null);

        Assert.Equal(2, canExecuteNotifications);
        Assert.Equal(["IsRunning", "IsRunning"], propertyNames);
    }
}
