/*
 * Mihomo Service Manager
 * Manages the optional Windows service used as transparent proxy prerequisite
 *
 * @author: WaterRun
 * @file: Service/MihomoServiceManager.cs
 * @date: 2026-06-24
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Runs Windows service-control commands for the mihomo service manager.</summary>
internal interface IMihomoServiceCommandRunner
{
    /// <summary>Runs sc.exe without elevation.</summary>
    MihomoServiceCommandResult RunSc(params string[] arguments);

    /// <summary>Runs sc.exe with elevation.</summary>
    Task<MihomoServiceCommandResult> RunScElevatedAsync(CancellationToken cancellationToken, params string[] arguments);
}

/// <summary>Provides deployment paths and configuration for the mihomo Windows service.</summary>
internal interface IMihomoServiceDeploymentContext
{
    /// <summary>Returns the bundled service host path, or null when unavailable.</summary>
    string? ResolveServiceHostPath();

    /// <summary>Gets the bundled mihomo binary path.</summary>
    string MihomoBinaryPath { get; }

    /// <summary>Ensures a TUN-enabled runtime configuration exists for the service.</summary>
    CoreConfigurationState EnsureTransparentProxyConfiguration();
}

/// <summary>Small command result record.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="Output">Captured output text; never null.</param>
internal readonly record struct MihomoServiceCommandResult(int ExitCode, string Output);

/// <summary>Manages the optional Windows service used as transparent proxy prerequisite.</summary>
/// <remarks>
/// Invariants: Service state is read from Windows Service Control Manager.
/// Thread safety: Stateless methods are safe for concurrent calls when dependencies are safe.
/// Side effects: May start elevated sc.exe processes for deployment and removal through injected dependencies.
/// </remarks>
public sealed partial class MihomoServiceManager
{
    /// <summary>Windows service name.</summary>
    public const string ServiceName = "ClashSharpMihomo";

    /// <summary>Windows service display name.</summary>
    private const string ServiceDisplayName = "Clash# Mihomo Service";

    private readonly IMihomoServiceCommandRunner _commandRunner;

    private readonly IMihomoServiceDeploymentContext _deploymentContext;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes the service manager.</summary>
    internal MihomoServiceManager(
        IMihomoServiceCommandRunner commandRunner,
        IMihomoServiceDeploymentContext deploymentContext,
        Func<string, string> getString)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _deploymentContext = deploymentContext ?? throw new ArgumentNullException(nameof(deploymentContext));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Gets current Windows service status.</summary>
    /// <returns>Service deployment status.</returns>
    public MihomoServiceStatus GetStatus()
    {
        MihomoServiceCommandResult result = _commandRunner.RunSc("query", ServiceName);
        if (result.ExitCode != 0)
        {
            return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.NotDeployed"));
        }

        bool isRunning = result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        return new MihomoServiceStatus(
            true,
            isRunning,
            isRunning
                ? GetString("MihomoService.Status.DeployedRunning")
                : GetString("MihomoService.Status.Deployed"));
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

        string? serviceHostPath = _deploymentContext.ResolveServiceHostPath();
        if (serviceHostPath is null)
        {
            return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.HostMissing"));
        }

        string mihomoPath = _deploymentContext.MihomoBinaryPath;
        string configPath = _deploymentContext.EnsureTransparentProxyConfiguration().ConfigPath;
        string workDirectory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        string binPath = Quote(serviceHostPath)
            + " --mihomo " + Quote(mihomoPath)
            + " --config " + Quote(configPath)
            + " --workdir " + Quote(workDirectory);

        MihomoServiceCommandResult createResult = await _commandRunner.RunScElevatedAsync(
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
            return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.DeploymentFailed"));
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

        await _commandRunner.RunScElevatedAsync(cancellationToken, "stop", ServiceName).ConfigureAwait(false);
        MihomoServiceCommandResult deleteResult = await _commandRunner.RunScElevatedAsync(cancellationToken, "delete", ServiceName).ConfigureAwait(false);
        return deleteResult.ExitCode == 0
            ? new MihomoServiceStatus(false, false, GetString("MihomoService.Status.Removed"))
            : new MihomoServiceStatus(true, current.IsRunning, GetString("MihomoService.Status.RemovalFailed"));
    }

    /// <summary>Quotes one command-line path or value for sc.exe binPath.</summary>
    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
