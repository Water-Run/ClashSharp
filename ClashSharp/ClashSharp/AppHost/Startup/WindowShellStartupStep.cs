using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Unlocks the already-visible startup shell after mutation and trigger readiness.</summary>
internal sealed class WindowShellStartupStep(
    Action<MainWindowStartupContext> completeWindow,
    ITriggerRuntimeEventPublisher triggerEvents,
    ApplicationActionService actions,
    ApplicationLifecycleService lifecycle,
    TrayCommandService trayCommands,
    StartupConflictSnapshot startupConflicts) : IStartupStep
{
    public string Name => "window-shell";

    public int Order => 600;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        completeWindow(new MainWindowStartupContext(
            triggerEvents,
            actions,
            lifecycle,
            trayCommands,
            startupConflicts));
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}

/// <summary>Runtime-only dependencies supplied when the startup shell becomes interactive.</summary>
internal sealed record MainWindowStartupContext(
    ITriggerRuntimeEventPublisher TriggerEvents,
    ApplicationActionService Actions,
    ApplicationLifecycleService Lifecycle,
    TrayCommandService TrayCommands,
    StartupConflictSnapshot StartupConflicts);
