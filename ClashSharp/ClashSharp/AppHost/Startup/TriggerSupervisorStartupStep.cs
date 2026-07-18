using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Compatibility;

namespace ClashSharp.Hosting.Startup;

/// <summary>Starts trigger scheduling only in the owned primary pipeline.</summary>
internal sealed class TriggerSupervisorStartupStep(LegacyTriggerRuntimeParticipant triggers) : IStartupStep
{
    public string Name => "trigger-supervisor";

    public int Order => 500;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        await triggers.StartAsync(cancellationToken);
        return StartupStepResult.Succeeded();
    }
}
