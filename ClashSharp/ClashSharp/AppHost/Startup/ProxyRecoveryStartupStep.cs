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

/// <summary>Completes best-effort stale proxy recovery before window startup.</summary>
internal sealed class ProxyRecoveryStartupStep(
    NetworkStateCoordinator network,
    LegacyNetworkIntentSource intents) : IStartupStep
{
    public string Name => "proxy-recovery";

    public int Order => 300;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            MutationResult<NetworkTransitionResult> result = await network
                .ApplyAsync(intents.CreateStartupRecovery(), cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == MutationOutcome.Succeeded)
            {
                return StartupStepResult.Succeeded();
            }

            LogStorageService.Instance.AppendLog(
                "Warning",
                "ProxyRecovery",
                LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                result.ErrorCode);
            return result.Outcome is MutationOutcome.RecoveryRequired or MutationOutcome.CommittedRecoveryRequired
                ? StartupStepResult.Fatal(result.ErrorCode ?? "proxy-recovery-required")
                : StartupStepResult.Warning(result.ErrorCode ?? "proxy-recovery-failed");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog(
                "Warning",
                "ProxyRecovery",
                LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                exception.Message);
            return StartupStepResult.Warning("ProxyRecovery.StartupFailed");
        }
    }
}
