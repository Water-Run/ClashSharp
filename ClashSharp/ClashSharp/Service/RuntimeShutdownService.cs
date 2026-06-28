/*
 * Runtime Shutdown Service
 * Coordinates runtime service cleanup when the application window closes
 *
 * @author: WaterRun
 * @file: Service/RuntimeShutdownService.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.ComponentModel;

namespace ClashSharp.Service;

/// <summary>Controls background connection sampling during shutdown.</summary>
internal interface IRuntimeShutdownSampling
{
    /// <summary>Stops connection sampling.</summary>
    void Stop();
}

/// <summary>Controls the owned mihomo core process during shutdown.</summary>
internal interface IRuntimeShutdownCore
{
    /// <summary>Stops the mihomo core process.</summary>
    void Stop();
}

/// <summary>Provides shutdown cleanup settings.</summary>
internal interface IRuntimeShutdownSettings
{
    /// <summary>Gets whether Windows proxy should be restored on application exit.</summary>
    bool RestoreProxyOnExit { get; }
}

/// <summary>Mutates Windows proxy state during shutdown.</summary>
internal interface IRuntimeShutdownWindowsProxy
{
    /// <summary>Disables Windows system proxy.</summary>
    void DisableProxy();
}

/// <summary>Persists runtime shutdown warning logs.</summary>
internal interface IRuntimeShutdownLog
{
    /// <summary>Appends a runtime log entry.</summary>
    void AppendLog(string level, string category, string message, string? detail);
}

/// <summary>Coordinates best-effort runtime cleanup during application shutdown.</summary>
/// <remarks>
/// Invariants: Cleanup attempts stop sampling before stopping the core and restore Windows proxy only when configured.
/// Thread safety: Not thread-safe; intended for UI-thread shutdown execution.
/// Side effects: Stops runtime services, may mutate Windows proxy state, and may append warning logs.
/// </remarks>
internal sealed partial class RuntimeShutdownService
{
    private readonly IRuntimeShutdownSampling _sampling;

    private readonly IRuntimeShutdownCore _core;

    private readonly IRuntimeShutdownSettings _settings;

    private readonly IRuntimeShutdownWindowsProxy _windowsProxy;

    private readonly IRuntimeShutdownLog _log;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes a runtime shutdown service with explicit dependencies.</summary>
    internal RuntimeShutdownService(
        IRuntimeShutdownSampling sampling,
        IRuntimeShutdownCore core,
        IRuntimeShutdownSettings settings,
        IRuntimeShutdownWindowsProxy windowsProxy,
        IRuntimeShutdownLog log,
        Func<string, string> getString)
    {
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Stops owned runtime services and restores Windows proxy state when configured.</summary>
    public void ShutdownRuntime()
    {
        TryCleanupStep(_sampling.Stop);
        TryCleanupStep(_core.Stop);

        if (_settings.RestoreProxyOnExit)
        {
            TryCleanupStep(_windowsProxy.DisableProxy);
        }
    }

    private void TryCleanupStep(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            _log.AppendLog("Warning", "Shutdown", GetString("RuntimeShutdown.CleanupFailed"), exception.Message);
        }
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
