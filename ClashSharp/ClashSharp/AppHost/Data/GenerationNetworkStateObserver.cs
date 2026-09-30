using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Network;

namespace ClashSharp.Hosting.Data;

/// <summary>Uses the legacy observer only during pre-generation startup recovery, then resolves every observation from the active scope.</summary>
internal sealed class GenerationNetworkStateObserver(DataGenerationManager generations, INetworkStateObserver startupRecovery) : INetworkStateObserver
{
    public Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken) => generations.IsAwaitingInitialization
        ? startupRecovery.ObserveAsync(cancellationToken)
        : generations.ExecuteAsync<AppDataGenerationRuntime, NetworkStateSnapshot>(
            (runtime, _, token) => runtime.NetworkObserver.ObserveAsync(token), cancellationToken);
}
