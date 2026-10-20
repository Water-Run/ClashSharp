extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using RuntimeAdapter = ClashSharpUi::ClashSharp.Presentation.Adapters.MasterControlRuntimeAdapter;
using RuntimeSnapshot = ClashSharpUi::ClashSharp.ViewModel.MasterControlRuntimeSnapshot;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlRuntimeAdapterTests
{
    [Fact]
    public async Task NetworkObservation_UsesTheIndependentObserverOnEveryRefresh()
    {
        NetworkStateSnapshot state = new(ClashSharpMode.Disabled, false, false, false, 7890, "verified-baseline");
        int calls = 0;
        RuntimeAdapter adapter = new(new RecordingRuntimeSnapshotSource(), new DashboardNetworkObserver(_ =>
        {
            calls++;
            return calls == 1 ? Task.FromResult(state)
                : Task.FromException<NetworkStateSnapshot>(new InvalidOperationException("runtime changed"));
        }));

        RuntimeSnapshot first = await adapter.GetSnapshotAsync(CancellationToken.None);
        RuntimeSnapshot second = await adapter.GetSnapshotAsync(CancellationToken.None);

        Assert.Same(state, first.NetworkState);
        Assert.Null(second.NetworkState);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NetworkObservation_UnknownDoesNotPublishAnApparentActiveMode()
    {
        NetworkStateSnapshot unknown = new(ClashSharpMode.RuleTakeover, true, true, true, 7890, "uncertain", IsKnown: false);
        RuntimeAdapter adapter = new(new RecordingRuntimeSnapshotSource(),
            new DashboardNetworkObserver(_ => Task.FromResult(unknown)));

        RuntimeSnapshot result = await adapter.GetSnapshotAsync(CancellationToken.None);

        Assert.Null(result.NetworkState);
    }

    [Fact]
    public async Task NetworkObservation_CallerCancellationIsPropagated()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeAdapter adapter = new(new RecordingRuntimeSnapshotSource(), new DashboardNetworkObserver(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }));
        Task<RuntimeSnapshot> observation = adapter.GetSnapshotAsync(cancellation.Token);
        await entered.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation);
    }

    [Fact]
    public async Task NetworkObservation_DeadlineReturnsUnknownWithoutCancelingTheCaller()
    {
        using CancellationTokenSource cancellation = new();
        RuntimeAdapter adapter = new(new RecordingRuntimeSnapshotSource(), new DashboardNetworkObserver(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }));

        RuntimeSnapshot result = await adapter.GetSnapshotAsync(cancellation.Token);

        Assert.Null(result.NetworkState);
        Assert.False(cancellation.IsCancellationRequested);
    }

    private sealed class DashboardNetworkObserver(Func<CancellationToken, Task<NetworkStateSnapshot>> observe) : INetworkStateObserver
    {
        public Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken) => observe(cancellationToken);
    }
}
