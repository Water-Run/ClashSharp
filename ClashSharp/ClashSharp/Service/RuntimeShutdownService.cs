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

/// <summary>Coordinates best-effort runtime cleanup during application shutdown.</summary>
/// <remarks>
/// Invariants: Cleanup attempts stop sampling before stopping the core and restore Windows proxy only when configured.
/// Thread safety: Not thread-safe; intended for UI-thread shutdown execution.
/// Side effects: Stops runtime services, may mutate Windows proxy state, and may append warning logs.
/// </remarks>
internal static class RuntimeShutdownService
{
    /// <summary>Stops owned runtime services and restores Windows proxy state when configured.</summary>
    public static void Shutdown()
    {
        try
        {
            ConnectionSamplingService.Instance.Stop();
            MihomoCoreService.Instance.Stop();

            if (AppSettingsService.Instance.RestoreProxyOnExit)
            {
                WindowsProxyService.Instance.DisableProxy();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Shutdown", "Runtime shutdown cleanup failed.", exception.Message);
        }
    }
}
