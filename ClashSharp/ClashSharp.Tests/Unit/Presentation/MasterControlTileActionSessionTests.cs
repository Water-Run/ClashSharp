using ClashSharp.Presentation.Lifecycle;
using ClashSharp.Tests.Unit.ViewModel;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class MasterControlTileActionSessionTests
{
    [Fact]
    public async Task Deactivate_CancelsRunningAndQueuedInteractionsAndRejectsLateClicks()
    {
        TestApplicationErrorSink errors = new();
        MasterControlTileActionSession session = new(errors);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        session.Activate(async (_, token) =>
        {
            calls++;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        Task first = session.ExecuteAsync(MasterControlTileAction.RunLatencyTest, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task queued = session.ExecuteAsync(MasterControlTileAction.ImportConfiguration, CancellationToken.None);

        session.Deactivate();
        await session.ExecuteAsync(MasterControlTileAction.ExportConfiguration, CancellationToken.None);
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, queued);

        Assert.Equal(1, calls);
        Assert.Empty(errors.Errors);
        session.Activate((_, _) => { calls++; return Task.CompletedTask; });
        await session.ExecuteAsync(MasterControlTileAction.ShowStartupPrompt, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WaitsForThePresenterToUnwindAfterCancellation()
    {
        MasterControlTileActionSession session = new(new TestApplicationErrorSink());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Activate(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        Task execution = session.ExecuteAsync(MasterControlTileAction.RunLatencyTest, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Deactivate();
        Task drain = session.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.False(execution.IsCompleted);
        release.SetResult();
        await Task.WhenAll(drain, execution).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
