using System.Runtime.CompilerServices;
using ClashSharp.Installer.Presentation;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class AsyncDelegateCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingExecutionSurvivesCollectionAndReleasesItsOperation(bool fail)
    {
        var source = new WeakCompletionSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int failures = 0;
        var command = new AsyncDelegateCommand(source.Begin, canExecute: null, () => failures++);
        command.CanExecuteChanged += (_, _) =>
        {
            if (command.CanExecute(parameter: null)) { finished.TrySetResult(); }
        };

        BeginWithoutRetainingCallerTask(command);
        Assert.False(command.CanExecute(parameter: null));
        CollectUnrootedOperations();
        Assert.True(source.Complete(fail), "The live command lost its pending operation during collection.");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fail ? 1 : 0, failures);
        Assert.True(command.CanExecute(parameter: null));

        // Let the completion callback return before checking that the command released its root.
        await Task.Delay(10);
        CollectUnrootedOperations();
        Assert.False(source.IsAlive());
        GC.KeepAlive(command);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeginWithoutRetainingCallerTask(AsyncDelegateCommand command) => _ = command.ExecuteAsync();

    private static void CollectUnrootedOperations()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Models a native completion source whose managed state is kept alive by the returned task.
    // The test driver retains only a weak callback handle, not the command's operation.
    private sealed class WeakCompletionSource
    {
        private WeakReference<CompletionRelay>? _relay;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task Begin()
        {
            var relay = new CompletionRelay();
            _relay = new(relay);
            return relay.Completion.Task;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Complete(bool fail)
        {
            if (_relay is null || !_relay.TryGetTarget(out CompletionRelay? relay)) { return false; }
            if (fail) { relay.Completion.SetException(new IOException("private failure sentinel")); }
            else { relay.Completion.SetResult(); }
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsAlive() => _relay?.TryGetTarget(out _) == true;
    }

    private sealed class CompletionRelay
    {
        public CompletionRelay() => Completion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; }
    }

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
