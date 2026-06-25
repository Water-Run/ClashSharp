/*
 * Runtime Shutdown Service Factory
 * Wires production dependencies for application shutdown cleanup
 *
 * @author: WaterRun
 * @file: Service/RuntimeShutdownServiceFactory.cs
 * @date: 2026-06-25
 */

namespace ClashSharp.Service;

internal sealed partial class RuntimeShutdownService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    public static RuntimeShutdownService Instance { get; } = RuntimeShutdownServiceFactory.CreateDefault();

    /// <summary>Stops owned runtime services and restores Windows proxy state when configured.</summary>
    public static void Shutdown()
    {
        Instance.ShutdownRuntime();
    }
}

/// <summary>Creates runtime shutdown services with production dependencies.</summary>
internal static class RuntimeShutdownServiceFactory
{
    /// <summary>Creates the default service used by application close and data cleanup paths.</summary>
    public static RuntimeShutdownService CreateDefault()
    {
        return new RuntimeShutdownService(
            new RuntimeShutdownSamplingAdapter(ConnectionSamplingService.Instance),
            new RuntimeShutdownCoreAdapter(MihomoCoreService.Instance),
            new RuntimeShutdownSettingsAdapter(AppSettingsService.Instance),
            new RuntimeShutdownWindowsProxyAdapter(WindowsProxyService.Instance),
            new RuntimeShutdownLogAdapter(LogStorageService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class RuntimeShutdownSamplingAdapter(ConnectionSamplingService sampling) : IRuntimeShutdownSampling
{
    public void Stop()
    {
        sampling.Stop();
    }
}

internal sealed class RuntimeShutdownCoreAdapter(MihomoCoreService core) : IRuntimeShutdownCore
{
    public void Stop()
    {
        core.Stop();
    }
}

internal sealed class RuntimeShutdownSettingsAdapter(AppSettingsService settings) : IRuntimeShutdownSettings
{
    public bool RestoreProxyOnExit => settings.RestoreProxyOnExit;
}

internal sealed class RuntimeShutdownWindowsProxyAdapter(WindowsProxyService windowsProxy) : IRuntimeShutdownWindowsProxy
{
    public void DisableProxy()
    {
        windowsProxy.DisableProxy();
    }
}

internal sealed class RuntimeShutdownLogAdapter(LogStorageService logStorage) : IRuntimeShutdownLog
{
    public void AppendLog(string level, string category, string message, string? detail)
    {
        logStorage.AppendLog(level, category, message, detail);
    }
}
