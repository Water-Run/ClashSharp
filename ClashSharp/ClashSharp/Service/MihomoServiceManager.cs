/*
 * Mihomo Service Manager
 * Manages the optional Windows service used as transparent proxy prerequisite
 *
 * @author: WaterRun
 * @file: Service/MihomoServiceManager.cs
 * @date: 2026-06-24
 */

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Manages the optional Windows service used as transparent proxy prerequisite.</summary>
/// <remarks>
/// Invariants: Service state is read from Windows Service Control Manager.
/// Thread safety: Stateless methods are safe for concurrent calls.
/// Side effects: May start elevated sc.exe processes for deployment and removal.
/// </remarks>
public sealed class MihomoServiceManager
{
    /// <summary>Windows service name.</summary>
    public const string ServiceName = "ClashSharpMihomo";

    /// <summary>Windows service display name.</summary>
    private const string ServiceDisplayName = "Clash# Mihomo Service";

    /// <summary>Shared singleton instance.</summary>
    /// <value>A non-null service manager.</value>
    public static MihomoServiceManager Instance { get; } = new();

    /// <summary>Initializes the service manager.</summary>
    private MihomoServiceManager()
    {
    }

    /// <summary>Gets current Windows service status.</summary>
    /// <returns>Service deployment status.</returns>
    public MihomoServiceStatus GetStatus()
    {
        ProcessResult result = RunSc("query", ServiceName);
        if (result.ExitCode != 0)
        {
            return new MihomoServiceStatus(false, false, LocalizationService.Instance.GetString("MihomoService.Status.NotDeployed"));
        }

        bool isRunning = result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        return new MihomoServiceStatus(
            true,
            isRunning,
            isRunning
                ? LocalizationService.Instance.GetString("MihomoService.Status.DeployedRunning")
                : LocalizationService.Instance.GetString("MihomoService.Status.Deployed"));
    }

    /// <summary>Deploys the Windows service when a service host is available.</summary>
    /// <param name="cancellationToken">Cancels waiting for sc.exe when requested.</param>
    /// <returns>Updated service status or failure status.</returns>
    public async Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken)
    {
        MihomoServiceStatus current = GetStatus();
        if (current.IsInstalled)
        {
            return current;
        }

        string? serviceHostPath = ResolveServiceHostPath();
        if (serviceHostPath is null)
        {
            return new MihomoServiceStatus(false, false, LocalizationService.Instance.GetString("MihomoService.Status.HostMissing"));
        }

        string mihomoPath = MihomoCoreService.Instance.BinaryPath;
        string configPath = CoreConfigurationService.Instance.EnsureConfiguration(
            AppSettingsService.Instance.CurrentMode,
            transparentProxyEnabled: true).ConfigPath;
        string workDirectory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        string binPath = Quote(serviceHostPath)
            + " --mihomo " + Quote(mihomoPath)
            + " --config " + Quote(configPath)
            + " --workdir " + Quote(workDirectory);

        ProcessResult createResult = await RunScElevatedAsync(
            cancellationToken,
            "create",
            ServiceName,
            "binPath=",
            binPath,
            "start=",
            "demand",
            "DisplayName=",
            ServiceDisplayName).ConfigureAwait(false);

        if (createResult.ExitCode != 0)
        {
            return new MihomoServiceStatus(false, false, LocalizationService.Instance.GetString("MihomoService.Status.DeploymentFailed"));
        }

        return GetStatus();
    }

    /// <summary>Uninstalls the Windows service.</summary>
    /// <param name="cancellationToken">Cancels waiting for sc.exe when requested.</param>
    /// <returns>Updated service status.</returns>
    public async Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken)
    {
        MihomoServiceStatus current = GetStatus();
        if (!current.IsInstalled)
        {
            return current;
        }

        await RunScElevatedAsync(cancellationToken, "stop", ServiceName).ConfigureAwait(false);
        ProcessResult deleteResult = await RunScElevatedAsync(cancellationToken, "delete", ServiceName).ConfigureAwait(false);
        return deleteResult.ExitCode == 0
            ? new MihomoServiceStatus(false, false, LocalizationService.Instance.GetString("MihomoService.Status.Removed"))
            : new MihomoServiceStatus(true, current.IsRunning, LocalizationService.Instance.GetString("MihomoService.Status.RemovalFailed"));
    }

    /// <summary>Attempts to locate the bundled service host.</summary>
    /// <returns>Service host path, or null when unavailable.</returns>
    private static string? ResolveServiceHostPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Binaries", "Service", "ClashSharp.MihomoService.exe"),
            Path.Combine(AppContext.BaseDirectory, "ClashSharp.MihomoService.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Runs sc.exe and captures output without elevation.</summary>
    private static ProcessResult RunSc(params string[] arguments)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = BuildScStartInfo(useShellExecute: false, verb: null, arguments),
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return new ProcessResult(process.ExitCode, output);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, exception.Message);
        }
    }

    /// <summary>Runs sc.exe with elevation and waits for completion.</summary>
    private static async Task<ProcessResult> RunScElevatedAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = BuildScStartInfo(useShellExecute: true, verb: "runas", arguments),
            };
            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Empty);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return new ProcessResult(-1, exception.Message);
        }
    }

    /// <summary>Builds sc.exe process start info.</summary>
    private static ProcessStartInfo BuildScStartInfo(bool useShellExecute, string? verb, string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "sc.exe",
            UseShellExecute = useShellExecute,
            Verb = verb ?? string.Empty,
            CreateNoWindow = !useShellExecute,
            RedirectStandardOutput = !useShellExecute,
            RedirectStandardError = !useShellExecute,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>Quotes one command-line path or value for sc.exe binPath.</summary>
    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Small process result record.</summary>
    private readonly record struct ProcessResult(int ExitCode, string Output);
}
