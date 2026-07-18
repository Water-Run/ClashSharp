using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Runs the login restore helper without constructing the normal application shell.</summary>
internal sealed class StartupRestoreFallbackStep(
    NetworkStateCoordinator network,
    LegacyNetworkIntentSource intents) : IStartupStep
{
    public string Name => "startup-restore-fallback";

    public int Order => 200;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Arguments.Contains(StartupRestoreFallbackService.HelperArgument, StringComparison.OrdinalIgnoreCase))
        {
            return StartupStepResult.Succeeded();
        }

        try
        {
            var result = await network
                .ApplyAsync(intents.CreateStartupRecovery(), cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome != MutationOutcome.Succeeded)
            {
                LogStorageService.Instance.AppendLog(
                    "Warning",
                    "StartupRestoreFallback",
                    LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                    result.ErrorCode);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog(
                "Warning",
                "StartupRestoreFallback",
                LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                exception.Message);
        }

        return StartupStepResult.ExitRequested();
    }
}
