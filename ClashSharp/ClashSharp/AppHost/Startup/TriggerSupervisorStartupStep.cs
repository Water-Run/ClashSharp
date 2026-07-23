using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Triggers;

namespace ClashSharp.Hosting.Startup;

/// <summary>Initializes durable trigger state before starting primary-process scheduling.</summary>
internal interface ITriggerStartupInitializer
{
    Task<StartupStepResult> InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>Starts trigger scheduling only after migration and outbox reconciliation complete.</summary>
internal sealed class TriggerSupervisorStartupStep(
    ITriggerStartupInitializer initializer,
    TriggerScheduler scheduler) : IStartupStep
{
    public string Name => "trigger-supervisor";

    public int Order => 500;

    public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        StartupStepResult initialized;
        try
        {
            initialized = await initializer
                .InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StartupStepResult.Fatal("trigger.startup.initialization_failed");
        }

        if (initialized.Outcome is StartupStepOutcome.Fatal or StartupStepOutcome.ExitRequested)
        {
            return initialized;
        }

        try
        {
            await scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
            return initialized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StartupStepResult.Fatal("trigger.scheduler.start_failed");
        }
    }
}
