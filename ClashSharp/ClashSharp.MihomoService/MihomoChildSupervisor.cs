using System.ComponentModel;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.MihomoService;

internal sealed record MihomoChildOperationResult(
    bool Succeeded,
    string? ErrorCode,
    MihomoServiceIpcSnapshot Snapshot);

internal sealed record MihomoControllerBoundOperationResult<T>(
    T? Value,
    string? ErrorCode,
    MihomoServiceIpcSnapshot Snapshot,
    Exception? Failure = null)
    where T : class;

/// <summary>Command-controlled, generation-bound owner of the service mihomo child.</summary>
internal sealed class MihomoChildSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan[] DefaultRestartBackoffs =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
    ];

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly MihomoServiceOptions _options;
    private readonly MihomoGenerationStore _generationStore;
    private readonly MihomoEffectiveConfigurationMaterializer _effectiveConfigurationMaterializer;
    private readonly IMihomoChildProcessLauncher _processLauncher;
    private readonly IMihomoControllerReadinessProbe _readinessProbe;
    private readonly MihomoServiceLogBuffer _logs;
    private readonly MihomoRuntimeLogBuffer _runtimeLogs;
    private readonly TimeSpan _startupObservationDelay;
    private readonly IReadOnlyList<TimeSpan> _restartBackoffs;
    private readonly TimeSpan _stopTimeout;
    private readonly TimeSpan _readinessTimeout;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _shutdownLock = new();
    private readonly object _backgroundOperationsLock = new();
    private readonly HashSet<Task> _backgroundOperations = [];
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly string _serviceVersion;

    private MihomoServiceChildState _childState = MihomoServiceChildState.Stopped;
    private int? _childProcessId;
    private long? _activeGeneration;
    private string? _activeConfigurationHash;
    private string? _faultCode;
    private IMihomoChildProcess? _activeProcess;
    private MihomoEffectiveGeneration? _desiredGeneration;
    private MihomoRuntimeConfigurationPlan? _desiredRuntimePlan;
    private MihomoControllerRuntimeContext? _controllerContext;
    private CancellationTokenSource? _restartCancellation;
    private long _lifecycleEpoch;
    private int _unexpectedRestartCount;
    private Task? _shutdownTask;
    private int _shutdownRequested;
    private int _disposeStarted;
    private bool _backgroundOperationsSealed;

    internal MihomoChildSupervisor(
        MihomoServiceOptions options,
        MihomoGenerationStore generationStore,
        MihomoEffectiveConfigurationMaterializer effectiveConfigurationMaterializer,
        IMihomoChildProcessLauncher processLauncher,
        IMihomoControllerReadinessProbe readinessProbe,
        MihomoServiceLogBuffer logs,
        MihomoRuntimeLogBuffer runtimeLogs,
        TimeSpan? startupObservationDelay = null,
        IReadOnlyList<TimeSpan>? restartBackoffs = null,
        TimeSpan? stopTimeout = null,
        TimeSpan? readinessTimeout = null,
        string? serviceVersion = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _generationStore = generationStore ?? throw new ArgumentNullException(nameof(generationStore));
        _effectiveConfigurationMaterializer = effectiveConfigurationMaterializer
            ?? throw new ArgumentNullException(nameof(effectiveConfigurationMaterializer));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _runtimeLogs = runtimeLogs ?? throw new ArgumentNullException(nameof(runtimeLogs));
        _startupObservationDelay = startupObservationDelay ?? TimeSpan.FromMilliseconds(300);
        _restartBackoffs = restartBackoffs?.ToArray() ?? DefaultRestartBackoffs;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(10);
        _serviceVersion = string.IsNullOrWhiteSpace(serviceVersion)
            ? typeof(MihomoChildSupervisor).Assembly.GetName().Version?.ToString() ?? "0.0.0"
            : serviceVersion;
        ValidateDurations();
        _effectiveConfigurationMaterializer.CleanupStaleAfterConfirmedNoOwnedJob(
            _generationStore.PrepareRuntimeDirectory());
        _logs.Append("service", "IPC supervisor ready; mihomo is stopped until an authenticated Start command.");
    }

    internal MihomoServiceIpcSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new MihomoServiceIpcSnapshot
            {
                SessionId = _sessionId,
                ServiceVersion = _serviceVersion,
                ChildState = _childState,
                ChildProcessId = _childProcessId,
                ActiveGeneration = _activeGeneration,
                ActiveConfigurationHash = _activeConfigurationHash,
                FaultCode = _faultCode,
            };
        }
    }

    /// <summary>Resolves a ready controller only for the exact caller-observed runtime identity.</summary>
    internal string? TryGetReadyControllerContext(
        MihomoServiceIpcControllerBinding expected,
        out MihomoControllerRuntimeContext? context)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_stateLock)
        {
            context = null;
            if (expected.ServiceSessionId != _sessionId
                || expected.Generation != _activeGeneration
                || !string.Equals(
                    expected.ConfigurationHash,
                    _activeConfigurationHash,
                    StringComparison.Ordinal))
            {
                return "service.controller.stale_generation";
            }

            if (_childState != MihomoServiceChildState.Running
                || _controllerContext is null
                || _childProcessId is null)
            {
                return "service.controller.not_ready";
            }

            context = _controllerContext;
            return null;
        }
    }

    internal bool IsControllerContextCurrent(MihomoControllerRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateLock)
        {
            return _childState == MihomoServiceChildState.Running
                && ReferenceEquals(_controllerContext, context)
                && _childProcessId == context.ProcessId
                && _lifecycleEpoch == context.LifecycleEpoch
                && _activeProcess is { HasExited: false };
        }
    }

    /// <summary>
    /// Serializes one typed broker operation with lifecycle commands and checks its exact binding
    /// both before and after any upstream effect.
    /// </summary>
    internal async Task<MihomoControllerBoundOperationResult<T>> ExecuteControllerOperationAsync<T>(
        MihomoServiceIpcControllerBinding expected,
        Func<MihomoControllerRuntimeContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(operation);
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? bindingError = TryGetReadyControllerContext(expected, out var context);
            if (bindingError is not null || context is null)
            {
                return new MihomoControllerBoundOperationResult<T>(
                    null,
                    bindingError ?? "service.controller.not_ready",
                    GetSnapshot());
            }

            T value;
            try
            {
                value = await operation(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedControllerOperationException(exception))
            {
                return new MihomoControllerBoundOperationResult<T>(
                    null,
                    null,
                    GetSnapshot(),
                    exception);
            }

            if (!IsControllerContextCurrent(context))
            {
                return new MihomoControllerBoundOperationResult<T>(
                    null,
                    "service.controller.stale_generation",
                    GetSnapshot());
            }

            return new MihomoControllerBoundOperationResult<T>(
                value,
                null,
                GetSnapshot());
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal async Task<MihomoChildOperationResult> StartAsync(
        long generation,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShutdown();
            if (_activeProcess is not null)
            {
                if (!_activeProcess.HasExited)
                {
                    if (_childState == MihomoServiceChildState.Running
                        && _activeGeneration == generation
                        && string.Equals(
                            _activeConfigurationHash,
                            configurationHash,
                            StringComparison.Ordinal))
                    {
                        return Success();
                    }

                    return Failure("service.child.already_running");
                }

                // The primary process may have exited while descendants remain in its Job.
                // Confirm that the complete old tree is empty before assigning a new generation.
                IncrementEpochAndCancelRestart();
                MihomoChildOperationResult cleanup = await StopActiveProcessAsync()
                    .ConfigureAwait(false);
                if (!cleanup.Succeeded)
                {
                    return cleanup;
                }

                MihomoChildOperationResult effectiveCleanup = DeleteDesiredEffectiveAfterJobEmpty();
                if (!effectiveCleanup.Succeeded)
                {
                    return effectiveCleanup;
                }
            }

            (MihomoEffectiveGeneration? effective, MihomoRuntimeConfigurationPlan? plan, string? stagingError) =
                await TryPrepareGenerationAsync(
                    generation,
                    configurationHash,
                    cancellationToken)
                .ConfigureAwait(false);
            if (effective is null || plan is null)
            {
                return Failure(stagingError ?? "service.child.staging_failed");
            }

            cancellationToken.ThrowIfCancellationRequested();
            MihomoChildOperationResult oldEffectiveCleanup = DeleteDesiredEffectiveAfterJobEmpty();
            if (!oldEffectiveCleanup.Succeeded)
            {
                DeleteUnusedEffective(effective);
                return oldEffectiveCleanup;
            }

            BeginExplicitGeneration(effective, plan);
            return await LaunchDesiredAsync(
                    effective,
                    plan,
                    _lifecycleEpoch,
                    retainEffectiveOnFailure: false)
                .ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal async Task<MihomoChildOperationResult> ReloadAsync(
        long generation,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShutdown();
            if (_childState == MihomoServiceChildState.Running
                && _activeGeneration == generation
                && string.Equals(_activeConfigurationHash, configurationHash, StringComparison.Ordinal))
            {
                return Success();
            }

            (MihomoEffectiveGeneration? effective, MihomoRuntimeConfigurationPlan? plan, string? stagingError) =
                await TryPrepareGenerationAsync(
                    generation,
                    configurationHash,
                    cancellationToken)
                .ConfigureAwait(false);
            if (effective is null || plan is null)
            {
                return Failure(stagingError ?? "service.child.staging_failed");
            }

            cancellationToken.ThrowIfCancellationRequested();
            IncrementEpochAndCancelRestart();
            MihomoChildOperationResult stop = await StopActiveProcessAsync().ConfigureAwait(false);
            if (!stop.Succeeded)
            {
                DeleteUnusedEffective(effective);
                return stop;
            }

            MihomoChildOperationResult effectiveCleanup = DeleteDesiredEffectiveAfterJobEmpty();
            if (!effectiveCleanup.Succeeded)
            {
                DeleteUnusedEffective(effective);
                return effectiveCleanup;
            }

            _desiredGeneration = effective;
            _desiredRuntimePlan = plan;
            _unexpectedRestartCount = 0;
            CreateRestartCancellation();
            return await LaunchDesiredAsync(
                    effective,
                    plan,
                    _lifecycleEpoch,
                    retainEffectiveOnFailure: false)
                .ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal async Task<MihomoChildOperationResult> StopAsync(CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IncrementEpochAndCancelRestart();
            MihomoChildOperationResult result = await StopActiveProcessAsync().ConfigureAwait(false);
            if (result.Succeeded)
            {
                MihomoChildOperationResult effectiveCleanup = DeleteDesiredEffectiveAfterJobEmpty();
                if (!effectiveCleanup.Succeeded)
                {
                    return effectiveCleanup;
                }

                SetState(MihomoServiceChildState.Stopped, null, null, null, null);
                _logs.Append("service", "mihomo child stopped and its Job Object is empty.");
                return Success();
            }

            return result;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal Task ShutdownAsync()
    {
        lock (_shutdownLock)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Volatile.Write(ref _shutdownRequested, 1);
        _shutdownCancellation.Cancel();
        await _commandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            IncrementEpochAndCancelRestart();
            MihomoChildOperationResult result = await StopActiveProcessAsync().ConfigureAwait(false);
            if (result.Succeeded)
            {
                MihomoChildOperationResult effectiveCleanup = DeleteDesiredEffectiveAfterJobEmpty();
                if (effectiveCleanup.Succeeded)
                {
                    SetState(MihomoServiceChildState.Stopped, null, null, null, null);
                }
            }
            else if (_activeProcess is not null)
            {
                // Production processes own a KILL_ON_JOB_CLOSE handle. Disposal is the final
                // fail-closed shutdown path when explicit termination confirmation failed.
                try
                {
                    _activeProcess.Dispose();
                }
                catch (Exception exception) when (IsExpectedLifecycleException(exception))
                {
                    _logs.Append("child", $"Final Job close failed ({exception.GetType().Name}).");
                }
                finally
                {
                    _activeProcess = null;
                }
            }
        }
        finally
        {
            _commandGate.Release();
            await SealAndDrainBackgroundOperationsAsync().ConfigureAwait(false);
        }
    }

    private async Task<(
        MihomoEffectiveGeneration? Effective,
        MihomoRuntimeConfigurationPlan? Plan,
        string? ErrorCode)> TryPrepareGenerationAsync(
        long generation,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdownCancellation.Token);
            MihomoStagedGeneration staged = await _generationStore
                .StageAsync(
                    generation,
                    configurationHash,
                    linked.Token,
                    _desiredGeneration?.Source.ConfigurationPath)
                .ConfigureAwait(false);
            MihomoRuntimeConfigurationPlan plan = await MihomoRuntimeConfigurationPlan
                .ReadAsync(staged.ConfigurationPath, linked.Token)
                .ConfigureAwait(false);
            MihomoEffectiveGeneration effective = await _effectiveConfigurationMaterializer
                .MaterializeAsync(staged, _generationStore.PrepareRuntimeDirectory(), linked.Token)
                .ConfigureAwait(false);
            RegisterControllerAuthority(effective.Authority);
            return (effective, plan, null);
        }
        catch (MihomoConfigurationHashMismatchException)
        {
            return (null, null, "service.child.configuration_hash_mismatch");
        }
        catch (FileNotFoundException)
        {
            return (null, null, "service.child.configuration_missing");
        }
        catch (MihomoGenerationConflictException)
        {
            return (null, null, "service.child.generation_conflict");
        }
        catch (MihomoRuntimeAssetException exception)
        {
            _logs.Append("service", $"Runtime asset staging failed ({exception.ErrorCode}).");
            return (null, null, exception.ErrorCode);
        }
        catch (MihomoServiceConfigurationTrustException)
        {
            return (null, null, "service.child.configuration_untrusted");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            return (null, null, "service.child.service_stopping");
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            _logs.Append("service", $"Configuration staging failed ({exception.GetType().Name}).");
            return (null, null, "service.child.staging_failed");
        }
    }

    private void BeginExplicitGeneration(
        MihomoEffectiveGeneration effective,
        MihomoRuntimeConfigurationPlan plan)
    {
        IncrementEpochAndCancelRestart();
        _desiredGeneration = effective;
        _desiredRuntimePlan = plan;
        _unexpectedRestartCount = 0;
        CreateRestartCancellation();
    }

    private async Task<MihomoChildOperationResult> LaunchDesiredAsync(
        MihomoEffectiveGeneration effective,
        MihomoRuntimeConfigurationPlan plan,
        long epoch,
        bool retainEffectiveOnFailure)
    {
        MihomoStagedGeneration staged = effective.Source;
        SetState(
            MihomoServiceChildState.Starting,
            null,
            staged.Generation,
            staged.ConfigurationHash,
            null);
        IMihomoChildProcess? process = null;
        try
        {
            if (!File.Exists(_options.MihomoPath))
            {
                throw new FileNotFoundException("The mihomo executable was not found.", _options.MihomoPath);
            }

            string runtimeDirectory = _generationStore.PrepareRuntimeDirectory();
            await MihomoGenerationStore.VerifyHashAsync(
                    staged.ConfigurationPath,
                    staged.ConfigurationHash,
                    _shutdownCancellation.Token)
                .ConfigureAwait(false);
            await MihomoServiceConfigurationTrustValidator.ValidateAsync(
                    staged.ConfigurationPath,
                    runtimeDirectory,
                    _shutdownCancellation.Token)
                .ConfigureAwait(false);
            await MihomoGenerationStore.VerifyHashAsync(
                    effective.ConfigurationPath,
                    effective.EffectiveHash,
                    _shutdownCancellation.Token)
                .ConfigureAwait(false);
            process = _processLauncher.Start(new MihomoChildStartRequest(
                _options.MihomoPath,
                runtimeDirectory,
                effective.ConfigurationPath));
            _activeProcess = process;
            SetState(
                MihomoServiceChildState.Starting,
                process.Id,
                staged.Generation,
                staged.ConfigurationHash,
                null);
            StartOutputPumps(process);
            if (_startupObservationDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupObservationDelay, _shutdownCancellation.Token)
                    .ConfigureAwait(false);
            }

            if (process.HasExited)
            {
                int exitCode = process.ExitCode ?? -1;
                if (!await TryConfirmStopAndDisposeProcessAsync(process).ConfigureAwait(false))
                {
                    return RetainFailedLaunchOwnership(
                        process,
                        staged,
                        "service.child.startup_cleanup_failed");
                }

                _activeProcess = null;
                MihomoChildOperationResult? effectiveCleanup =
                    CleanupFailedLaunchEffective(effective, retainEffectiveOnFailure);
                if (effectiveCleanup is not null)
                {
                    return effectiveCleanup;
                }

                SetState(
                    MihomoServiceChildState.Faulted,
                    null,
                    staged.Generation,
                    staged.ConfigurationHash,
                    "service.child.startup_exit");
                _logs.Append("child", $"mihomo exited during startup with code {exitCode}.");
                return Failure("service.child.startup_exit");
            }

            MihomoServiceIpcEffectiveConfiguration ready = await _readinessProbe
                .WaitUntilReadyAsync(
                    effective.Authority,
                    process,
                    plan,
                    _readinessTimeout,
                    _shutdownCancellation.Token)
                .ConfigureAwait(false);
            if (process.HasExited
                || !ready.ControllerReady
                || ready.Validate() is not null
                || ready.MixedPort != plan.MixedPort
                || ready.Mode != plan.Mode
                || ready.TunEnabled != plan.TunEnabled)
            {
                throw new MihomoControllerNotReadyException(
                    "The child returned an invalid controller readiness projection.");
            }

            SetRunningState(process, effective, ready, epoch);
            _logs.Append(
                "child",
                $"mihomo started: pid={process.Id}, generation={staged.Generation}, hash={staged.ConfigurationHash}.");
            TrackBackgroundOperation(
                "exit-monitor",
                () => MonitorUnexpectedExitAsync(process, effective, plan, epoch));
            return Success();
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            if (process is not null)
            {
                await ForceCloseProcessForShutdownAsync(process).ConfigureAwait(false);
                _activeProcess = null;
            }

            SetState(MihomoServiceChildState.Stopped, null, null, null, null);
            return Failure("service.child.service_stopping");
        }
        catch (MihomoConfigurationHashMismatchException)
        {
            if (process is not null)
            {
                if (!await TryConfirmStopAndDisposeProcessAsync(process).ConfigureAwait(false))
                {
                    return RetainFailedLaunchOwnership(
                        process,
                        staged,
                        "service.child.launch_cleanup_failed");
                }

                _activeProcess = null;
            }

            MihomoChildOperationResult? effectiveCleanup =
                CleanupFailedLaunchEffective(effective, retainEffectiveOnFailure);
            if (effectiveCleanup is not null)
            {
                return effectiveCleanup;
            }

            SetState(
                MihomoServiceChildState.Faulted,
                null,
                staged.Generation,
                staged.ConfigurationHash,
                "service.child.staging_hash_mismatch");
            return Failure("service.child.staging_hash_mismatch");
        }
        catch (MihomoControllerNotReadyException exception)
        {
            if (process is not null)
            {
                if (!await TryConfirmStopAndDisposeProcessAsync(process).ConfigureAwait(false))
                {
                    _logs.Append("child", "Controller readiness failed and Job cleanup was not confirmed.");
                    return RetainFailedLaunchOwnership(
                        process,
                        staged,
                        "service.child.startup_cleanup_failed");
                }

                _activeProcess = null;
            }

            MihomoChildOperationResult? effectiveCleanup =
                CleanupFailedLaunchEffective(effective, retainEffectiveOnFailure);
            if (effectiveCleanup is not null)
            {
                return effectiveCleanup;
            }

            SetState(
                MihomoServiceChildState.Faulted,
                null,
                staged.Generation,
                staged.ConfigurationHash,
                "service.child.controller_not_ready");
            _logs.Append(
                "child",
                $"mihomo controller readiness failed ({exception.GetType().Name}).");
            return Failure("service.child.controller_not_ready");
        }
        catch (MihomoRuntimeAssetException exception)
        {
            MihomoChildOperationResult? effectiveCleanup =
                CleanupFailedLaunchEffective(effective, retainEffectiveOnFailure);
            if (effectiveCleanup is not null)
            {
                return effectiveCleanup;
            }

            SetState(
                MihomoServiceChildState.Faulted,
                null,
                staged.Generation,
                staged.ConfigurationHash,
                exception.ErrorCode);
            _logs.Append("service", $"Runtime asset validation failed ({exception.ErrorCode}).");
            return Failure(exception.ErrorCode);
        }
        catch (MihomoServiceConfigurationTrustException)
        {
            MihomoChildOperationResult? effectiveCleanup =
                CleanupFailedLaunchEffective(effective, retainEffectiveOnFailure);
            if (effectiveCleanup is not null)
            {
                return effectiveCleanup;
            }

            SetState(
                MihomoServiceChildState.Faulted,
                null,
                staged.Generation,
                staged.ConfigurationHash,
                "service.child.configuration_untrusted");
            return Failure("service.child.configuration_untrusted");
        }
        catch (FileNotFoundException)
        {
            MihomoChildOperationResult? effectiveCleanup =
                CleanupFailedLaunchEffective(effective, retainEffectiveOnFailure);
            if (effectiveCleanup is not null)
            {
                return effectiveCleanup;
            }

            SetState(
                MihomoServiceChildState.Faulted,
                null,
                staged.Generation,
                staged.ConfigurationHash,
                "service.child.binary_missing");
            return Failure("service.child.binary_missing");
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            if (process is not null)
            {
                if (!await TryConfirmStopAndDisposeProcessAsync(process).ConfigureAwait(false))
                {
                    _logs.Append("child", $"mihomo launch failed ({exception.GetType().Name}).");
                    return RetainFailedLaunchOwnership(
                        process,
                        staged,
                        "service.child.launch_cleanup_failed");
                }

                _activeProcess = null;
            }

            MihomoChildOperationResult? effectiveCleanup =
                CleanupFailedLaunchEffective(effective, retainEffectiveOnFailure);
            if (effectiveCleanup is not null)
            {
                return effectiveCleanup;
            }

            SetState(
                MihomoServiceChildState.Faulted,
                null,
                staged.Generation,
                staged.ConfigurationHash,
                "service.child.launch_failed");
            _logs.Append("child", $"mihomo launch failed ({exception.GetType().Name}).");
            return Failure("service.child.launch_failed");
        }
    }

    private async Task<MihomoChildOperationResult> StopActiveProcessAsync()
    {
        IMihomoChildProcess? process = _activeProcess;
        if (process is null)
        {
            return Success();
        }

        SetState(
            MihomoServiceChildState.Stopping,
            GetOwnedProcessId(process),
            _activeGeneration,
            _activeConfigurationHash,
            null);
        try
        {
            await process.StopTreeAsync(_stopTimeout, CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
            _activeProcess = null;
            return Success();
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            int? processId = TryGetLiveProcessId(process);
            SetState(
                MihomoServiceChildState.Faulted,
                processId,
                _activeGeneration,
                _activeConfigurationHash,
                "service.child.stop_failed");
            _logs.Append("child", $"mihomo Job shutdown failed ({exception.GetType().Name}).");
            return Failure("service.child.stop_failed");
        }
    }

    private async Task MonitorUnexpectedExitAsync(
        IMihomoChildProcess process,
        MihomoEffectiveGeneration effective,
        MihomoRuntimeConfigurationPlan plan,
        long epoch)
    {
        bool exitObserved = false;
        try
        {
            await process.WaitForExitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
            exitObserved = true;
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            _logs.Append("child", $"mihomo exit observation failed ({exception.GetType().Name}).");
        }

        if (exitObserved && !TryInvalidateControllerAfterUnexpectedExit(process, epoch))
        {
            return;
        }

        await RestartAfterUnexpectedExitAsync(process, effective, plan, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes readiness as soon as the current root process exit is observed, without waiting for
    /// a potentially long controller operation to release the lifecycle command gate.
    /// </summary>
    internal bool TryInvalidateControllerAfterUnexpectedExit(
        IMihomoChildProcess exitedProcess,
        long epoch)
    {
        lock (_stateLock)
        {
            if (epoch != _lifecycleEpoch || !ReferenceEquals(_activeProcess, exitedProcess))
            {
                return false;
            }

            _controllerContext = null;
            _childState = MihomoServiceChildState.Faulted;
            _childProcessId = null;
            _faultCode = "service.child.unexpected_exit";
            return true;
        }
    }

    private async Task RestartAfterUnexpectedExitAsync(
        IMihomoChildProcess exitedProcess,
        MihomoEffectiveGeneration effective,
        MihomoRuntimeConfigurationPlan plan,
        long epoch)
    {
        MihomoStagedGeneration staged = effective.Source;
        await _commandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (epoch != _lifecycleEpoch || !ReferenceEquals(_activeProcess, exitedProcess))
            {
                return;
            }

            int exitCode = exitedProcess.ExitCode ?? -1;
            _logs.Append(
                "child",
                $"mihomo exited unexpectedly with code {exitCode} on generation {staged.Generation}.");
            try
            {
                await StopAndDisposeProcessAsync(exitedProcess).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedLifecycleException(exception))
            {
                _activeProcess = exitedProcess;
                SetState(
                    MihomoServiceChildState.Faulted,
                    null,
                    staged.Generation,
                    staged.ConfigurationHash,
                    "service.child.exit_cleanup_failed");
                _logs.Append("child", $"Exited child Job cleanup failed ({exception.GetType().Name}).");
                return;
            }

            _activeProcess = null;
        }
        finally
        {
            _commandGate.Release();
        }

        while (true)
        {
            CancellationToken restartToken;
            TimeSpan backoff;
            await _commandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (epoch != _lifecycleEpoch
                    || IsShutdownRequested
                    || _desiredGeneration != effective)
                {
                    return;
                }

                if (_unexpectedRestartCount >= _restartBackoffs.Count)
                {
                    SetState(
                        MihomoServiceChildState.Faulted,
                        null,
                        staged.Generation,
                        staged.ConfigurationHash,
                        "service.child.restart_exhausted");
                    _logs.Append("child", "Unexpected-exit restart budget exhausted.");
                    return;
                }

                backoff = _restartBackoffs[_unexpectedRestartCount++];
                restartToken = _restartCancellation?.Token ?? _shutdownCancellation.Token;
                SetState(
                    MihomoServiceChildState.Starting,
                    null,
                    staged.Generation,
                    staged.ConfigurationHash,
                    null);
            }
            finally
            {
                _commandGate.Release();
            }

            try
            {
                await Task.Delay(backoff, restartToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (restartToken.IsCancellationRequested)
            {
                return;
            }

            await _commandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (epoch != _lifecycleEpoch
                    || IsShutdownRequested
                    || _desiredGeneration != effective)
                {
                    return;
                }

                MihomoChildOperationResult restart = await LaunchDesiredAsync(
                        effective,
                        plan,
                        epoch,
                        retainEffectiveOnFailure: true)
                    .ConfigureAwait(false);
                if (restart.Succeeded)
                {
                    _logs.Append("child", "mihomo restarted on the same immutable generation.");
                    return;
                }

                if (_activeProcess is not null)
                {
                    // A failed launch whose Job could not be confirmed empty retains ownership.
                    // Never overlap it with another restart attempt in this service session.
                    return;
                }
            }
            finally
            {
                _commandGate.Release();
            }
        }
    }

    private void StartOutputPumps(IMihomoChildProcess process)
    {
        if (process.StandardOutput is TextReader output)
        {
            TrackBackgroundOperation("stdout", () => PumpOutputAsync(output, "stdout"));
        }

        if (process.StandardError is TextReader error)
        {
            TrackBackgroundOperation("stderr", () => PumpOutputAsync(error, "stderr"));
        }
    }

    private async Task PumpOutputAsync(TextReader reader, string category)
    {
        try
        {
            while (await reader.ReadLineAsync(_shutdownCancellation.Token).ConfigureAwait(false)
                is string line)
            {
                _runtimeLogs.Append(category, line);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _logs.Append(category, $"Output stream closed ({exception.GetType().Name}).");
        }
    }

    private void TrackBackgroundOperation(string operationName, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        lock (_backgroundOperationsLock)
        {
            if (_backgroundOperationsSealed)
            {
                throw new InvalidOperationException(
                    "The mihomo service supervisor no longer accepts background operations.");
            }

            _backgroundOperations.RemoveWhere(static task => task.IsCompleted);
            Task trackedOperation = ObserveBackgroundOperationAsync(operationName, operation);
            _backgroundOperations.Add(trackedOperation);
        }
    }

    private async Task ObserveBackgroundOperationAsync(
        string operationName,
        Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logs.Append(
                "service",
                $"Background operation {operationName} failed ({exception.GetType().Name}).");
        }
    }

    private async Task SealAndDrainBackgroundOperationsAsync()
    {
        Task[] operations;
        lock (_backgroundOperationsLock)
        {
            _backgroundOperationsSealed = true;
            operations = _backgroundOperations.ToArray();
        }

        await Task.WhenAll(operations).ConfigureAwait(false);
        lock (_backgroundOperationsLock)
        {
            _backgroundOperations.Clear();
        }
    }

    private async Task StopAndDisposeProcessAsync(IMihomoChildProcess process)
    {
        await process.StopTreeAsync(_stopTimeout, CancellationToken.None).ConfigureAwait(false);
        process.Dispose();
    }

    private async Task<bool> TryConfirmStopAndDisposeProcessAsync(IMihomoChildProcess process)
    {
        try
        {
            await process.StopTreeAsync(_stopTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            _logs.Append(
                "child",
                $"Failed launch Job cleanup was not confirmed ({exception.GetType().Name}); ownership retained.");
            return false;
        }

        try
        {
            process.Dispose();
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            // The Job was already confirmed empty, so a managed-wrapper disposal failure cannot
            // permit generation overlap. Log it without downgrading the confirmed handoff.
            _logs.Append("child", $"Empty Job owner disposal failed ({exception.GetType().Name}).");
        }

        return true;
    }

    private async Task ForceCloseProcessForShutdownAsync(IMihomoChildProcess process)
    {
        if (await TryConfirmStopAndDisposeProcessAsync(process).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            process.Dispose();
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            _logs.Append("child", $"Terminal Job close failed ({exception.GetType().Name}).");
        }
    }

    private MihomoChildOperationResult RetainFailedLaunchOwnership(
        IMihomoChildProcess process,
        MihomoStagedGeneration staged,
        string faultCode)
    {
        _activeProcess = process;
        SetState(
            MihomoServiceChildState.Faulted,
            TryGetLiveProcessId(process),
            staged.Generation,
            staged.ConfigurationHash,
            faultCode);
        return Failure(faultCode);
    }

    private MihomoChildOperationResult DeleteDesiredEffectiveAfterJobEmpty()
    {
        MihomoEffectiveGeneration? effective = _desiredGeneration;
        if (effective is null)
        {
            _desiredRuntimePlan = null;
            return Success();
        }

        try
        {
            _effectiveConfigurationMaterializer.DeleteAfterJobEmpty(effective);
            _desiredGeneration = null;
            _desiredRuntimePlan = null;
            return Success();
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            SetState(
                MihomoServiceChildState.Faulted,
                null,
                effective.Source.Generation,
                effective.Source.ConfigurationHash,
                "service.child.effective_cleanup_failed");
            _logs.Append(
                "service",
                $"Effective configuration cleanup failed ({exception.GetType().Name}).");
            return Failure("service.child.effective_cleanup_failed");
        }
    }

    private MihomoChildOperationResult? CleanupFailedLaunchEffective(
        MihomoEffectiveGeneration effective,
        bool retainEffectiveOnFailure)
    {
        if (retainEffectiveOnFailure)
        {
            return null;
        }

        if (ReferenceEquals(_desiredGeneration, effective))
        {
            MihomoChildOperationResult cleanup = DeleteDesiredEffectiveAfterJobEmpty();
            return cleanup.Succeeded ? null : cleanup;
        }

        DeleteUnusedEffective(effective);
        return null;
    }

    private void DeleteUnusedEffective(MihomoEffectiveGeneration effective)
    {
        try
        {
            // This candidate has never been launched, so its Job is vacuously empty.
            _effectiveConfigurationMaterializer.DeleteAfterJobEmpty(effective);
        }
        catch (Exception exception) when (IsExpectedLifecycleException(exception))
        {
            _logs.Append(
                "service",
                $"Unused effective configuration cleanup failed ({exception.GetType().Name}).");
        }
    }

    private void RegisterControllerAuthority(MihomoControllerAuthority authority)
    {
        _logs.RegisterSensitiveValue(authority.Secret);
        _logs.RegisterSensitiveValue(authority.PipeName);
        const string prefix = @"\\.\pipe\";
        if (authority.PipeName.StartsWith(prefix, StringComparison.Ordinal))
        {
            _logs.RegisterSensitiveValue(authority.PipeName[prefix.Length..]);
        }
    }

    private void IncrementEpochAndCancelRestart()
    {
        lock (_stateLock)
        {
            _lifecycleEpoch++;
            _controllerContext = null;
        }

        _restartCancellation?.Cancel();
        _restartCancellation?.Dispose();
        _restartCancellation = null;
    }

    private void CreateRestartCancellation()
    {
        _restartCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCancellation.Token);
    }

    private void SetState(
        MihomoServiceChildState state,
        int? processId,
        long? generation,
        string? configurationHash,
        string? faultCode)
    {
        lock (_stateLock)
        {
            _controllerContext = null;
            _childState = state;
            _childProcessId = processId;
            _activeGeneration = generation;
            _activeConfigurationHash = configurationHash;
            _faultCode = faultCode;
        }
    }

    private void SetRunningState(
        IMihomoChildProcess process,
        MihomoEffectiveGeneration effective,
        MihomoServiceIpcEffectiveConfiguration ready,
        long epoch)
    {
        lock (_stateLock)
        {
            if (epoch != _lifecycleEpoch || !ReferenceEquals(_activeProcess, process))
            {
                throw new InvalidOperationException(
                    "The controller became ready outside its lifecycle epoch.");
            }

            _childState = MihomoServiceChildState.Running;
            _childProcessId = process.Id;
            _activeGeneration = effective.Source.Generation;
            _activeConfigurationHash = effective.Source.ConfigurationHash;
            _faultCode = null;
            _controllerContext = new MihomoControllerRuntimeContext(
                _sessionId,
                effective.Source.Generation,
                effective.Source.ConfigurationHash,
                epoch,
                process.Id,
                effective.Authority,
                ready);
        }
    }

    private MihomoChildOperationResult Success()
    {
        return new MihomoChildOperationResult(true, null, GetSnapshot());
    }

    private MihomoChildOperationResult Failure(string errorCode)
    {
        return new MihomoChildOperationResult(false, errorCode, GetSnapshot());
    }

    private void ThrowIfShutdown()
    {
        if (IsShutdownRequested)
        {
            throw new InvalidOperationException("The mihomo service supervisor is stopping.");
        }
    }

    private void ValidateDurations()
    {
        if (_startupObservationDelay < TimeSpan.Zero
            || _startupObservationDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(_startupObservationDelay));
        }

        if (_stopTimeout <= TimeSpan.Zero || _stopTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(_stopTimeout));
        }

        if (_readinessTimeout <= TimeSpan.Zero || _readinessTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(_readinessTimeout));
        }

        if (_restartBackoffs.Count == 0
            || _restartBackoffs.Any(static delay =>
                delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(_restartBackoffs));
        }
    }

    private static int? TryGetLiveProcessId(IMihomoChildProcess process)
    {
        try
        {
            return process.HasExited ? null : process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private int GetOwnedProcessId(IMihomoChildProcess process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            lock (_stateLock)
            {
                return _childProcessId
                    ?? throw new InvalidOperationException(
                        "The owned mihomo process has no stable process identity.");
            }
        }
    }

    private bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    private static bool IsExpectedLifecycleException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Win32Exception
            or TimeoutException;
    }

    private static bool IsExpectedControllerOperationException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or HttpRequestException
            or System.Text.Json.JsonException
            or FormatException
            or TimeoutException;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await ShutdownAsync().ConfigureAwait(false);
        _restartCancellation?.Dispose();
        _shutdownCancellation.Dispose();
        _commandGate.Dispose();
    }
}
