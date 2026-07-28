using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Applies the configured startup mode before trigger and window activation.</summary>
internal sealed class StartupNetworkBehaviorStep(
    LegacyNetworkIntentSource intents,
    ApplicationActionService actions,
    StartupConflictSnapshot conflicts,
    LogStorageService logStorage,
    LocalizationService localization) : IStartupStep
{
    public string Name => "startup-network-behavior";

    public int Order => 450;

    public async Task<StartupStepResult> ExecuteAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        NetworkIntent intent = intents.CreateStartupBehavior();
        if (conflicts.HasBlockingConflicts && intent.Mode != ClashSharpMode.Disabled)
        {
            return StartupStepResult.Warning("startup-network-conflicts-pending");
        }

        try
        {
            NetworkTakeoverResult result = await actions
                .ApplyNetworkModeAsync(intent.Mode, cancellationToken)
                .ConfigureAwait(false);
            logStorage.AppendLog("Info", "Startup", result.Message, null);
            await actions.PublishProxyModeAppliedAsync(result.Mode, cancellationToken).ConfigureAwait(false);
            return StartupStepResult.Succeeded();
        }
        catch (NetworkTransitionFailedException exception)
            when (exception.Outcome is MutationOutcome.RecoveryRequired
                or MutationOutcome.CommittedRecoveryRequired)
        {
            logStorage.AppendLog(
                "Error",
                "Startup",
                localization.GetString("Startup.Log.ProxyBehaviorFailed"),
                exception.Message);
            return StartupStepResult.Fatal(exception.ErrorCode ?? "startup-network-recovery-required");
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or FileNotFoundException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            logStorage.AppendLog(
                "Warning",
                "Startup",
                localization.GetString("Startup.Log.ProxyBehaviorFailed"),
                exception.Message);
            return StartupStepResult.Warning("startup-network-behavior-failed");
        }
    }
}
