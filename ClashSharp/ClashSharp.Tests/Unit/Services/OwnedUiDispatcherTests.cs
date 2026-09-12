using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Tests.Unit.Services;

public sealed class OwnedUiDispatcherTests
{
    [Fact]
    public async Task ConstructionIsPure_AndOwningThreadRunsWithoutEnqueue()
    {
        int threadReads = 0;
        await using OwnedUiDispatcher dispatcher = new(() => { ++threadReads; return true; }, _ => throw new IOException(), CancellationToken.None);
        Assert.Equal(0, threadReads);
        int thread = Environment.CurrentManagedThreadId;
        Assert.Equal(thread, await dispatcher.InvokeAsync(() => Environment.CurrentManagedThreadId, CancellationToken.None));
        Assert.Equal(1, threadReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrThrowingEnqueue_CannotRunAnUnstartedCallbackLater(bool throws)
    {
        Action? queued = null;
        IOException failure = new("Isolated queue reply failure.");
        await using OwnedUiDispatcher dispatcher = new(() => false, action =>
        {
            queued = action;
            return throws ? throw failure : false;
        }, CancellationToken.None);
        int effects = 0;
        Exception? observed = await Record.ExceptionAsync(() => dispatcher.InvokeAsync(() => ++effects, CancellationToken.None));
        if (throws) { Assert.Same(failure, observed); }
        else { Assert.IsType<InvalidOperationException>(observed); }
        Assert.NotNull(queued);
        queued();
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task ExecutedCallback_ResolvesAnEnqueueReplyLostAfterCompletion()
    {
        await using OwnedUiDispatcher dispatcher = new(() => false, action => { action(); throw new IOException("Lost queue reply."); }, CancellationToken.None);
        int effects = 0;
        Assert.Equal(1, await dispatcher.InvokeAsync(() => ++effects, CancellationToken.None));
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task CancelledQueuedOperation_IsObservedAtTheCallbackWithoutStartingEffects()
    {
        using CancellationTokenSource cancellation = new();
        Action? queued = null;
        await using OwnedUiDispatcher dispatcher = new(() => false, action => { queued = action; return true; }, CancellationToken.None);
        int effects = 0;
        Task<int> operation = dispatcher.InvokeAsync(() => ++effects, cancellation.Token);
        cancellation.Cancel();
        Assert.False(operation.IsCompleted);
        Assert.NotNull(queued);
        queued();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task WindowShutdown_RevokesUnstartedCallbacksAndRejectsFutureOperations()
    {
        using CancellationTokenSource window = new();
        Action? queued = null;
        await using OwnedUiDispatcher dispatcher = new(() => false, action => { queued = action; return true; }, window.Token);
        int effects = 0;
        Task<int> operation = dispatcher.InvokeAsync(() => ++effects, CancellationToken.None);
        window.Cancel();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => ++effects, CancellationToken.None));
        Assert.NotNull(queued);
        queued();
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task StartedCallback_RetainsCompletionAcrossCancellationAndRetirement()
    {
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        Action? queued = null;
        await using OwnedUiDispatcher dispatcher = new(() => false, action => { queued = action; return true; }, CancellationToken.None);
        Task<int> operation = dispatcher.InvokeAsync(() =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Test callback release was not observed."); }
            return 42;
        }, cancellation.Token);
        Assert.NotNull(queued);
        Task callback = Task.Run(queued);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            Task retirement = dispatcher.DisposeAsync().AsTask();
            Assert.False(operation.IsCompleted);
            Assert.False(retirement.IsCompleted);
            release.Set();
            Assert.Equal(42, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); await callback.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackFailure_PreservesOriginalExceptionAndFatalDispatcherPropagation(bool fatal)
    {
        Action? queued = null;
        await using OwnedUiDispatcher dispatcher = new(() => false, action => { queued = action; return true; }, CancellationToken.None);
        Exception failure = fatal ? new AggregateException(new InvalidOperationException("Nested fatal.", Activator.CreateInstance<OutOfMemoryException>()))
            : new IOException("Isolated callback failure.");
        Task<int> operation = dispatcher.InvokeAsync<int>(() => throw failure, CancellationToken.None);
        Assert.NotNull(queued);
        Exception? dispatchFailure = Record.Exception(queued);
        if (fatal) { Assert.Same(failure, dispatchFailure); }
        else { Assert.Null(dispatchFailure); }
        Assert.Same(failure, await Record.ExceptionAsync(() => operation));
    }
}
