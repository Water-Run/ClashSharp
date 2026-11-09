using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>
/// Interleavings between prepared catalog mutations, the shared fair gate and admission shutdown.
/// The scenarios use the production barrier and gate; only the prepared and mutation callbacks are scripted.
/// </summary>
public sealed class ProfileCatalogMutationCoordinatorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    /// <summary>A caller that gives up after its download finished must not run the mutation or keep admission.</summary>
    [Fact]
    public async Task PreparedMutation_CallerCancelsWhileQueuedBehindTheGate_SkipsTheMutationAndReleasesAdmission()
    {
        MutationAdmissionBarrier admission = new();
        FairAsyncMutationGate gate = new();
        ProfileCatalogMutationCoordinator coordinator = new(admission, gate);
        using GateOwner owner = await GateOwner.HoldAsync(gate);
        using CancellationTokenSource caller = new();
        bool mutated = false;
        List<Exception> reports = [];

        Task<int> queued = coordinator.ExecutePreparedAsync<bool, int>(
            Guid.NewGuid(),
            _ => Task.FromResult(true),
            (_, _, _) =>
            {
                mutated = true;
                return Task.FromResult(1);
            },
            caller.Token,
            reports.Add);
        await WaitUntilAsync(() => gate.QueuedCount == 1);
        await caller.CancelAsync();

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(Limit));

        Assert.IsNotType<ProfileCatalogMutationCanceledException>(cancelled);
        Assert.False(mutated);
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(reports));
        Assert.Equal(0, gate.QueuedCount);
        Assert.True(gate.IsHeld, "The unrelated owner must keep the gate.");
        await using MutationAdmissionLease closed = await admission
            .CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None)
            .AsTask()
            .WaitAsync(Limit);
        await owner.ReleaseAsync();
    }

    /// <summary>Shutdown that revokes admission while a prepared mutation is queued yields the trusted cancellation signal.</summary>
    [Fact]
    public async Task PreparedMutation_AdmissionClosedWhileQueued_ReportsTheTrustedCancellationAndDrains()
    {
        MutationAdmissionBarrier admission = new();
        FairAsyncMutationGate gate = new();
        ProfileCatalogMutationCoordinator coordinator = new(admission, gate);
        using GateOwner owner = await GateOwner.HoldAsync(gate);
        bool mutated = false;
        List<Exception> reports = [];

        Task<int> queued = coordinator.ExecutePreparedAsync<bool, int>(
            Guid.NewGuid(),
            _ => Task.FromResult(true),
            (_, _, _) =>
            {
                mutated = true;
                return Task.FromResult(1);
            },
            CancellationToken.None,
            reports.Add);
        await WaitUntilAsync(() => gate.QueuedCount == 1);
        Task<MutationAdmissionLease> drain = admission
            .CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None)
            .AsTask();

        ProfileCatalogMutationCanceledException cancelled =
            await Assert.ThrowsAsync<ProfileCatalogMutationCanceledException>(() => queued.WaitAsync(Limit));

        Assert.False(mutated);
        Assert.Same(cancelled, Assert.Single(reports));
        Assert.Equal(0, gate.QueuedCount);
        await using MutationAdmissionLease closed = await drain.WaitAsync(Limit);
        await owner.ReleaseAsync();
    }

    /// <summary>Preparation retains admission without the shared gate, and shutdown cancels it with the trusted signal.</summary>
    [Fact]
    public async Task PreparedMutation_PreparationLeavesTheGateFreeAndShutdownCancelsIt()
    {
        MutationAdmissionBarrier admission = new();
        FairAsyncMutationGate gate = new();
        ProfileCatalogMutationCoordinator coordinator = new(admission, gate);
        TaskCompletionSource prepareStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool mutated = false;

        Task<int> running = coordinator.ExecutePreparedAsync<bool, int>(
            Guid.NewGuid(),
            async token =>
            {
                prepareStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return true;
            },
            (_, _, _) =>
            {
                mutated = true;
                return Task.FromResult(1);
            },
            CancellationToken.None);
        await prepareStarted.Task.WaitAsync(Limit);

        Assert.False(gate.IsHeld);
        Assert.Equal(7, await gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.FromResult(7), CancellationToken.None)
            .WaitAsync(Limit));
        Task<MutationAdmissionLease> drain = admission
            .CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None)
            .AsTask();

        await Assert.ThrowsAsync<ProfileCatalogMutationCanceledException>(() => running.WaitAsync(Limit));

        Assert.False(mutated);
        await using MutationAdmissionLease closed = await drain.WaitAsync(Limit);
    }

    /// <summary>
    /// A failed commit records its status after the shared gate is free but while admission is still held,
    /// so shutdown cannot interleave with the status write and later queued work is not blocked by it.
    /// </summary>
    [Fact]
    public async Task PreparedMutation_CommitFailureReportsAfterTheGateIsFreeButBeforeAdmissionIsReleased()
    {
        MutationAdmissionBarrier admission = new();
        FairAsyncMutationGate gate = new();
        ProfileCatalogMutationCoordinator coordinator = new(admission, gate);
        Task<MutationAdmissionLease>? drain = null;
        bool gateHeldWhileReporting = true;
        int reports = 0;

        await Assert.ThrowsAsync<IOException>(() => coordinator.ExecutePreparedAsync<bool, int>(
            Guid.NewGuid(),
            _ => Task.FromResult(true),
            (_, _, _) => Task.FromException<int>(new IOException("Isolated commit failure.")),
            CancellationToken.None,
            _ =>
            {
                ++reports;
                gateHeldWhileReporting = gate.IsHeld;
                drain = admission
                    .CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None)
                    .AsTask();
                Assert.False(drain.IsCompleted);
            }));

        Assert.Equal(1, reports);
        Assert.False(gateHeldWhileReporting);
        await using MutationAdmissionLease closed = await drain!.WaitAsync(Limit);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Limit);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Holds the shared gate on behalf of an unrelated operation until released.</summary>
    private sealed class GateOwner : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _owner = Task.CompletedTask;

        public static async Task<GateOwner> HoldAsync(FairAsyncMutationGate gate)
        {
            GateOwner owner = new();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            owner._owner = gate.ExecuteAsync(
                Guid.NewGuid(),
                async (_, _) =>
                {
                    entered.SetResult();
                    await owner._release.Task.ConfigureAwait(false);
                },
                CancellationToken.None);
            await entered.Task.WaitAsync(Limit);
            return owner;
        }

        public async Task ReleaseAsync()
        {
            _release.TrySetResult();
            await _owner.WaitAsync(Limit);
        }

        public void Dispose() => _release.TrySetResult();
    }
}
