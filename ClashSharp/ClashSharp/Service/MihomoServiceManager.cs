using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Processes;
using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Provides deployment paths and configuration for the mihomo Windows service.</summary>
internal interface IMihomoServiceDeploymentContext
{
    /// <summary>Returns the bundled service host path, or null when unavailable.</summary>
    string? ResolveServiceHostPath();

    /// <summary>Gets the bundled mihomo binary path.</summary>
    string MihomoBinaryPath { get; }

    /// <summary>Ensures the shared service configuration path exists without acquiring TUN ownership.</summary>
    CoreConfigurationState EnsureServiceConfiguration();
}

/// <summary>Manages the optional Windows service used as transparent proxy prerequisite.</summary>
/// <remarks>
/// Invariants: Service state is read from Windows Service Control Manager.
/// Thread safety: Cached status access is synchronized; concurrent process operations still depend on the injected runner.
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

    private static readonly TimeSpan ServiceStateTransitionTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ServiceStatePollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProcessRunner _processRunner;

    private readonly IMihomoServiceDeploymentContext _deploymentContext;

    private readonly IMihomoServiceBinaryTrustValidator _binaryTrustValidator;

    private readonly Func<string, string> _getString;

    private readonly MihomoServiceIpcEndpoint _ipcEndpoint;

    private readonly IMihomoServiceIpcClient _ipcClient;

    private readonly object _statusLock = new();

    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private MihomoServiceStatus _latestStatus;

    /// <summary>Initializes the service manager.</summary>
    internal MihomoServiceManager(
        IProcessRunner processRunner,
        IMihomoServiceDeploymentContext deploymentContext,
        IMihomoServiceBinaryTrustValidator binaryTrustValidator,
        MihomoServiceIpcEndpoint ipcEndpoint,
        IMihomoServiceIpcClient ipcClient,
        Func<string, string> getString)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _deploymentContext = deploymentContext ?? throw new ArgumentNullException(nameof(deploymentContext));
        _binaryTrustValidator = binaryTrustValidator
            ?? throw new ArgumentNullException(nameof(binaryTrustValidator));
        _ipcEndpoint = ipcEndpoint ?? throw new ArgumentNullException(nameof(ipcEndpoint));
        _ipcClient = ipcClient ?? throw new ArgumentNullException(nameof(ipcClient));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _latestStatus = _ipcEndpoint.IsProvisioned
            ? MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown"))
            : CreateProvisioningFailureStatus();
    }

    /// <summary>Gets current Windows service status.</summary>
    /// <returns>Service deployment status.</returns>
    public async Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ipcEndpoint.IsProvisioned)
        {
            MihomoServiceStatus status = CreateProvisioningFailureStatus();
            CacheLatestStatus(status);
            return status;
        }

        ServiceQueryResult query = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        query = await ObservePendingTransitionAsync(query, cancellationToken).ConfigureAwait(false);
        if (!query.IsConclusive)
        {
            return MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown"));
        }

        return query.State == ServiceControllerState.Running
            ? await ObserveIpcStatusAsync(query.Status, cancellationToken).ConfigureAwait(false)
            : query.Status;
    }

    /// <summary>Gets the latest observed status without performing process I/O.</summary>
    public MihomoServiceStatus GetLatestStatus()
    {
        lock (_statusLock)
        {
            return _latestStatus;
        }
    }

    private async Task<ServiceQueryResult> QueryStatusAsync(CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunScAsync(cancellationToken, "query", ServiceName).ConfigureAwait(false);
        if (result.Outcome == ProcessRunOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        ServiceQueryResult queryResult;
        if (result.Outcome == ProcessRunOutcome.Completed && result.ExitCode == 1060)
        {
            queryResult = new ServiceQueryResult(
                new MihomoServiceStatus(false, false, GetString("MihomoService.Status.NotDeployed")),
                ServiceControllerState.NotInstalled);
        }
        else if (result.Outcome != ProcessRunOutcome.Completed || result.ExitCode != 0)
        {
            queryResult = new ServiceQueryResult(
                MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown")),
                ServiceControllerState.Unknown);
        }
        else
        {
            ServiceControllerState state = ParseServiceControllerState(result.CombinedOutput);
            if (state == ServiceControllerState.Other)
            {
                queryResult = new ServiceQueryResult(
                    MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown")),
                    state);
            }
            else
            {
                bool isRunning = state == ServiceControllerState.Running;
                queryResult = new ServiceQueryResult(
                    new MihomoServiceStatus(
                        true,
                        false,
                        isRunning
                            ? GetString("MihomoService.Status.DeployedRunning")
                            : GetString("MihomoService.Status.Deployed"))
                    {
                        IsScmRunning = isRunning,
                    },
                    state);
            }
        }

        CacheLatestStatus(queryResult.IsPending || !queryResult.IsConclusive
            ? MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown"))
            : queryResult.Status);

        return queryResult;
    }

    /// <summary>Polls SCM until a requested running/stopped terminal state is confirmed.</summary>
    private async Task<ServiceQueryResult> ObserveTerminalStateAsync(
        bool shouldBeRunning,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            ServiceQueryResult observed = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!observed.IsConclusive || !observed.Status.IsInstalled)
            {
                return observed;
            }

            bool reachedTerminalState = shouldBeRunning
                ? observed.State == ServiceControllerState.Running
                : observed.State == ServiceControllerState.Stopped;
            if (reachedTerminalState)
            {
                return observed;
            }

            if (!observed.IsPending || stopwatch.Elapsed >= ServiceStateTransitionTimeout)
            {
                return observed.IsPending
                    ? CacheUnknownQueryResult()
                    : observed;
            }

            await Task.Delay(ServiceStatePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for an already-pending SCM transition before issuing another control command.</summary>
    private Task<ServiceQueryResult> ObservePendingTransitionAsync(
        ServiceQueryResult query,
        CancellationToken cancellationToken)
    {
        return query.State switch
        {
            ServiceControllerState.StartPending => ObserveTerminalStateAsync(
                shouldBeRunning: true,
                cancellationToken),
            ServiceControllerState.StopPending => ObserveTerminalStateAsync(
                shouldBeRunning: false,
                cancellationToken),
            _ => Task.FromResult(query),
        };
    }

    /// <summary>Deploys the Windows service when a service host is available.</summary>
    /// <param name="cancellationToken">Cancels waiting for sc.exe when requested.</param>
    /// <returns>Updated service status or failure status.</returns>
    internal async Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ipcEndpoint.IsProvisioned)
        {
            MihomoServiceStatus status = CreateProvisioningFailureStatus();
            CacheLatestStatus(status);
            return status;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceQueryResult currentQuery = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
            currentQuery = await ObservePendingTransitionAsync(currentQuery, cancellationToken).ConfigureAwait(false);
            if (!currentQuery.IsConclusive)
            {
                return MihomoServiceStatus.Unknown(GetString("MihomoService.Status.DeploymentFailed"));
            }

            string? serviceHostPath = _deploymentContext.ResolveServiceHostPath();
            if (serviceHostPath is null)
            {
                return currentQuery.Status with
                {
                    IsRunning = false,
                    Message = GetString("MihomoService.Status.HostMissing"),
                };
            }

            string mihomoPath = _deploymentContext.MihomoBinaryPath;
            MihomoServiceBinaryTrustValidation binaryTrust = _binaryTrustValidator.Validate(
                serviceHostPath,
                mihomoPath);
            if (!binaryTrust.IsTrusted)
            {
                return currentQuery.Status with
                {
                    IsRunning = false,
                    Message = GetString("MihomoService.Status.UntrustedBinaries"),
                };
            }

            string configPath = _deploymentContext.EnsureServiceConfiguration().ConfigPath;
            string binPath = Quote(serviceHostPath)
                + " --mihomo " + Quote(mihomoPath)
                + " --config " + Quote(configPath)
                + " --pipe-name " + Quote(_ipcEndpoint.PipeName)
                + " --ipc-token " + Quote(_ipcEndpoint.AuthenticationToken)
                + " --allowed-sid " + Quote(_ipcEndpoint.UserSid);

            if (currentQuery.Status.IsInstalled)
            {
                // An existing installation may point at an older executable,
                // token, SID, or pipe. Stop its child and host before replacing
                // SCM's immutable launch contract.
                if (currentQuery.State == ServiceControllerState.Running)
                {
                    // Reconciliation crosses its commit point before releasing the
                    // live owner. Once crossed, finish stop + config atomically with
                    // respect to caller cancellation so the old service is never
                    // left stopped under a stale immutable launch contract.
                    cancellationToken.ThrowIfCancellationRequested();
                    await TryStopIpcChildAsync(CancellationToken.None).ConfigureAwait(false);
                    _ = await RunScElevatedAsync(
                        CancellationToken.None,
                        "stop",
                        ServiceName).ConfigureAwait(false);
                    ServiceQueryResult stoppedQuery = await ObserveTerminalStateAsync(
                        shouldBeRunning: false,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!stoppedQuery.IsConclusive
                        || stoppedQuery.State != ServiceControllerState.Stopped)
                    {
                        MihomoServiceStatus failure = CreateDeploymentFailed(stoppedQuery.Status);
                        cancellationToken.ThrowIfCancellationRequested();
                        return failure;
                    }

                    ProcessRunResult committedConfigResult = await RunScElevatedAsync(
                        CancellationToken.None,
                        "config",
                        ServiceName,
                        "binPath=",
                        binPath,
                        "start=",
                        "demand",
                        "DisplayName=",
                        ServiceDisplayName).ConfigureAwait(false);
                    ServiceQueryResult committedReconciledQuery = await QueryStatusAsync(
                        CancellationToken.None).ConfigureAwait(false);
                    MihomoServiceStatus committedStatus;
                    if (committedConfigResult.Outcome != ProcessRunOutcome.Completed
                        || committedConfigResult.ExitCode != 0
                        || !committedReconciledQuery.IsConclusive
                        || !committedReconciledQuery.Status.IsInstalled
                        || committedReconciledQuery.State != ServiceControllerState.Stopped)
                    {
                        committedStatus = CreateDeploymentFailed(committedReconciledQuery.Status);
                    }
                    else
                    {
                        committedStatus = committedReconciledQuery.Status;
                        CacheLatestStatus(committedStatus);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return committedStatus;
                }

                ProcessRunResult configResult = await RunScElevatedAsync(
                    cancellationToken,
                    "config",
                    ServiceName,
                    "binPath=",
                    binPath,
                    "start=",
                    "demand",
                    "DisplayName=",
                    ServiceDisplayName).ConfigureAwait(false);
                ServiceQueryResult reconciledQuery = await QueryStatusAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                ThrowIfCancelled(configResult, cancellationToken);
                if (configResult.Outcome != ProcessRunOutcome.Completed
                    || configResult.ExitCode != 0
                    || !reconciledQuery.IsConclusive
                    || !reconciledQuery.Status.IsInstalled
                    || reconciledQuery.State != ServiceControllerState.Stopped)
                {
                    return CreateDeploymentFailed(reconciledQuery.Status);
                }

                CacheLatestStatus(reconciledQuery.Status);
                return reconciledQuery.Status;
            }

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
            ServiceQueryResult observedQuery = await QueryStatusAsync(CancellationToken.None).ConfigureAwait(false);
            ThrowIfCancelled(createResult, cancellationToken);

            if (observedQuery.IsConclusive && observedQuery.Status.IsInstalled)
            {
                return observedQuery.State == ServiceControllerState.Running
                    ? await ObserveIpcStatusAsync(observedQuery.Status, CancellationToken.None)
                        .ConfigureAwait(false)
                    : observedQuery.Status;
            }

            if (!observedQuery.IsConclusive)
            {
                return MihomoServiceStatus.Unknown(GetString("MihomoService.Status.DeploymentFailed"));
            }

            if (createResult.Outcome != ProcessRunOutcome.Completed || createResult.ExitCode != 0)
            {
                return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.DeploymentFailed"));
            }

            return new MihomoServiceStatus(false, false, GetString("MihomoService.Status.DeploymentFailed"));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Activates an exact promoted generation in the installed service.</summary>
    /// <param name="generation">Promoted runtime generation.</param>
    /// <param name="configurationHash">Exact lowercase SHA-256 of the promoted configuration bytes.</param>
    /// <param name="cancellationToken">Cancels waiting for service-control operations.</param>
    /// <returns>The authenticated child state observed after the activation attempt.</returns>
    public async Task<MihomoServiceStatus> RestartAsync(
        long generation,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);

        if (!MihomoServiceIpcProtocol.IsCanonicalSha256(configurationHash))
        {
            throw new ArgumentException(
                "The runtime configuration hash must be canonical lowercase SHA-256 text.",
                nameof(configurationHash));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_ipcEndpoint.IsProvisioned)
        {
            MihomoServiceStatus status = CreateProvisioningFailureStatus();
            CacheLatestStatus(status);
            return status;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceQueryResult currentQuery = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
            currentQuery = await ObservePendingTransitionAsync(currentQuery, cancellationToken).ConfigureAwait(false);
            if (!currentQuery.IsConclusive || !currentQuery.Status.IsInstalled)
            {
                return currentQuery.Status;
            }

            if (currentQuery.State != ServiceControllerState.Running)
            {
                MihomoServiceStatus unavailable = currentQuery.Status with
                {
                    IpcFailureCode = RuntimeFailureDiagnostics.ServiceUnavailable,
                };
                CacheLatestStatus(unavailable);
                return unavailable;
            }

            try
            {
                IpcSessionObservation session = await ObserveIpcSessionAsync(cancellationToken)
                    .ConfigureAwait(false);
                MihomoServiceIpcResponse response = await SendIpcAsync(
                    MihomoServiceIpcCommand.Reload,
                    generation,
                    configurationHash,
                    cancellationToken).ConfigureAwait(false);
                MihomoServiceIpcSnapshot snapshot = RequireSuccessfulSnapshot(response);
                EnsureSameSession(session.Snapshot, snapshot);
                EnsureActivatedSnapshot(snapshot, generation, configurationHash);

                MihomoServiceStatus status = CreateReadyStatus(response.ProtocolVersion, snapshot);
                CacheLatestStatus(status);
                return status;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = await CleanupFailedActivationAsync(
                    currentQuery.Status,
                    "service.ipc.activation_cancelled").ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (!IsProcessFatal(exception))
            {
                return await CleanupFailedActivationAsync(
                    currentQuery.Status,
                    GetIpcFailureCode(exception)).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Stops the service-owned child while keeping the authenticated host available.</summary>
    /// <param name="cancellationToken">Cancels waiting for the service-control operation.</param>
    /// <returns>The final status observed after the stop attempt.</returns>
    public async Task<MihomoServiceStatus> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ipcEndpoint.IsProvisioned)
        {
            MihomoServiceStatus status = CreateProvisioningFailureStatus();
            CacheLatestStatus(status);
            return status;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceQueryResult currentQuery = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
            currentQuery = await ObservePendingTransitionAsync(currentQuery, cancellationToken).ConfigureAwait(false);
            if (!currentQuery.IsConclusive
                || !currentQuery.Status.IsInstalled
                || currentQuery.State != ServiceControllerState.Running)
            {
                return currentQuery.Status;
            }

            // Releasing the only service-owned core is a commit point. Caller
            // cancellation is honored before it, then deferred until IPC has
            // supplied a terminal ownership observation.
            cancellationToken.ThrowIfCancellationRequested();

            // Normal operation keeps the Installer-managed host alive so later
            // TUN transitions do not require elevation. SCM stop remains the
            // fail-closed fallback when authenticated IPC cannot prove that the
            // service-owned child released its network ownership.
            MihomoServiceStatus? ipcStoppedStatus = null;
            try
            {
                IpcSessionObservation session = await ObserveIpcSessionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                MihomoServiceIpcResponse response = await SendIpcAsync(
                    MihomoServiceIpcCommand.Stop,
                    generation: null,
                    configurationHash: null,
                    CancellationToken.None).ConfigureAwait(false);
                MihomoServiceIpcSnapshot snapshot = RequireSuccessfulSnapshot(response);
                EnsureSameSession(session.Snapshot, snapshot);
                if (snapshot.ChildState != MihomoServiceChildState.Stopped)
                {
                    throw new InvalidDataException(
                        "The mihomo service did not confirm that its child stopped.");
                }

                ipcStoppedStatus = CreateReadyStatus(
                    response.ProtocolVersion,
                    snapshot);
            }
            catch (Exception exception) when (!IsProcessFatal(exception))
            {
                // IPC failure must never prevent the authoritative SCM fallback.
            }

            if (ipcStoppedStatus is { } confirmedStoppedStatus)
            {
                CacheLatestStatus(confirmedStoppedStatus);
                cancellationToken.ThrowIfCancellationRequested();
                return confirmedStoppedStatus;
            }

            _ = await RunScElevatedAsync(
                CancellationToken.None,
                "stop",
                ServiceName).ConfigureAwait(false);
            ServiceQueryResult stoppedQuery = await ObserveTerminalStateAsync(
                shouldBeRunning: false,
                CancellationToken.None)
                .ConfigureAwait(false);
            MihomoServiceStatus status = stoppedQuery.IsConclusive
                ? stoppedQuery.Status
                : MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown"));
            CacheLatestStatus(status);
            cancellationToken.ThrowIfCancellationRequested();
            return status;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Uninstalls the Windows service.</summary>
    /// <param name="cancellationToken">Cancels waiting for sc.exe when requested.</param>
    /// <returns>Updated service status.</returns>
    internal async Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ipcEndpoint.IsProvisioned)
        {
            MihomoServiceStatus status = CreateProvisioningFailureStatus();
            CacheLatestStatus(status);
            return status;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UninstallCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<MihomoServiceStatus> UninstallCoreAsync(CancellationToken cancellationToken)
    {
        ServiceQueryResult currentQuery = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        currentQuery = await ObservePendingTransitionAsync(currentQuery, cancellationToken).ConfigureAwait(false);
        if (!currentQuery.IsConclusive)
        {
            return CreateRemovalFailed(currentQuery.Status);
        }

        MihomoServiceStatus current = currentQuery.Status;
        if (!current.IsInstalled)
        {
            return current;
        }

        if (currentQuery.State == ServiceControllerState.Running)
        {
            // Once uninstall starts releasing a running owner, finish the SCM
            // stop even if the caller cancels. Deletion remains a later, stable
            // cancellation boundary.
            cancellationToken.ThrowIfCancellationRequested();
            await TryStopIpcChildAsync(CancellationToken.None).ConfigureAwait(false);
        }

        ProcessRunResult stopResult = await RunScElevatedAsync(
            currentQuery.State == ServiceControllerState.Running
                ? CancellationToken.None
                : cancellationToken,
            "stop",
            ServiceName).ConfigureAwait(false);
        ServiceQueryResult afterStopQuery = await ObserveTerminalStateAsync(
            shouldBeRunning: false,
            CancellationToken.None)
            .ConfigureAwait(false);
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

        if (stopResult.Outcome is ProcessRunOutcome.TimedOut
            or ProcessRunOutcome.StartFailed
            or ProcessRunOutcome.Cancelled)
        {
            return CreateRemovalFailed(afterStop);
        }

        // SCM has now confirmed a stable stopped owner. Cancellation may safely
        // prevent the independent service-registration deletion.
        cancellationToken.ThrowIfCancellationRequested();
        ProcessRunResult deleteResult = await RunScElevatedAsync(
            cancellationToken,
            "delete",
            ServiceName).ConfigureAwait(false);
        ServiceQueryResult afterDeleteQuery = await QueryStatusAsync(CancellationToken.None).ConfigureAwait(false);
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

    private Task<ProcessRunResult> RunScAsync(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        ProcessRequest request = new("sc.exe", arguments, QueryTimeout);
        return _processRunner.RunAsync(request, cancellationToken);
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
        string message = GetString("MihomoService.Status.RemovalFailed");
        return observed.IsKnown
            ? observed with { IsRunning = false, Message = message }
            : MihomoServiceStatus.Unknown(message);
    }

    private MihomoServiceStatus CreateDeploymentFailed(MihomoServiceStatus observed)
    {
        string message = GetString("MihomoService.Status.DeploymentFailed");
        MihomoServiceStatus status = observed.IsKnown
            ? observed with { IsRunning = false, Message = message }
            : MihomoServiceStatus.Unknown(message);
        CacheLatestStatus(status);
        return status;
    }

    private MihomoServiceStatus CreateProvisioningFailureStatus()
    {
        string failureCode = _ipcEndpoint.ProvisioningFailureCode
            ?? throw new InvalidOperationException(
                "A provisioned service endpoint has no provisioning failure status.");
        return MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown")) with
        {
            ProvisioningFailureCode = failureCode,
        };
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

    private void CacheLatestStatus(MihomoServiceStatus status)
    {
        lock (_statusLock)
        {
            _latestStatus = status;
        }
    }

    private ServiceQueryResult CacheUnknownQueryResult()
    {
        MihomoServiceStatus status = MihomoServiceStatus.Unknown(GetString("MihomoService.Status.Unknown"));
        CacheLatestStatus(status);
        return new ServiceQueryResult(status, ServiceControllerState.Unknown);
    }

    private static ServiceControllerState ParseServiceControllerState(string output)
    {
        if (output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceControllerState.StartPending;
        }

        if (output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceControllerState.StopPending;
        }

        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceControllerState.Running;
        }

        if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceControllerState.Stopped;
        }

        return ServiceControllerState.Other;
    }

    private readonly record struct ServiceQueryResult(
        MihomoServiceStatus Status,
        ServiceControllerState State)
    {
        public bool IsConclusive => Status.IsKnown && State is not (
            ServiceControllerState.Unknown or ServiceControllerState.Other);

        public bool IsPending => State is ServiceControllerState.StartPending
            or ServiceControllerState.StopPending;
    }

    private enum ServiceControllerState
    {
        Unknown,
        NotInstalled,
        Stopped,
        Running,
        StartPending,
        StopPending,
        Other,
    }
}
