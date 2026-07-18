using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Tests.Architecture;

/// <summary>Specifies fair mutation serialization and admission state transitions.</summary>
public sealed class MutationAdmissionContractTests
{
    /// <summary>Verifies queued mutation work enters the gate in submission order.</summary>
    [Fact]
    public async Task FairGate_ConcurrentWaiters_EnterInFifoOrder()
    {
        FairAsyncMutationGate gate = new();
        TaskCompletionSource<object?> firstEntered = CreateSignal();
        TaskCompletionSource<object?> releaseFirst = CreateSignal();
        List<int> order = [];

        Task first = gate.ExecuteAsync(
            Guid.NewGuid(),
            async (_, _) =>
            {
                order.Add(1);
                firstEntered.SetResult(null);
                await releaseFirst.Task;
            },
            CancellationToken.None);
        await firstEntered.Task;

        Task second = gate.ExecuteAsync(
            Guid.NewGuid(),
            (_, _) =>
            {
                order.Add(2);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        Task third = gate.ExecuteAsync(
            Guid.NewGuid(),
            (_, _) =>
            {
                order.Add(3);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, gate.QueuedCount);
        releaseFirst.SetResult(null);
        await Task.WhenAll(first, second, third);

        Assert.Equal([1, 2, 3], order);
        Assert.False(gate.IsHeld);
        Assert.Equal(0, gate.QueuedCount);
    }

    /// <summary>Verifies cancellation removes a waiter without disturbing later FIFO work.</summary>
    [Fact]
    public async Task FairGate_CancelledWaiter_DoesNotEnterOrBlockLaterWork()
    {
        FairAsyncMutationGate gate = new();
        TaskCompletionSource<object?> firstEntered = CreateSignal();
        TaskCompletionSource<object?> releaseFirst = CreateSignal();
        List<int> order = [];
        using CancellationTokenSource cancellation = new();

        Task first = gate.ExecuteAsync(
            Guid.NewGuid(),
            async (_, _) =>
            {
                order.Add(1);
                firstEntered.SetResult(null);
                await releaseFirst.Task;
            },
            CancellationToken.None);
        await firstEntered.Task;

        Task cancelled = gate.ExecuteAsync(
            Guid.NewGuid(),
            (_, _) =>
            {
                order.Add(2);
                return Task.CompletedTask;
            },
            cancellation.Token);
        Task third = gate.ExecuteAsync(
            Guid.NewGuid(),
            (_, _) =>
            {
                order.Add(3);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        Assert.Equal(2, gate.QueuedCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, gate.QueuedCount);

        releaseFirst.SetResult(null);
        await Task.WhenAll(first, third);
        Assert.Equal([1, 3], order);
    }

    /// <summary>Verifies the same asynchronous flow cannot recursively acquire the mutation gate.</summary>
    [Fact]
    public async Task FairGate_SameFlowNestedExecution_IsRejectedWithoutDeadlock()
    {
        FairAsyncMutationGate gate = new();
        Guid operationId = Guid.NewGuid();

        MutationReentrancyException exception = await Assert.ThrowsAsync<MutationReentrancyException>(
            () => gate.ExecuteAsync(
                operationId,
                async (context, cancellationToken) =>
                {
                    Assert.Equal(operationId, context.OperationId);
                    await gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, cancellationToken);
                },
                CancellationToken.None));

        Assert.Equal(operationId, exception.OwnerOperationId);
        Assert.False(gate.IsHeld);
    }

    /// <summary>Verifies destructive admission waits for ordinary leases and rejects new ordinary work.</summary>
    [Fact]
    public async Task AdmissionBarrier_CloseAndDrain_WaitsForOrdinaryLeaseThenReopens()
    {
        MutationAdmissionBarrier barrier = new();
        await using MutationAdmissionLease ordinary = await barrier.AcquireOrdinaryAsync(CancellationToken.None);

        ValueTask<MutationAdmissionLease> exclusiveTask = barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None);

        Assert.Equal(MutationAdmissionState.Closing, barrier.State);
        Assert.False(exclusiveTask.IsCompleted);
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.AcquireOrdinaryAsync(CancellationToken.None));

        await ordinary.DisposeAsync();
        await using MutationAdmissionLease exclusive = await exclusiveTask;
        Assert.True(exclusive.IsExclusive);

        await exclusive.DisposeAsync();
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
    }

    /// <summary>Verifies recovery-only admission permits one recovery lease and no ordinary lease.</summary>
    [Fact]
    public async Task AdmissionBarrier_RecoveryOnly_PermitsExactlyOneRecoveryLease()
    {
        MutationAdmissionBarrier barrier = new();
        barrier.EnterRecoveryOnly();

        await using MutationAdmissionLease recovery = await barrier.AcquireRecoveryAsync(CancellationToken.None);
        Assert.True(recovery.IsExclusive);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, barrier.State);
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.AcquireOrdinaryAsync(CancellationToken.None));
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.AcquireRecoveryAsync(CancellationToken.None));

        await recovery.DisposeAsync();
        Assert.Equal(MutationAdmissionState.RecoveryOnly, barrier.State);
    }

    /// <summary>Verifies shutdown closure is terminal after existing leases drain.</summary>
    [Fact]
    public async Task AdmissionBarrier_ShutdownClosure_IsTerminal()
    {
        MutationAdmissionBarrier barrier = new();
        await using MutationAdmissionLease ordinary = await barrier.AcquireOrdinaryAsync(CancellationToken.None);
        ValueTask<MutationAdmissionLease> shutdownTask = barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Shutdown,
            CancellationToken.None);

        await ordinary.DisposeAsync();
        await using MutationAdmissionLease shutdown = await shutdownTask;
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
        await shutdown.DisposeAsync();

        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.AcquireOrdinaryAsync(CancellationToken.None));
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.AcquireRecoveryAsync(CancellationToken.None));
    }

    /// <summary>Verifies shutdown pending during recovery wins atomically over a successful completion.</summary>
    [Fact]
    public async Task AdmissionBarrier_ShutdownDuringRecovery_CompletionCannotReopenAdmission()
    {
        MutationAdmissionBarrier barrier = new();
        barrier.EnterRecoveryOnly();
        await using MutationAdmissionLease recovery = await barrier.AcquireRecoveryAsync(CancellationToken.None);

        Task shutdown = barrier.RequestRecoveryShutdownAsync(CancellationToken.None);
        Assert.Equal(MutationAdmissionState.RecoveryClosing, barrier.State);
        Assert.False(shutdown.IsCompleted);

        recovery.CompleteRecoveryAttempt(journalPresent: false, verifiedSuccess: true);
        await shutdown;

        Assert.Equal(MutationAdmissionState.ClosedForShutdown, barrier.State);
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.AcquireOrdinaryAsync(CancellationToken.None));
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(
            async () => await barrier.AcquireRecoveryAsync(CancellationToken.None));
    }

    private static TaskCompletionSource<object?> CreateSignal()
    {
        return new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
