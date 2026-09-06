using ClashSharp.Presentation.Lifecycle;
using ClashSharp.Tests.Unit.ViewModel;

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

    [Fact]
    public async Task DrainAsync_WaitsForReplacedReadToFinishUnwinding()
    {
        PageLoadSession session = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task oldRead = session.RunAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.RunAsync(_ => Task.CompletedTask);

        Task drain = session.DrainAsync();
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        await oldRead;
        Assert.True(session.DrainAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RunAsync_CancelledCallerDoesNotInvokeRead()
    {
        PageLoadSession session = new();
        using CancellationTokenSource caller = new();
        caller.Cancel();
        bool invoked = false;
        await session.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, cancellationToken: caller.Token);

        Assert.False(invoked);
        await session.DrainAsync();
    }

    [Fact]
    public async Task Replacement_ThrowingCancellationCallbackIsObservedAndNewReadRuns()
    {
        TestApplicationErrorSink errors = new();
        PageLoadSession session = new(errors);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task oldRead = session.RunAsync(async token =>
        {
            using CancellationTokenRegistration callback = token.Register(
                () => throw new InvalidOperationException("Cancellation failed."));
            started.SetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool invoked = false;
        await session.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });
        release.SetResult();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await oldRead;

        Assert.True(invoked);
        Assert.IsType<AggregateException>(Assert.Single(errors.Errors).Exception);
    }

    [Fact]
    public async Task Cancel_ThrowingCallbackIsOwnedByDrain()
    {
        TestApplicationErrorSink errors = new();
        PageLoadSession session = new(errors);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task read = session.RunAsync(async token =>
        {
            using CancellationTokenRegistration callback = token.Register(
                () => throw new InvalidOperationException("Cancellation failed."));
            started.SetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Cancel();
        release.SetResult();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await read;

        Assert.IsType<AggregateException>(Assert.Single(errors.Errors).Exception);
    }

    [Fact]
    public async Task Replacement_WithoutErrorSinkReleasesFailedReadOwnership()
    {
        PageLoadSession session = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task oldRead = session.RunAsync(async token =>
        {
            using CancellationTokenRegistration callback = token.Register(
                () => throw new InvalidOperationException("Cancellation failed."));
            started.SetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<AggregateException>(
            () => session.RunAsync(_ => Task.CompletedTask));
        release.SetResult();
        await oldRead;
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.RunAsync(_ => Task.CompletedTask);
        Assert.True(session.DrainAsync().IsCompletedSuccessfully);
    }
}
