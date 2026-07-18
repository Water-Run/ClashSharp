using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Starts trigger scheduling only in the owned primary pipeline.</summary>
internal sealed class TriggerSupervisorStartupStep(TriggerService triggers) : IStartupStep
{
    public string Name => "trigger-supervisor";

    public int Order => 500;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        triggers.Start();
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
