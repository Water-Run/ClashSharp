extern alias ClashSharpUi;

using DispatcherOperationPolicy =
    ClashSharpUi::ClashSharp.Hosting.Startup.DispatcherOperationPolicy;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Verifies dispatcher rejection has an awaited terminal fallback.</summary>
public sealed class DispatcherOperationPolicyTests
{
    [Fact]
    public async Task TryEnqueueWithRetryAsync_FirstAttemptRejected_RetriesSameOperationOnce()
    {
        int attempts = 0;
        int operations = 0;

        bool accepted = await DispatcherOperationPolicy.TryEnqueueWithRetryAsync(
            operation =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return false;
                }

                operation();
                return true;
            },
            () => Interlocked.Increment(ref operations),
            maximumAttempts: 3,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Equal(2, attempts);
        Assert.Equal(1, operations);
    }

    [Fact]
    public async Task TryEnqueueWithRetryAsync_AlwaysRejected_StopsAtExplicitBound()
    {
        int attempts = 0;
        int operations = 0;

        bool accepted = await DispatcherOperationPolicy.TryEnqueueWithRetryAsync(
            _ =>
            {
                attempts++;
                return false;
            },
            () => operations++,
            maximumAttempts: 4,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.False(accepted);
        Assert.Equal(4, attempts);
        Assert.Equal(0, operations);
    }

    [Fact]
    public async Task RunWithRetryOrFallbackAsync_ExhaustedDispatcher_AwaitsCleanupOnce()
    {
        TaskCompletionSource cleanupCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        int cleanupCalls = 0;

        Task<bool> operation = DispatcherOperationPolicy.RunWithRetryOrFallbackAsync(
            _ =>
            {
                attempts++;
                return false;
            },
            static () => throw new InvalidOperationException("must not dispatch"),
            maximumAttempts: 3,
            retryDelay: TimeSpan.Zero,
            () =>
            {
                cleanupCalls++;
                return cleanupCompletion.Task;
            },
            CancellationToken.None);

        Assert.False(operation.IsCompleted);
        Assert.Equal(3, attempts);
        Assert.Equal(1, cleanupCalls);

        cleanupCompletion.SetResult();
        Assert.False(await operation);
    }

    [Fact]
    public void TryEnqueue_RecoverableSchedulerFailure_ReturnsFalse()
    {
        bool accepted = DispatcherOperationPolicy.TryEnqueue(
            _ => throw new ObjectDisposedException("dispatcher"),
            () => { });

        Assert.False(accepted);
    }

    [Fact]
    public void TryEnqueue_WrappedProcessFatalFailure_PropagatesWrapper()
    {
        InvalidOperationException expected = new(
            "dispatcher wrapper",
            Activator.CreateInstance<OutOfMemoryException>());

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => DispatcherOperationPolicy.TryEnqueue(
                _ => throw expected,
                () => { }));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RunOrFallbackAsync_DispatcherRejects_RunsAndAwaitsFallback()
    {
        TaskCompletionSource fallbackCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatchedCalls = 0;
        int fallbackCalls = 0;

        Task operation = DispatcherOperationPolicy.RunOrFallbackAsync(
            _ => false,
            () =>
            {
                dispatchedCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                fallbackCalls++;
                return fallbackCompletion.Task;
            });

        Assert.False(operation.IsCompleted);
        Assert.Equal(0, dispatchedCalls);
        Assert.Equal(1, fallbackCalls);

        fallbackCompletion.SetResult();
        await operation;
    }

    [Fact]
    public async Task RunOrFallbackAsync_DispatcherRejects_ObservesFallbackFailure()
    {
        IOException expected = new("terminal fallback failed");

        IOException actual = await Assert.ThrowsAsync<IOException>(
            () => DispatcherOperationPolicy.RunOrFallbackAsync(
                _ => false,
                () => Task.CompletedTask,
                () => Task.FromException(expected)));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RunOrFallbackAsync_DispatcherRejects_DoesNotCaptureRejectingContext()
    {
        TaskCompletionSource fallbackCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        RejectingSynchronizationContext rejectingContext = new();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(rejectingContext);
            operation = DispatcherOperationPolicy.RunOrFallbackAsync(
                _ => false,
                () => Task.CompletedTask,
                () => fallbackCompletion.Task);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        fallbackCompletion.SetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, rejectingContext.PostCount);
    }

    [Fact]
    public async Task RunOrFallbackAsync_DispatcherAccepts_AwaitsDispatchedOperation()
    {
        Action? callback = null;
        TaskCompletionSource dispatchedCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int fallbackCalls = 0;

        Task operation = DispatcherOperationPolicy.RunOrFallbackAsync(
            scheduled =>
            {
                callback = scheduled;
                return true;
            },
            () => dispatchedCompletion.Task,
            () =>
            {
                fallbackCalls++;
                return Task.CompletedTask;
            });

        Assert.NotNull(callback);
        Assert.False(operation.IsCompleted);
        callback();
        Assert.False(operation.IsCompleted);

        dispatchedCompletion.SetResult();
        await operation;
        Assert.Equal(0, fallbackCalls);
    }

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            PostCount++;
        }
    }
}
