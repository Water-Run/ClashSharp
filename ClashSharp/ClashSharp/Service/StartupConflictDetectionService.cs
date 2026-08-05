using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Host operations used by startup conflict detection and repair.</summary>
internal interface IStartupConflictEnvironment
{
    IReadOnlyList<StartupConflictProcess> GetExternalMihomoProcesses();

    IReadOnlyList<string> GetActiveTunInterfaces();

    bool IsTcpPortInUse(int port);

    WindowsProxyState GetWindowsProxyState();

    Task TerminateProcessAsync(
        StartupConflictProcess process,
        CancellationToken cancellationToken);

    Task DisableWindowsProxyAsync(CancellationToken cancellationToken);
}

/// <summary>Detects startup conflicts and exposes repair actions for each issue.</summary>
internal sealed class StartupConflictDetectionService
{
    public static StartupConflictDetectionService Instance { get; } = StartupConflictDetectionServiceFactory.CreateDefault();

    private readonly IStartupConflictEnvironment _environment;

    private readonly Func<string, string> _getString;

    public StartupConflictDetectionService(IStartupConflictEnvironment environment)
        : this(environment, key => key)
    {
    }

    public StartupConflictDetectionService(IStartupConflictEnvironment environment, Func<string, string> getString)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    public IReadOnlyList<StartupConflictIssue> CheckConflicts(int mixedPort)
    {
        return CheckConflicts(mixedPort, CancellationToken.None);
    }

    private IReadOnlyList<StartupConflictIssue> CheckConflicts(
        int mixedPort,
        CancellationToken cancellationToken)
    {
        if (mixedPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort), "Port must be in the range [1, 65535].");
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<StartupConflictIssue> issues = [];
        IReadOnlyList<StartupConflictProcess> processes = _environment.GetExternalMihomoProcesses();
        cancellationToken.ThrowIfCancellationRequested();
        if (processes.Count > 0)
        {
            issues.Add(new StartupConflictIssue(
                StartupConflictKind.ExternalMihomoProcess,
                _getString("StartupConflict.Mihomo.Title"),
                string.Format(CultureInfo.CurrentCulture, _getString("StartupConflict.Mihomo.Description"), processes.Count),
                _getString("StartupConflict.Mihomo.Repair"),
                token => TerminateExternalMihomoProcessesAsync(processes, token))
            {
                DiagnosticCode = "tun.conflict.external_mihomo",
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> activeTunInterfaces = _environment.GetActiveTunInterfaces();
        cancellationToken.ThrowIfCancellationRequested();
        if (activeTunInterfaces.Count > 0)
        {
            issues.Add(new StartupConflictIssue(
                StartupConflictKind.ActiveTunInterface,
                _getString("StartupConflict.Tun.Title"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    _getString("StartupConflict.Tun.Description"),
                    string.Join(", ", activeTunInterfaces)))
            {
                DiagnosticCode = RuntimeFailureDiagnostics.TunConflict,
            });
        }

        if (_environment.IsTcpPortInUse(mixedPort))
        {
            cancellationToken.ThrowIfCancellationRequested();
            issues.Add(new StartupConflictIssue(
                StartupConflictKind.MixedPortOccupied,
                _getString("StartupConflict.Port.Title"),
                string.Format(CultureInfo.CurrentCulture, _getString("StartupConflict.Port.Description"), mixedPort),
                _getString("StartupConflict.Port.Repair"),
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(new StartupConflictRepairResult(
                        false,
                        _getString("StartupConflict.Port.RepairFailed")));
                })
            {
                DiagnosticCode = RuntimeFailureDiagnostics.MixedPortOccupied,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        WindowsProxyState proxyState = _environment.GetWindowsProxyState();
        cancellationToken.ThrowIfCancellationRequested();
        if (proxyState.IsEnabled && !ProxyUsesTargetPort(proxyState.ProxyServer, mixedPort))
        {
            issues.Add(new StartupConflictIssue(
                StartupConflictKind.WindowsProxyWrongPort,
                _getString("StartupConflict.Proxy.Title"),
                string.Format(CultureInfo.CurrentCulture, _getString("StartupConflict.Proxy.Description"), proxyState.ProxyServer, mixedPort),
                _getString("StartupConflict.Proxy.Repair"),
                DisableWindowsProxyAsync)
            {
                DiagnosticCode = "route.system_proxy_mismatch",
            });
        }

        return issues;
    }

    /// <summary>Runs host process, socket, and registry probes away from the UI startup context.</summary>
    public Task<IReadOnlyList<StartupConflictIssue>> CheckConflictsAsync(
        int mixedPort,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<IReadOnlyList<StartupConflictIssue>> probeTask = Task.Run(
            () => CheckConflicts(mixedPort, cancellationToken),
            CancellationToken.None);
        return probeTask.WaitAsync(cancellationToken);
    }

    private async Task<StartupConflictRepairResult> TerminateExternalMihomoProcessesAsync(
        IReadOnlyList<StartupConflictProcess> processes,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (StartupConflictProcess process in processes)
            {
                await _environment.TerminateProcessAsync(process, cancellationToken);
            }

            return new StartupConflictRepairResult(true, _getString("StartupConflict.Mihomo.RepairSucceeded"));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            return new StartupConflictRepairResult(false, _getString("StartupConflict.Status.Failed"));
        }
    }

    private async Task<StartupConflictRepairResult> DisableWindowsProxyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _environment.DisableWindowsProxyAsync(cancellationToken);
            return new StartupConflictRepairResult(true, _getString("StartupConflict.Proxy.RepairSucceeded"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupConflictRepairResult(false, _getString("StartupConflict.Status.Failed"));
        }
    }

    private static bool ProxyUsesTargetPort(string proxyServer, int mixedPort)
    {
        return WindowsProxyEndpointMatcher.ContainsLoopbackEndpointWithPort(proxyServer, mixedPort);
    }

}
