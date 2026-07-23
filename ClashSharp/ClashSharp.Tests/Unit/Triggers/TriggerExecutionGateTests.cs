using ClashSharp.ApplicationModel.Triggers;

namespace ClashSharp.Tests.Unit.Triggers;

/// <summary>Verifies task-keyed asynchronous trigger serialization.</summary>
public sealed class TriggerExecutionGateTests
{
    [Fact]
    public async Task EnterAsync_SameTaskWaitsUntilCurrentLeaseIsReleased()
    {
        TriggerExecutionGate gate = new();
        TriggerExecutionLease first = await gate.EnterAsync("same", CancellationToken.None);

        Task<TriggerExecutionLease> secondWait = gate
            .EnterAsync("same", CancellationToken.None)
            .AsTask();

        Assert.False(secondWait.IsCompleted);
        first.Dispose();
        await using TriggerExecutionLease second = await secondWait;
    }

    [Fact]
    public async Task EnterAsync_DifferentTasksProgressIndependently()
    {
        TriggerExecutionGate gate = new();
        await using TriggerExecutionLease first = await gate.EnterAsync(
            "first",
            CancellationToken.None);

        Task<TriggerExecutionLease> secondWait = gate
            .EnterAsync("second", CancellationToken.None)
            .AsTask();

        Assert.True(secondWait.IsCompletedSuccessfully);
        await using TriggerExecutionLease second = await secondWait;
    }

    [Fact]
    public async Task EnterAsync_CancelledWaiterDoesNotPoisonLaterAcquisition()
    {
        TriggerExecutionGate gate = new();
        TriggerExecutionLease first = await gate.EnterAsync("same", CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        Task<TriggerExecutionLease> cancelledWait = gate
            .EnterAsync("same", cancellation.Token)
            .AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        first.Dispose();

        await using TriggerExecutionLease next = await gate.EnterAsync(
            "same",
            CancellationToken.None);
    }
}
