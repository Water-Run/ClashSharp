using System;
using System.Runtime.InteropServices;
using System.Threading;
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
    Task<StartupLaunchTaskState> RequestEnableAsync();

    /// <summary>Disables startup launch.</summary>
    void Disable();
}

/// <summary>Classifies a failed startup-launch update without losing the platform failure.</summary>
internal enum StartupLaunchUpdateFailure
{
    PlatformFailure,
    EnableDenied,
    UnsupportedState,
    VerificationFailed,
}

/// <summary>Reports that Windows did not accept or verify a requested startup-launch update.</summary>
internal sealed class StartupLaunchUpdateException : InvalidOperationException
{
    public StartupLaunchUpdateException(
        bool desiredEnabled,
        StartupLaunchUpdateFailure failure,
        StartupLaunchTaskState? observedState,
        Exception? innerException = null)
        : base(
            $"Startup launch could not be set to '{desiredEnabled}'. " +
            $"Failure: '{failure}', observed state: '{observedState?.ToString() ?? "Unavailable"}'.",
            innerException)
    {
        DesiredEnabled = desiredEnabled;
        Failure = failure;
        ObservedState = observedState;
    }

    public bool DesiredEnabled { get; }

    public StartupLaunchUpdateFailure Failure { get; }

    public StartupLaunchTaskState? ObservedState { get; }
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

    /// <summary>Reads the actual packaged startup-task state for durable action reconciliation.</summary>
    /// <returns>The observed state, or null when the platform state cannot be established.</returns>
    public async Task<StartupLaunchTaskState?> TryGetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IStartupLaunchTask startupTask = await _taskProvider.GetAsync(TaskId).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return startupTask.State;
        }
        catch (Exception exception) when (IsPlatformFailure(exception))
        {
            AppendUpdateFailure(exception);
            return null;
        }
    }

    /// <summary>Requests the startup task state to match <paramref name="isEnabled"/>.</summary>
    public async Task SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IStartupLaunchTask startupTask = await _taskProvider.GetAsync(TaskId).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (startupTask.State == StartupLaunchTaskState.Other)
            {
                throw CreateUpdateException(
                    isEnabled,
                    StartupLaunchUpdateFailure.UnsupportedState,
                    startupTask.State);
            }

            if (isEnabled)
            {
                if (startupTask.State == StartupLaunchTaskState.Disabled)
                {
                    StartupLaunchTaskState requestedState = await startupTask
                        .RequestEnableAsync()
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (requestedState == StartupLaunchTaskState.Disabled)
                    {
                        throw CreateUpdateException(
                            isEnabled,
                            StartupLaunchUpdateFailure.EnableDenied,
                            requestedState);
                    }

                    if (requestedState == StartupLaunchTaskState.Other)
                    {
                        throw CreateUpdateException(
                            isEnabled,
                            StartupLaunchUpdateFailure.UnsupportedState,
                            requestedState);
                    }

                    IStartupLaunchTask verifiedTask = await _taskProvider
                        .GetAsync(TaskId)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (verifiedTask.State != StartupLaunchTaskState.Enabled)
                    {
                        throw CreateUpdateException(
                            isEnabled,
                            StartupLaunchUpdateFailure.VerificationFailed,
                            verifiedTask.State);
                    }
                }

                return;
            }

            if (startupTask.State == StartupLaunchTaskState.Enabled)
            {
                startupTask.Disable();
                cancellationToken.ThrowIfCancellationRequested();
                IStartupLaunchTask verifiedTask = await _taskProvider
                    .GetAsync(TaskId)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (verifiedTask.State != StartupLaunchTaskState.Disabled)
                {
                    throw CreateUpdateException(
                        isEnabled,
                        StartupLaunchUpdateFailure.VerificationFailed,
                        verifiedTask.State);
                }
            }
        }
        catch (StartupLaunchUpdateException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsPlatformFailure(exception))
        {
            throw CreateUpdateException(
                isEnabled,
                StartupLaunchUpdateFailure.PlatformFailure,
                observedState: null,
                exception);
        }
    }

    private static bool IsPlatformFailure(Exception exception)
    {
        return exception is InvalidOperationException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            COMException;
    }

    private StartupLaunchUpdateException CreateUpdateException(
        bool desiredEnabled,
        StartupLaunchUpdateFailure failure,
        StartupLaunchTaskState? observedState,
        Exception? innerException = null)
    {
        StartupLaunchUpdateException exception = new(
            desiredEnabled,
            failure,
            observedState,
            innerException);
        AppendUpdateFailure(innerException ?? exception);
        return exception;
    }

    private void AppendUpdateFailure(Exception exception)
    {
        _log.AppendLog("Warning", "StartupLaunch", GetString("StartupLaunch.UpdateFailed"), exception.Message);
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
