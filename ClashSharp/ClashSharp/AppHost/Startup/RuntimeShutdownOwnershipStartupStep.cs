using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Compatibility;

namespace ClashSharp.Hosting.Startup;

/// <summary>Establishes cleanup ownership after data and credentials are ready, before runtime recovery may change network state.</summary>
internal sealed class RuntimeShutdownOwnershipStartupStep(
    RuntimeLifetimeRegistry runtime,
    NetworkStateCoordinator network,
    LegacyNetworkIntentSource intents) : IStartupStep
{
    public string Name => "runtime-shutdown-ownership";

    public int Order => 145;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        runtime.RegisterNetwork(network, intents.CreateShutdown);
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
