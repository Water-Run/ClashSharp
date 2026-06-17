/*
 * Startup Launch Service
 * Synchronizes user startup preference with the packaged Windows startup task
 *
 * @author: WaterRun
 * @file: Service/StartupLaunchService.cs
 * @date: 2026-06-17
 */

using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace ClashSharp.Service;

/// <summary>Manages the packaged startup task used to launch Clash# when the user signs in.</summary>
internal sealed class StartupLaunchService
{
    /// <summary>Startup task identifier declared in Package.appxmanifest.</summary>
    public const string TaskId = "ClashSharpStartup";

    /// <summary>Shared singleton instance.</summary>
    public static StartupLaunchService Instance { get; } = new();

    private StartupLaunchService()
    {
    }

    /// <summary>Requests the startup task state to match <paramref name="isEnabled"/>.</summary>
    public async Task SetEnabledAsync(bool isEnabled)
    {
        try
        {
            StartupTask startupTask = await StartupTask.GetAsync(TaskId);
            if (isEnabled)
            {
                if (startupTask.State == StartupTaskState.Disabled)
                {
                    await startupTask.RequestEnableAsync();
                }

                return;
            }

            if (startupTask.State == StartupTaskState.Enabled)
            {
                startupTask.Disable();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            LogStorageService.Instance.AppendLog("Warning", "StartupLaunch", "Failed to update launch-at-startup setting.", exception.Message);
        }
    }
}
