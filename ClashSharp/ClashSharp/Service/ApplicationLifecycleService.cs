/*
 * Application Lifecycle Service
 * Provides service-bound exit and restart commands for UI surfaces
 *
 * @author: WaterRun
 * @file: Service/ApplicationLifecycleService.cs
 * @date: 2026-06-29
 */

using System;
using System.Diagnostics;

namespace ClashSharp.Service;

internal interface IApplicationLifecycleRuntimeShutdown
{
    void Shutdown();
}

internal interface IApplicationLifecycleProcessLauncher
{
    void Start(string executablePath);
}

internal interface IApplicationLifecycleExit
{
    void Exit();
}

internal sealed class ApplicationLifecycleService
{
    private readonly IApplicationLifecycleRuntimeShutdown _runtimeShutdown;
    private readonly IApplicationLifecycleProcessLauncher _processLauncher;
    private readonly IApplicationLifecycleExit _applicationExit;
    private readonly Func<string> _getExecutablePath;

    public ApplicationLifecycleService(
        IApplicationLifecycleRuntimeShutdown runtimeShutdown,
        IApplicationLifecycleProcessLauncher processLauncher,
        IApplicationLifecycleExit applicationExit,
        Func<string> getExecutablePath)
    {
        _runtimeShutdown = runtimeShutdown ?? throw new ArgumentNullException(nameof(runtimeShutdown));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _applicationExit = applicationExit ?? throw new ArgumentNullException(nameof(applicationExit));
        _getExecutablePath = getExecutablePath ?? throw new ArgumentNullException(nameof(getExecutablePath));
    }

    public static ApplicationLifecycleService Instance { get; } = new(
        new RuntimeShutdownAdapter(),
        new ProcessLauncher(),
        new EnvironmentApplicationExit(),
        ResolveExecutablePath);

    public void ExitApplication()
    {
        _runtimeShutdown.Shutdown();
        _applicationExit.Exit();
    }

    public void RestartApplication()
    {
        string executablePath = _getExecutablePath();
        _runtimeShutdown.Shutdown();
        _processLauncher.Start(executablePath);
        _applicationExit.Exit();
    }

    private static string ResolveExecutablePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "ClashSharp.exe";
    }

    private sealed class RuntimeShutdownAdapter : IApplicationLifecycleRuntimeShutdown
    {
        public void Shutdown()
        {
            RuntimeShutdownService.Shutdown();
        }
    }

    private sealed class ProcessLauncher : IApplicationLifecycleProcessLauncher
    {
        public void Start(string executablePath)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
            });
        }
    }

    private sealed class EnvironmentApplicationExit : IApplicationLifecycleExit
    {
        public void Exit()
        {
            Environment.Exit(0);
        }
    }
}
