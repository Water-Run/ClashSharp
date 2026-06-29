/*
 * Single Instance Service
 * Detects and closes duplicate Clash# desktop processes through an injectable environment boundary
 *
 * @author: WaterRun
 * @file: Service/SingleInstanceService.cs
 * @date: 2026-06-29
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashSharp.Service;

internal sealed record SingleInstanceProcessInfo(int ProcessId, string ProcessName, string ExecutablePath);

internal sealed record SingleInstanceCheckResult(bool HasOtherInstance, SingleInstanceProcessInfo? Process);

internal sealed record SingleInstanceCloseResult(bool WasClosed, string? ErrorMessage = null);

internal interface ISingleInstanceEnvironment
{
    int CurrentProcessId { get; }

    string CurrentExecutablePath { get; }

    IReadOnlyList<SingleInstanceProcessInfo> GetProcesses();

    void CloseProcess(int processId);
}

internal sealed class SingleInstanceService
{
    private readonly ISingleInstanceEnvironment _environment;

    public SingleInstanceService(ISingleInstanceEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public static SingleInstanceService Instance { get; } = new(new DefaultSingleInstanceEnvironment());

    public SingleInstanceCheckResult CheckForOtherInstance()
    {
        string currentPath = NormalizePath(_environment.CurrentExecutablePath);
        SingleInstanceProcessInfo? process = _environment
            .GetProcesses()
            .Where(process => process.ProcessId != _environment.CurrentProcessId)
            .FirstOrDefault(process => IsSameExecutable(currentPath, process.ExecutablePath));

        return new SingleInstanceCheckResult(process is not null, process);
    }

    public SingleInstanceCloseResult CloseOtherInstance(SingleInstanceCheckResult result)
    {
        if (!result.HasOtherInstance || result.Process is null)
        {
            return new SingleInstanceCloseResult(false);
        }

        try
        {
            _environment.CloseProcess(result.Process.ProcessId);
            return new SingleInstanceCloseResult(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new SingleInstanceCloseResult(false, exception.Message);
        }
    }

    private static bool IsSameExecutable(string currentPath, string candidatePath)
    {
        string normalizedCandidate = NormalizePath(candidatePath);
        return !string.IsNullOrWhiteSpace(currentPath)
            && string.Equals(currentPath, normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.Trim();
        }
    }

    private sealed class DefaultSingleInstanceEnvironment : ISingleInstanceEnvironment
    {
        public int CurrentProcessId => Environment.ProcessId;

        public string CurrentExecutablePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

        public IReadOnlyList<SingleInstanceProcessInfo> GetProcesses()
        {
            List<SingleInstanceProcessInfo> processes = [];
            string currentName = Process.GetCurrentProcess().ProcessName;
            foreach (Process process in Process.GetProcessesByName(currentName))
            {
                using (process)
                {
                    try
                    {
                        processes.Add(new SingleInstanceProcessInfo(
                            process.Id,
                            process.ProcessName,
                            process.MainModule?.FileName ?? string.Empty));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
                    {
                    }
                }
            }

            return processes;
        }

        public void CloseProcess(int processId)
        {
            using Process process = Process.GetProcessById(processId);
            if (process.CloseMainWindow() && process.WaitForExit(3000))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
    }
}
