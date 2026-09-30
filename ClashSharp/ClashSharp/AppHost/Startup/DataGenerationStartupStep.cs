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
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Opens the complete generation, installs its lifecycle owners and verifies startup settings before normal consumers run.</summary>
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
    public string Name => "data-generation";
    public int Order => 225;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
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
                GenerationReplacementCheckpoint? checkpoint = await replacementRecovery.PrepareAdmittedAsync(lease, cancellationToken).ConfigureAwait(false);
                result = await generations.ExecuteAsync<AppDataGenerationRuntime, StartupStepResult>(
                    (runtime, _, token) => runtime.InitializeAdmittedAsync(lease, token), cancellationToken).ConfigureAwait(false);
                if (checkpoint is not null)
                {
                    if (result.Outcome is StartupStepOutcome.Succeeded or StartupStepOutcome.Warning)
                    {
                        await replacementRecovery.CompleteAdmittedAsync(checkpoint, lease, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (result.Outcome == StartupStepOutcome.Fatal) { await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false); }
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
        return result;
    }
}
