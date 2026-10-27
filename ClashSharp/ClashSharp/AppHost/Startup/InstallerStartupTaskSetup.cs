using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace ClashSharp.Hosting.Startup;

internal interface IInstallerStartupTask
{
    StartupTaskState State { get; }
    void Disable();
    Task EnableAsync();
}

/// <summary>Applies a selected login state without overriding a user's refusal or Windows policy.</summary>
internal static class InstallerStartupTaskSetup
{
    internal static async Task<bool> ApplyAsync(bool enabled, Func<Task<IInstallerStartupTask>> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IInstallerStartupTask task = await read();
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled && task.State == StartupTaskState.Disabled) { await task.EnableAsync(); }
        else if (!enabled && task.State == StartupTaskState.Enabled) { task.Disable(); }
        cancellationToken.ThrowIfCancellationRequested();
        IInstallerStartupTask observed = await read();
        return enabled ? observed.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy
            : observed.State is StartupTaskState.Disabled or StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
    }
}
