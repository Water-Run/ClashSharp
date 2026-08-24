using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Processes;
using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Observes and controls the runtime state of the Installer-provisioned mihomo service.</summary>
/// <remarks>
/// Invariants: Service registration is owned exclusively by the Installer; this type can only query
/// SCM state, stop a failed host as a safety fallback, and use authenticated IPC for child lifecycle.
/// Thread safety: Cached status access is synchronized; concurrent process operations still depend on the injected runner.
/// Side effects: Runtime stop fallback may start an elevated <c>sc.exe stop</c> process.
/// </remarks>
public sealed partial class MihomoServiceManager
{
    /// <summary>Windows service name.</summary>
    public const string ServiceName = "ClashSharpMihomo";

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StopOperationTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ServiceStateTransitionTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ServiceStatePollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProcessRunner _processRunner;

    private readonly Func<string, string> _getString;

    private readonly MihomoServiceIpcEndpoint _ipcEndpoint;

    private readonly IMihomoServiceIpcClient _ipcClient;

    private readonly object _statusLock = new();

    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private MihomoServiceStatus _latestStatus;

    /// <summary>Initializes the service manager.</summary>
    internal MihomoServiceManager(
        IProcessRunner processRunner,
        MihomoServiceIpcEndpoint ipcEndpoint,
        IMihomoServiceIpcClient ipcClient,
        Func<string, string> getString)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
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
        ProcessRunResult result = await RunScQueryAsync(cancellationToken).ConfigureAwait(false);
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

            _ = await RunScStopElevatedAsync(CancellationToken.None).ConfigureAwait(false);
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

    private Task<ProcessRunResult> RunScQueryAsync(CancellationToken cancellationToken)
    {
        ProcessRequest request = new("sc.exe", ["query", ServiceName], QueryTimeout);
        return _processRunner.RunAsync(request, cancellationToken);
    }

    private Task<ProcessRunResult> RunScStopElevatedAsync(CancellationToken cancellationToken)
    {
        ProcessRequest request = new(
            "sc.exe",
            ["stop", ServiceName],
            StopOperationTimeout,
            runElevated: true);
        return _processRunner.RunAsync(request, cancellationToken);
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
