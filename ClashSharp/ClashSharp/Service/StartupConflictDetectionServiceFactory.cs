/*
 * Startup Conflict Detection Service Factory
 * Wires production dependencies for startup conflict detection
 *
 * @author: WaterRun
 * @file: Service/StartupConflictDetectionServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
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
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (IsExpectedProcessTerminationException(exception))
        {
        }

        return Task.CompletedTask;
    }

    private static bool IsExpectedProcessTerminationException(Exception exception)
    {
        return exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException;
    }

    public Task DisableWindowsProxyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsProxyService.Instance.DisableProxy();
        return Task.CompletedTask;
    }
}
