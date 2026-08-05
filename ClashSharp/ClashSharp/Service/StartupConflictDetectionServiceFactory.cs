using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Creates startup conflict detection service instances with production dependencies.</summary>
internal static class StartupConflictDetectionServiceFactory
{
    /// <summary>Creates the default startup conflict detection service used by the application singleton.</summary>
    /// <returns>A startup conflict detection service wired to host APIs and localization resources.</returns>
    public static StartupConflictDetectionService CreateDefault()
    {
        return new StartupConflictDetectionService(
            new DefaultStartupConflictEnvironment(),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class DefaultStartupConflictEnvironment : IStartupConflictEnvironment
{
    private static readonly string[] KnownTunInterfaceMarkers =
    [
        "wireguard",
        "tailscale",
        "zerotier",
        "openvpn",
        "tap-windows",
        "cloudflare warp",
        "sing-box",
    ];

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
                    long startTimeUtcTicks;
                    try
                    {
                        startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch (Exception exception) when (IsExpectedProcessInspectionException(exception))
                    {
                        startTimeUtcTicks = 0;
                    }

                    processes.Add(new StartupConflictProcess(
                        process.Id,
                        process.ProcessName,
                        startTimeUtcTicks));
                }
            }
        }

        return processes;
    }

    public IReadOnlyList<string> GetActiveTunInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(static network => network.OperationalStatus == OperationalStatus.Up)
                .Where(network => IsKnownTunInterface(network.Name, network.Description))
                .Select(static network => NormalizeInterfaceName(network.Name))
                .Where(static name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.CurrentCultureIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool IsKnownTunInterface(string name, string description)
    {
        return KnownTunInterfaceMarkers.Any(marker =>
            name.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeInterfaceName(string name)
    {
        string normalized = string.Concat(name.Trim().Take(128).Where(static character => !char.IsControl(character)));
        return normalized;
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

    public async Task TerminateProcessAsync(
        StartupConflictProcess expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected.StartTimeUtcTicks <= 0)
        {
            throw new InvalidOperationException("The process identity snapshot is incomplete.");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return;
            }

            string actualName = process.ProcessName;
            long actualStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            if (!MatchesProcessIdentity(expected, actualName, actualStartTimeUtcTicks))
            {
                throw new InvalidOperationException(
                    "The process identity changed before the requested repair.");
            }

            process.Kill();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool MatchesProcessIdentity(
        StartupConflictProcess expected,
        string actualProcessName,
        long actualStartTimeUtcTicks)
    {
        return expected.ProcessId > 0
            && expected.StartTimeUtcTicks > 0
            && StringComparer.OrdinalIgnoreCase.Equals(expected.ProcessName, "mihomo")
            && StringComparer.OrdinalIgnoreCase.Equals(actualProcessName, expected.ProcessName)
            && actualStartTimeUtcTicks == expected.StartTimeUtcTicks;
    }

    private static bool IsExpectedProcessInspectionException(Exception exception)
    {
        return exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException;
    }

    public Task DisableWindowsProxyAsync(CancellationToken cancellationToken)
    {
#if UNIT_TESTS
        throw new NotSupportedException("Use an injected startup conflict environment in tests.");
#else
        return ApplicationActionService.Instance.DisableWindowsProxyAsync(cancellationToken);
#endif
    }
}
