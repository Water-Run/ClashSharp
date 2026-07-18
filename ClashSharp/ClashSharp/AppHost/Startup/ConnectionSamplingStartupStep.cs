using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Compatibility;

namespace ClashSharp.Hosting.Startup;

/// <summary>Starts configured connection sampling after the primary shell exists.</summary>
internal sealed class ConnectionSamplingStartupStep(
    LegacyConnectionSamplingRuntimeParticipant sampling) : IStartupStep
{
    public string Name => "connection-sampling";

    public int Order => 700;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        await sampling.StartAsync(cancellationToken);
        return StartupStepResult.Succeeded();
    }
}
