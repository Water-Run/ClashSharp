using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Compatibility;

namespace ClashSharp.Hosting.Startup;

/// <summary>Activates the transitional WinUI trigger presentation boundary after durable state is loaded.</summary>
internal sealed class TriggerPresentationStartupStep(
    TriggerPresentationCompatibilityFactory factory) : IStartupStep
{
    public string Name => "trigger-presentation";

    public int Order => 550;

    public Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        factory.Activate();
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
