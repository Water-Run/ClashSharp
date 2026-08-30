using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class AsyncDelegateCommandTests
{
    [Fact]
    public async Task ConcurrentInvocationRunsDelegateOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var command = new AsyncDelegateCommand(
            async () =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
            },
            canExecute: null,
            onUnhandledFailure: static () => throw new InvalidOperationException());

        Task first = command.ExecuteAsync();
        Task second = command.ExecuteAsync();
        Assert.True(second.IsCompletedSuccessfully);
        Assert.False(command.CanExecute(parameter: null));
        Assert.Equal(1, Volatile.Read(ref calls));

        release.SetResult();
        await first;
        Assert.True(command.CanExecute(parameter: null));
    }

    [Fact]
    public async Task RawDelegateFailureInvokesSanitizedCallback()
    {
        int failures = 0;
        var command = new AsyncDelegateCommand(
            static () => throw new IOException("raw failure"),
            canExecute: null,
            () => failures++);

        await command.ExecuteAsync();

        Assert.Equal(1, failures);
        Assert.True(command.CanExecute(parameter: null));
    }

    [Fact]
    public async Task FatalDelegateFailurePropagatesWithoutInvokingRecoveryUi()
    {
        int failures = 0;
        var cause = new FatalPresentationTestException("fatal test sentinel");
        var command = new AsyncDelegateCommand(
            () => Task.FromException(cause),
            canExecute: null,
            () => failures++);

        FatalPresentationTestException exception =
            await Assert.ThrowsAsync<FatalPresentationTestException>(
                command.ExecuteAsync);

        Assert.Same(cause, exception);
        Assert.Equal(0, failures);
        Assert.True(command.CanExecute(parameter: null));
    }
}
