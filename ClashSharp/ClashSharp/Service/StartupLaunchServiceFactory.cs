/*
 * Startup Launch Service Factory
 * Wires production dependencies for packaged startup task synchronization
 *
 * @author: WaterRun
 * @file: Service/StartupLaunchServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace ClashSharp.Service;

internal sealed partial class StartupLaunchService
{
    /// <summary>Shared singleton instance.</summary>
    public static StartupLaunchService Instance { get; } = StartupLaunchServiceFactory.CreateDefault();
}

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
    public StartupLaunchTaskState State => task.State switch
    {
        StartupTaskState.Disabled => StartupLaunchTaskState.Disabled,
        StartupTaskState.Enabled => StartupLaunchTaskState.Enabled,
        _ => StartupLaunchTaskState.Other,
    };

    public async Task RequestEnableAsync()
    {
        await task.RequestEnableAsync();
    }

    public void Disable()
    {
        task.Disable();
    }
}

internal sealed class StartupLaunchLogAdapter(LogStorageService logStorage) : IStartupLaunchLog
{
    public void AppendLog(string level, string category, string message, string? detail)
    {
        logStorage.AppendLog(level, category, message, detail);
    }
}
