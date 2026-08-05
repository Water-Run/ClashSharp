using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Starts automatic profile subscription updates after the primary shell is ready.</summary>
internal sealed class ProfileSubscriptionSchedulerStartupStep(
    ProfileSubscriptionScheduler scheduler) : IStartupStep
{
    public string Name => "profile-subscription-updates";

    public int Order => 710;

    public async Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        await scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
        return StartupStepResult.Succeeded();
    }
}
