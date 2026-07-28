using ClashSharp.Presentation.Lifecycle;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Tests cancellable page-load session ownership.</summary>
public sealed class PageLoadSessionTests
{
    [Fact]
    public async Task RunAsync_WhenReplaced_CancelsPreviousLoad()
    {
        PageLoadSession session = new();
        TaskCompletionSource firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstCancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task firstLoad = session.RunAsync(async cancellationToken =>
        {
            firstStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                firstCancelled.SetResult();
            }
        });
        await firstStarted.Task;

        await session.RunAsync(static _ => Task.CompletedTask);
        await firstCancelled.Task;
        await firstLoad;
    }

    [Fact]
    public async Task Cancel_CancelsActiveLoad()
    {
        PageLoadSession session = new();
        TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task load = session.RunAsync(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                cancelled.SetResult();
            }
        });
        await started.Task;

        session.Cancel();

        await cancelled.Task;
        await load;
    }

    [Fact]
    public async Task RunAsync_WhenDebouncedLoadIsReplaced_DoesNotInvokeIt()
    {
        PageLoadSession session = new();
        int debouncedInvocationCount = 0;
        int replacementInvocationCount = 0;

        Task debouncedLoad = session.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref debouncedInvocationCount);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5));

        await session.RunAsync(_ =>
        {
            Interlocked.Increment(ref replacementInvocationCount);
            return Task.CompletedTask;
        });
        await debouncedLoad;

        Assert.Equal(0, debouncedInvocationCount);
        Assert.Equal(1, replacementInvocationCount);
    }

    [Fact]
    public async Task Cancel_DuringDebounce_CompletesWithoutInvokingLoad()
    {
        PageLoadSession session = new();
        int invocationCount = 0;
        Task debouncedLoad = session.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref invocationCount);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5));

        session.Cancel();
        await debouncedLoad;

        Assert.Equal(0, invocationCount);
    }
}
