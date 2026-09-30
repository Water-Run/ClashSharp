using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
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
    AppSettingsService settings) : IStartupStep
{
    private bool _registered;
    public string Name => "data-generation";
    public int Order => 225;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        StartupStepResult result;
        await using (MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false))
        {
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
            result = await generations.ExecuteAsync<AppDataGenerationRuntime, StartupStepResult>(
                (runtime, _, token) => runtime.InitializeAdmittedAsync(lease, token), cancellationToken).ConfigureAwait(false);
        }
        await generations.ExecuteAsync<AppDataGenerationRuntime>(
            (runtime, _, token) => runtime.AcknowledgeStartupReleaseAsync(token), CancellationToken.None).ConfigureAwait(false);
        return result;
    }
}
