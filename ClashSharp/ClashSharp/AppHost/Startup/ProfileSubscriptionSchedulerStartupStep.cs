using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Data;

namespace ClashSharp.Hosting.Startup;

/// <summary>Starts automatic profile subscription updates after the primary shell is ready.</summary>
internal sealed class ProfileSubscriptionSchedulerStartupStep(
    DataGenerationManager generations) : IStartupStep
{
    public string Name => "profile-subscription-updates";

    public int Order => 710;

    public async Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        await generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, token) => runtime.Subscriptions.StartAsync(token), cancellationToken).ConfigureAwait(false);
        return StartupStepResult.Succeeded();
    }
}
