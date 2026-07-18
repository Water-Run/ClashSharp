using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Starts configured connection sampling after the primary shell exists.</summary>
internal sealed class ConnectionSamplingStartupStep : IStartupStep
{
    public string Name => "connection-sampling";

    public int Order => 700;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionSamplingService.Instance.StartIfEnabled();
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
