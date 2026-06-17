/*
 * Startup Conflict Detection Service
 * Detects host proxy conflicts before Clash# applies startup network takeover
 *
 * @author: WaterRun
 * @file: Service/StartupConflictDetectionService.cs
 * @date: 2026-06-17
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Startup conflict categories shown in the startup check dialog.</summary>
internal enum StartupConflictKind
{
    /// <summary>An external mihomo process is already running.</summary>
    ExternalMihomoProcess,

    /// <summary>The configured mixed proxy port is occupied.</summary>
    MixedPortOccupied,

    /// <summary>Windows manual proxy is enabled but points to a different port.</summary>
    WindowsProxyWrongPort,
}

/// <summary>External process snapshot used by startup conflict checks.</summary>
internal readonly record struct StartupConflictProcess(int ProcessId, string ProcessName);

/// <summary>Result returned after attempting to repair a startup conflict.</summary>
internal readonly record struct StartupConflictRepairResult(bool Succeeded, string Message);

/// <summary>A detected startup conflict and its repair action.</summary>
internal sealed class StartupConflictIssue
{
    public StartupConflictIssue(
        StartupConflictKind kind,
        string title,
        string description,
        string repairText,
        Func<CancellationToken, Task<StartupConflictRepairResult>> repairAsync)
    {
        Kind = kind;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        RepairText = repairText ?? throw new ArgumentNullException(nameof(repairText));
        RepairAsync = repairAsync ?? throw new ArgumentNullException(nameof(repairAsync));
    }

    public StartupConflictKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    public string RepairText { get; }

    public Func<CancellationToken, Task<StartupConflictRepairResult>> RepairAsync { get; }
}

/// <summary>Host operations used by startup conflict detection and repair.</summary>
internal interface IStartupConflictEnvironment
{
    IReadOnlyList<StartupConflictProcess> GetExternalMihomoProcesses();

    bool IsTcpPortInUse(int port);

    WindowsProxyState GetWindowsProxyState();

    Task TerminateProcessAsync(int processId, CancellationToken cancellationToken);

    Task DisableWindowsProxyAsync(CancellationToken cancellationToken);
}

/// <summary>Detects startup conflicts and exposes repair actions for each issue.</summary>
internal sealed class StartupConflictDetectionService
{
    public static StartupConflictDetectionService Instance { get; } = new(new DefaultStartupConflictEnvironment());

    private readonly IStartupConflictEnvironment _environment;

    public StartupConflictDetectionService(IStartupConflictEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public IReadOnlyList<StartupConflictIssue> CheckConflicts(int mixedPort)
    {
        if (mixedPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort), "Port must be in the range [1, 65535].");
        }

        List<StartupConflictIssue> issues = [];
        IReadOnlyList<StartupConflictProcess> processes = _environment.GetExternalMihomoProcesses();
        if (processes.Count > 0)
        {
            issues.Add(new StartupConflictIssue(
                StartupConflictKind.ExternalMihomoProcess,
                LocalizationService.Instance.GetString("StartupConflict.Mihomo.Title"),
                string.Format(LocalizationService.Instance.GetString("StartupConflict.Mihomo.Description"), processes.Count),
                LocalizationService.Instance.GetString("StartupConflict.Mihomo.Repair"),
                token => TerminateExternalMihomoProcessesAsync(processes, token)));
        }

        if (_environment.IsTcpPortInUse(mixedPort))
        {
            issues.Add(new StartupConflictIssue(
                StartupConflictKind.MixedPortOccupied,
                LocalizationService.Instance.GetString("StartupConflict.Port.Title"),
                string.Format(LocalizationService.Instance.GetString("StartupConflict.Port.Description"), mixedPort),
                LocalizationService.Instance.GetString("StartupConflict.Port.Repair"),
                _ => Task.FromResult(new StartupConflictRepairResult(
                    false,
                    LocalizationService.Instance.GetString("StartupConflict.Port.RepairFailed")))));
        }

        WindowsProxyState proxyState = _environment.GetWindowsProxyState();
        if (proxyState.IsEnabled && !ProxyUsesTargetPort(proxyState.ProxyServer, mixedPort))
        {
            issues.Add(new StartupConflictIssue(
                StartupConflictKind.WindowsProxyWrongPort,
                LocalizationService.Instance.GetString("StartupConflict.Proxy.Title"),
                string.Format(LocalizationService.Instance.GetString("StartupConflict.Proxy.Description"), proxyState.ProxyServer, mixedPort),
                LocalizationService.Instance.GetString("StartupConflict.Proxy.Repair"),
                DisableWindowsProxyAsync));
        }

        return issues;
    }

    private async Task<StartupConflictRepairResult> TerminateExternalMihomoProcessesAsync(
        IReadOnlyList<StartupConflictProcess> processes,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (StartupConflictProcess process in processes)
            {
                await _environment.TerminateProcessAsync(process.ProcessId, cancellationToken);
            }

            return new StartupConflictRepairResult(true, LocalizationService.Instance.GetString("StartupConflict.Mihomo.RepairSucceeded"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupConflictRepairResult(false, exception.Message);
        }
    }

    private async Task<StartupConflictRepairResult> DisableWindowsProxyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _environment.DisableWindowsProxyAsync(cancellationToken);
            return new StartupConflictRepairResult(true, LocalizationService.Instance.GetString("StartupConflict.Proxy.RepairSucceeded"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupConflictRepairResult(false, exception.Message);
        }
    }

    private static bool ProxyUsesTargetPort(string proxyServer, int mixedPort)
    {
        return proxyServer.Contains($":{mixedPort}", StringComparison.OrdinalIgnoreCase)
            && (proxyServer.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || proxyServer.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DefaultStartupConflictEnvironment : IStartupConflictEnvironment
    {
        public IReadOnlyList<StartupConflictProcess> GetExternalMihomoProcesses()
        {
            int currentProcessId = Environment.ProcessId;
            List<StartupConflictProcess> processes = [];
            foreach (Process process in Process.GetProcessesByName("mihomo"))
            {
                using (process)
                {
                    if (process.Id != currentProcessId)
                    {
                        processes.Add(new StartupConflictProcess(process.Id, process.ProcessName));
                    }
                }
            }

            return processes;
        }

        public bool IsTcpPortInUse(int port)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
            finally
            {
                listener?.Stop();
            }
        }

        public WindowsProxyState GetWindowsProxyState()
        {
            return WindowsProxyService.Instance.GetCurrentState();
        }

        public Task TerminateProcessAsync(int processId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return Task.CompletedTask;
        }

        public Task DisableWindowsProxyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsProxyService.Instance.DisableProxy();
            return Task.CompletedTask;
        }
    }
}
