using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Data;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Startup;

/// <summary>Opens the generation and prepares non-network settings before conflict checks and runtime activation.</summary>
internal sealed class DataGenerationStartupStep(
    DataGenerationBootstrapper bootstrap,
    DataGenerationManager generations,
    MutationAdmissionBarrier admission,
    RuntimeLifetimeRegistry lifetime,
    GenerationSamplingRuntime sampling,
    GenerationSettingsAuthority authority,
    AppSettingsService settings,
    GenerationReplacementStartupRecovery replacementRecovery) : IStartupStep
{
    private bool _registered;
    private bool _opened;
    private GenerationReplacementCheckpoint? _checkpoint;
    private StartupStepResult? _activationResult;
    public string Name => "data-generation";
    public int Order => 225;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_opened) { return StartupStepResult.Succeeded(); }
        StartupStepResult result;
        await using (MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false))
        {
            try { await replacementRecovery.ValidateBeforeBootstrapAdmittedAsync(lease, cancellationToken).ConfigureAwait(false); }
            catch (Exception failure)
            {
                await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false);
                if (ExceptionGraphClassifier.IsProcessFatal(failure) || ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken)) { throw; }
                return StartupStepResult.Fatal("data-generation.recovery_required");
            }
            _ = await bootstrap.InitializeAdmittedAsync(lease, cancellationToken).ConfigureAwait(false);
            settings.BindAuthority(authority);
            if (!_registered)
            {
                // Registration follows successful initialization and precedes every producer start.
                // Early startup cleanup therefore never resolves a generation that does not exist.
                lifetime.RegisterParticipant(new GenerationRuntimeParticipant("trigger-scheduler", generations,
                    runtime => runtime.TriggerSettings.Scheduler), order: 100);
                lifetime.RegisterParticipant(sampling, order: 200);
                lifetime.RegisterParticipant(new GenerationRuntimeParticipant("profile-subscription-updates", generations,
                    runtime => runtime.Subscriptions), order: 300);
                _registered = true;
            }
            try
            {
                _checkpoint = await replacementRecovery.PrepareAdmittedAsync(lease, cancellationToken).ConfigureAwait(false);
                result = await generations.ExecuteAsync<AppDataGenerationRuntime, StartupStepResult>(
                    (runtime, _, token) => runtime.PrepareStartupAdmittedAsync(lease, token), cancellationToken).ConfigureAwait(false);
                if (_checkpoint is not null && result.Outcome == StartupStepOutcome.Fatal) { await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false); }
            }
            catch (Exception failure)
            {
                await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false);
                if (ExceptionGraphClassifier.IsProcessFatal(failure) || ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken)) { throw; }
                result = StartupStepResult.Fatal("data-generation.recovery_required");
            }
        }
        _opened = result.Outcome is StartupStepOutcome.Succeeded or StartupStepOutcome.Warning;
        return result;
    }

    /// <summary>Applies the startup policy after its conflict snapshot, then publishes the complete runtime.</summary>
    public async Task<StartupStepResult> ActivateRuntimeAsync(StartupConflictSnapshot conflicts,
        Func<ClashSharpMode, CancellationToken, Task> publishMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(publishMode);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_opened) { return StartupStepResult.Fatal("data-generation.not_opened"); }
        if (_activationResult is not null) { return (StartupStepResult)_activationResult; }
        StartupStepResult result;
        await using (MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                ClashSharpMode? verifiedMode = null;
                result = await generations.ExecuteAsync<AppDataGenerationRuntime, StartupStepResult>(async (runtime, _, token) =>
                {
                    SettingsEnvelope envelope = runtime.Repositories.Session.Snapshot;
                    ClashSharpMode currentMode = envelope.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>();
                    if (!Enum.IsDefined(currentMode) || currentMode == ClashSharpMode.Faulted) { currentMode = ClashSharpMode.Disabled; }
                    ClashSharpMode startupMode = StartupBehaviorService.ResolveStartupMode(
                        envelope.Desired[SettingsRegistry.Keys.StartupBehaviorMode].Value.Get<StartupBehaviorMode>(), currentMode);
                    bool tun = envelope.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>()
                        && startupMode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
                    bool applyNetwork = startupMode == ClashSharpMode.Disabled || !conflicts.HasBlockingConflicts(tun);
                    StartupStepResult activated = await runtime.InitializeAdmittedAsync(lease, applyNetwork, startupMode, token).ConfigureAwait(false);
                    SettingAppliedState mode = runtime.Repositories.Session.Snapshot.Applied[SettingsRegistry.Keys.CurrentMode];
                    if (applyNetwork && mode.Kind == SettingAppliedStateKind.Verified) { verifiedMode = mode.Value!.Get<ClashSharpMode>(); }
                    return activated;
                }, cancellationToken).ConfigureAwait(false);
                if (result.Outcome is StartupStepOutcome.Succeeded or StartupStepOutcome.Warning)
                {
                    if (_checkpoint is not null)
                    {
                        await replacementRecovery.CompleteAdmittedAsync(_checkpoint, lease, CancellationToken.None).ConfigureAwait(false);
                        _checkpoint = null;
                    }
                    try
                    {
                        await authority.PublishCurrentAdmittedAsync(lease, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception notificationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(notificationFailure))
                    {
                        if (result.Outcome == StartupStepOutcome.Succeeded) { result = StartupStepResult.Warning("startup-network-publication-failed"); }
                    }
                    if (verifiedMode is { } mode)
                    {
                        try { await publishMode(mode, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception notificationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(notificationFailure))
                        {
                            if (result.Outcome == StartupStepOutcome.Succeeded) { result = StartupStepResult.Warning("startup-network-publication-failed"); }
                        }
                    }
                }
                else if (_checkpoint is not null && result.Outcome == StartupStepOutcome.Fatal)
                {
                    await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false);
                if (ExceptionGraphClassifier.IsProcessFatal(failure) || ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken)) { throw; }
                result = StartupStepResult.Fatal("data-generation.recovery_required");
            }
        }
        if (result.Outcome == StartupStepOutcome.Fatal) { return result; }
        await generations.ExecuteAsync<AppDataGenerationRuntime>(
            (runtime, _, token) => runtime.AcknowledgeStartupReleaseAsync(token), CancellationToken.None).ConfigureAwait(false);
        return (StartupStepResult)(_activationResult = result);
    }
}
