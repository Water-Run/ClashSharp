using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace ClashSharp.Service;

/// <summary>Creates startup launch services with production dependencies.</summary>
internal static class StartupLaunchServiceFactory
{
    /// <summary>Creates the default startup launch service used by settings.</summary>
    public static StartupLaunchService CreateDefault()
    {
        return new StartupLaunchService(
            new StartupLaunchTaskProvider(),
            new StartupLaunchLogAdapter(LogStorageService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class StartupLaunchTaskProvider : IStartupLaunchTaskProvider
{
    public async Task<IStartupLaunchTask> GetAsync(string taskId)
    {
        StartupTask task = await StartupTask.GetAsync(taskId);
        return new StartupLaunchTaskAdapter(task);
    }
}

internal sealed class StartupLaunchTaskAdapter(StartupTask task) : IStartupLaunchTask
{
    public StartupLaunchTaskState State => NormalizeState(task.State);

    public async Task<StartupLaunchTaskState> RequestEnableAsync()
    {
        StartupTaskState state = await task.RequestEnableAsync();
        return NormalizeState(state);
    }

    public void Disable()
    {
        task.Disable();
    }

    private static StartupLaunchTaskState NormalizeState(StartupTaskState state)
    {
        return state switch
        {
            StartupTaskState.Disabled => StartupLaunchTaskState.Disabled,
            StartupTaskState.Enabled => StartupLaunchTaskState.Enabled,
            _ => StartupLaunchTaskState.Other,
        };
    }
}

internal sealed class StartupLaunchLogAdapter(LogStorageService logStorage) : IStartupLaunchLog
{
    public void AppendLog(string level, string category, string message, string? detail)
    {
        logStorage.AppendLog(level, category, message, detail);
    }
}
