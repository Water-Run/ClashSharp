/*
 * Startup Launch Service
 * Synchronizes user startup preference with the packaged Windows startup task
 *
 * @author: WaterRun
 * @file: Service/StartupLaunchService.cs
 * @date: 2026-06-17
 */

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClashSharp.Service;

/// <summary>Normalized startup task states used by launch synchronization.</summary>
internal enum StartupLaunchTaskState
{
    /// <summary>The packaged startup task is disabled and can request enablement.</summary>
    Disabled,

    /// <summary>The packaged startup task is enabled.</summary>
    Enabled,

    /// <summary>The packaged startup task is in another platform-specific state.</summary>
    Other,
}

/// <summary>Retrieves the packaged startup task from the platform boundary.</summary>
internal interface IStartupLaunchTaskProvider
{
    /// <summary>Gets the startup task identified by <paramref name="taskId"/>.</summary>
    Task<IStartupLaunchTask> GetAsync(string taskId);
}

/// <summary>Wraps the platform startup task operations used by settings.</summary>
internal interface IStartupLaunchTask
{
    /// <summary>Gets the normalized startup task state.</summary>
    StartupLaunchTaskState State { get; }

    /// <summary>Requests startup task enablement from Windows.</summary>
    Task RequestEnableAsync();

    /// <summary>Disables startup launch.</summary>
    void Disable();
}

/// <summary>Persists startup launch warning logs.</summary>
internal interface IStartupLaunchLog
{
    /// <summary>Appends a runtime log entry.</summary>
    void AppendLog(string level, string category, string message, string? detail);
}

/// <summary>Manages the packaged startup task used to launch Clash# when the user signs in.</summary>
internal sealed partial class StartupLaunchService
{
    /// <summary>Startup task identifier declared in Package.appxmanifest.</summary>
    public const string TaskId = "ClashSharpStartup";

    private readonly IStartupLaunchTaskProvider _taskProvider;

    private readonly IStartupLaunchLog _log;

    private readonly Func<string, string> _getString;

    internal StartupLaunchService(
        IStartupLaunchTaskProvider taskProvider,
        IStartupLaunchLog log,
        Func<string, string> getString)
    {
        _taskProvider = taskProvider ?? throw new ArgumentNullException(nameof(taskProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Requests the startup task state to match <paramref name="isEnabled"/>.</summary>
    public async Task SetEnabledAsync(bool isEnabled)
    {
        try
        {
            IStartupLaunchTask startupTask = await _taskProvider.GetAsync(TaskId).ConfigureAwait(false);
            if (isEnabled)
            {
                if (startupTask.State == StartupLaunchTaskState.Disabled)
                {
                    await startupTask.RequestEnableAsync().ConfigureAwait(false);
                }

                return;
            }

            if (startupTask.State == StartupLaunchTaskState.Enabled)
            {
                startupTask.Disable();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            _log.AppendLog("Warning", "StartupLaunch", GetString("StartupLaunch.UpdateFailed"), exception.Message);
        }
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
