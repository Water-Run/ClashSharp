using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Processes;
using ClashSharp.Model;

namespace ClashSharp.Service;

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

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ElevatedOperationTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _processRunner;

    private readonly IMihomoServiceDeploymentContext _deploymentContext;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes the service manager.</summary>
    internal MihomoServiceManager(
        IProcessRunner processRunner,
        IMihomoServiceDeploymentContext deploymentContext,
        Func<string, string> getString)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _deploymentContext = deploymentContext ?? throw new ArgumentNullException(nameof(deploymentContext));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Gets current Windows service status.</summary>
    /// <returns>Service deployment status.</returns>
    public MihomoServiceStatus GetStatus()
    {
        return QueryStatus().Status;
    }

    private ServiceQueryResult QueryStatus()
    {
        ProcessRunResult result = RunSc("query", ServiceName);
        if (result.Outcome == ProcessRunOutcome.Completed && result.ExitCode == 1060)
        {
            return new ServiceQueryResult(
                true,
                new MihomoServiceStatus(false, false, GetString("MihomoService.Status.NotDeployed")));
        }

        if (result.Outcome != ProcessRunOutcome.Completed || result.ExitCode != 0)
        {
            return new ServiceQueryResult(
                false,
                new MihomoServiceStatus(false, false, GetString("MihomoService.Status.NotDeployed")));
        }

        bool isRunning = result.CombinedOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        return new ServiceQueryResult(
            true,
            new MihomoServiceStatus(
                true,
                isRunning,
                isRunning
                    ? GetString("MihomoService.Status.DeployedRunning")
                    : GetString("MihomoService.Status.Deployed")));
    }

    /// <summary>Deploys the Windows service when a service host is available.</summary>
    /// <param name="cancellationToken">Cancels waiting for sc.exe when requested.</param>
    /// <returns>Updated service status or failure status.</returns>
    public async Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken)
    {
        ServiceQueryResult currentQuery = QueryStatus();
        if (!currentQuery.IsConclusive)
        {
            return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.DeploymentFailed"));
        }

        MihomoServiceStatus current = currentQuery.Status;
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

        ProcessRunResult createResult = await RunScElevatedAsync(
            cancellationToken,
            "create",
            ServiceName,
            "binPath=",
            binPath,
            "start=",
            "demand",
            "DisplayName=",
            ServiceDisplayName).ConfigureAwait(false);
        ServiceQueryResult observedQuery = QueryStatus();
        ThrowIfCancelled(createResult, cancellationToken);

        if (observedQuery.IsConclusive && observedQuery.Status.IsInstalled)
        {
            return observedQuery.Status;
        }

        if (createResult.Outcome != ProcessRunOutcome.Completed || createResult.ExitCode != 0)
        {
            return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.DeploymentFailed"));
        }

        return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.DeploymentFailed"));
    }

    /// <summary>Uninstalls the Windows service.</summary>
    /// <param name="cancellationToken">Cancels waiting for sc.exe when requested.</param>
    /// <returns>Updated service status.</returns>
    public async Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken)
    {
        ServiceQueryResult currentQuery = QueryStatus();
        if (!currentQuery.IsConclusive)
        {
            return CreateRemovalFailed(currentQuery.Status);
        }

        MihomoServiceStatus current = currentQuery.Status;
        if (!current.IsInstalled)
        {
            return current;
        }

        ProcessRunResult stopResult = await RunScElevatedAsync(
            cancellationToken,
            "stop",
            ServiceName).ConfigureAwait(false);
        ServiceQueryResult afterStopQuery = QueryStatus();
        ThrowIfCancelled(stopResult, cancellationToken);
        if (!afterStopQuery.IsConclusive)
        {
            return CreateRemovalFailed(current);
        }

        MihomoServiceStatus afterStop = afterStopQuery.Status;
        if (!afterStop.IsInstalled)
        {
            return afterStop;
        }

        if (stopResult.Outcome is ProcessRunOutcome.TimedOut or ProcessRunOutcome.StartFailed)
        {
            return CreateRemovalFailed(afterStop);
        }

        ProcessRunResult deleteResult = await RunScElevatedAsync(
            cancellationToken,
            "delete",
            ServiceName).ConfigureAwait(false);
        ServiceQueryResult afterDeleteQuery = QueryStatus();
        ThrowIfCancelled(deleteResult, cancellationToken);
        if (!afterDeleteQuery.IsConclusive)
        {
            return CreateRemovalFailed(afterStop);
        }

        MihomoServiceStatus afterDelete = afterDeleteQuery.Status;
        return afterDelete.IsInstalled
            ? CreateRemovalFailed(afterDelete)
            : afterDelete;
    }

    private ProcessRunResult RunSc(params string[] arguments)
    {
        ProcessRequest request = new("sc.exe", arguments, QueryTimeout);
        return _processRunner.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    }

    private Task<ProcessRunResult> RunScElevatedAsync(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        ProcessRequest request = new(
            "sc.exe",
            arguments,
            ElevatedOperationTimeout,
            runElevated: true);
        return _processRunner.RunAsync(request, cancellationToken);
    }

    private MihomoServiceStatus CreateRemovalFailed(MihomoServiceStatus observed)
    {
        return new MihomoServiceStatus(
            observed.IsInstalled,
            observed.IsRunning,
            GetString("MihomoService.Status.RemovalFailed"));
    }

    private static void ThrowIfCancelled(ProcessRunResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome == ProcessRunOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
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

    private readonly record struct ServiceQueryResult(bool IsConclusive, MihomoServiceStatus Status);
}
