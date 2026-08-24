using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Diagnostics;
using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

public sealed partial class MihomoServiceManager
{
    /// <summary>Enriches a conclusive SCM-running observation through a strict Hello/Status handshake.</summary>
    private async Task<MihomoServiceStatus> ObserveIpcStatusAsync(
        MihomoServiceStatus scmStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            IpcSessionObservation observation = await ObserveIpcSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            MihomoServiceStatus status = CreateReadyStatus(
                observation.ProtocolVersion,
                observation.Snapshot);
            CacheLatestStatus(status);
            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsProcessFatal(exception))
        {
            MihomoServiceStatus status = scmStatus with
            {
                IsRunning = false,
                Message = GetString("MihomoService.Status.Unknown"),
                IpcFailureCode = GetIpcFailureCode(exception),
            };
            CacheLatestStatus(status);
            return status;
        }
    }

    /// <summary>Negotiates the protocol and verifies Status belongs to the same service process.</summary>
    private async Task<IpcSessionObservation> ObserveIpcSessionAsync(
        CancellationToken cancellationToken)
    {
        MihomoServiceIpcResponse helloResponse = await SendIpcAsync(
            MihomoServiceIpcCommand.Hello,
            generation: null,
            configurationHash: null,
            cancellationToken).ConfigureAwait(false);
        MihomoServiceIpcSnapshot hello = RequireSuccessfulSnapshot(helloResponse);

        MihomoServiceIpcResponse statusResponse = await SendIpcAsync(
            MihomoServiceIpcCommand.Status,
            generation: null,
            configurationHash: null,
            cancellationToken).ConfigureAwait(false);
        MihomoServiceIpcSnapshot status = RequireSuccessfulSnapshot(statusResponse);
        EnsureSameSession(hello, status);
        return new IpcSessionObservation(statusResponse.ProtocolVersion, status);
    }

    private async Task<MihomoServiceIpcResponse> SendIpcAsync(
        MihomoServiceIpcCommand command,
        long? generation,
        string? configurationHash,
        CancellationToken cancellationToken)
    {
        MihomoServiceIpcRequest request = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = _ipcEndpoint.AuthenticationToken,
            Command = command,
            Generation = generation,
            ConfigurationHash = configurationHash,
        };
        return await SendIpcRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one typed controller capability bound to an exact service runtime.</summary>
    internal Task<MihomoServiceIpcResponse> SendControllerIpcAsync(
        MihomoServiceIpcCommand command,
        MihomoServiceIpcControllerBinding expectedRuntime,
        string? connectionId,
        MihomoServiceIpcProxySelection? proxySelection,
        MihomoServiceIpcRuntimeLogQuery? runtimeLogQuery,
        CancellationToken cancellationToken)
    {
        ThrowIfServiceNotProvisioned();
        ArgumentNullException.ThrowIfNull(expectedRuntime);
        if (command is not (
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration
            or MihomoServiceIpcCommand.GetConnections
            or MihomoServiceIpcCommand.CloseConnection
            or MihomoServiceIpcCommand.CloseAllConnections
            or MihomoServiceIpcCommand.GetProxyRuntimeSnapshot
            or MihomoServiceIpcCommand.SelectProxy
            or MihomoServiceIpcCommand.GetRuntimeLogs))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        MihomoServiceIpcRequest request = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = _ipcEndpoint.AuthenticationToken,
            Command = command,
            ExpectedRuntime = expectedRuntime,
            ConnectionId = connectionId,
            ProxySelection = proxySelection,
            RuntimeLogQuery = runtimeLogQuery,
        };
        string? requestError = request.Validate();
        if (requestError is not null)
        {
            throw new ArgumentException(
                $"The typed controller IPC request is invalid ({requestError}).",
                nameof(command));
        }

        return SendIpcRequestAsync(request, cancellationToken);
    }

    /// <summary>Sends the only provider mutation capability exposed by the service broker.</summary>
    internal Task<MihomoServiceIpcResponse> UpdateProviderIpcAsync(
        MihomoServiceIpcControllerBinding expectedRuntime,
        MihomoServiceIpcProviderUpdate providerUpdate,
        CancellationToken cancellationToken)
    {
        ThrowIfServiceNotProvisioned();
        ArgumentNullException.ThrowIfNull(expectedRuntime);
        ArgumentNullException.ThrowIfNull(providerUpdate);
        MihomoServiceIpcRequest request = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = _ipcEndpoint.AuthenticationToken,
            Command = MihomoServiceIpcCommand.UpdateProvider,
            ExpectedRuntime = expectedRuntime,
            ProviderUpdate = providerUpdate,
        };
        string? requestError = request.Validate();
        if (requestError is not null)
        {
            throw new ArgumentException(
                $"The typed provider update is invalid ({requestError}).",
                nameof(providerUpdate));
        }

        return SendIpcRequestAsync(request, cancellationToken);
    }

    private async Task<MihomoServiceIpcResponse> SendIpcRequestAsync(
        MihomoServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        MihomoServiceIpcResponse response = await _ipcClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string? validationError = response.ValidateFor(request);
        if (validationError is not null)
        {
            throw new InvalidDataException(
                $"The mihomo service IPC response failed request-specific validation ({validationError}).");
        }

        return response;
    }

    private void ThrowIfServiceNotProvisioned()
    {
        if (_ipcEndpoint.ProvisioningFailureCode is { } failureCode)
        {
            throw new MihomoServiceCommandException(failureCode);
        }
    }

    private MihomoServiceStatus CreateReadyStatus(
        int protocolVersion,
        MihomoServiceIpcSnapshot snapshot)
    {
        bool isReady = snapshot.ChildState == MihomoServiceChildState.Running;
        return new MihomoServiceStatus(
            true,
            isReady,
            isReady
                ? GetString("MihomoService.Status.DeployedRunning")
                : GetString("MihomoService.Status.Deployed"))
        {
            IsScmRunning = true,
            ProtocolVersion = protocolVersion,
            ServiceSessionId = snapshot.SessionId,
            ServiceVersion = snapshot.ServiceVersion,
            ChildState = snapshot.ChildState,
            ChildProcessId = snapshot.ChildProcessId,
            ActiveGeneration = snapshot.ActiveGeneration,
            ActiveConfigurationHash = snapshot.ActiveConfigurationHash,
            IpcFailureCode = snapshot.FaultCode,
        };
    }

    private async Task<MihomoServiceStatus> CleanupFailedActivationAsync(
        MihomoServiceStatus fallbackScmStatus,
        string activationFailureCode)
    {
        ServiceQueryResult stoppedQuery;
        try
        {
            _ = await RunScStopElevatedAsync(CancellationToken.None).ConfigureAwait(false);
            stoppedQuery = await ObserveTerminalStateAsync(
                shouldBeRunning: false,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsProcessFatal(exception))
        {
            MihomoServiceStatus cleanupFailed = fallbackScmStatus with
            {
                IsRunning = false,
                IpcFailureCode = activationFailureCode,
                CleanupFailureCode = GetIpcFailureCode(exception),
            };
            CacheLatestStatus(cleanupFailed);
            return cleanupFailed;
        }

        MihomoServiceStatus observed = stoppedQuery.IsConclusive
            ? stoppedQuery.Status
            : fallbackScmStatus with { IsRunning = false };
        bool cleanupConfirmed = stoppedQuery.IsConclusive
            && (!stoppedQuery.Status.IsInstalled
                || stoppedQuery.State == ServiceControllerState.Stopped);
        MihomoServiceStatus result = observed with
        {
            IsRunning = false,
            IpcFailureCode = activationFailureCode,
            CleanupFailureCode = cleanupConfirmed
                ? null
                : "service.ipc.scm_stop_not_confirmed",
        };
        CacheLatestStatus(result);
        return result;
    }

    /// <summary>Attempts cooperative child release without treating IPC as SCM ownership proof.</summary>
    private async Task TryStopIpcChildAsync(CancellationToken cancellationToken)
    {
        try
        {
            IpcSessionObservation session = await ObserveIpcSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            MihomoServiceIpcResponse response = await SendIpcAsync(
                MihomoServiceIpcCommand.Stop,
                generation: null,
                configurationHash: null,
                cancellationToken).ConfigureAwait(false);
            MihomoServiceIpcSnapshot snapshot = RequireSuccessfulSnapshot(response);
            EnsureSameSession(session.Snapshot, snapshot);
            if (snapshot.ChildState != MihomoServiceChildState.Stopped)
            {
                throw new InvalidDataException(
                    "The mihomo service did not confirm that its child stopped.");
            }
        }
        catch (Exception exception) when (!IsProcessFatal(exception))
        {
            // SCM stop is still mandatory and authoritative after every IPC failure.
        }
    }

    private static MihomoServiceIpcSnapshot RequireSuccessfulSnapshot(
        MihomoServiceIpcResponse response)
    {
        if (!response.Succeeded)
        {
            throw new MihomoServiceCommandException(
                response.ErrorCode ?? "service.ipc.command_failed");
        }

        return response.Snapshot
            ?? throw new InvalidDataException(
                "The successful mihomo service IPC response did not contain a snapshot.");
    }

    private static void EnsureSameSession(
        MihomoServiceIpcSnapshot expected,
        MihomoServiceIpcSnapshot actual)
    {
        if (actual.SessionId != expected.SessionId
            || !StringComparer.Ordinal.Equals(actual.ServiceVersion, expected.ServiceVersion))
        {
            throw new InvalidDataException(
                "The mihomo service process changed during the IPC operation.");
        }
    }

    private static void EnsureActivatedSnapshot(
        MihomoServiceIpcSnapshot snapshot,
        long generation,
        string configurationHash)
    {
        if (snapshot.ChildState != MihomoServiceChildState.Running
            || snapshot.ActiveGeneration != generation
            || !StringComparer.Ordinal.Equals(
                snapshot.ActiveConfigurationHash,
                configurationHash))
        {
            throw new InvalidDataException(
                "The mihomo service did not confirm the requested runtime generation and hash.");
        }
    }

    private static string GetIpcFailureCode(Exception exception)
    {
        return exception switch
        {
            MihomoServiceCommandException command => command.ErrorCode,
            MihomoServicePipeServerIdentityException => "service.ipc.endpoint_occupied",
            TimeoutException => "service.ipc.timeout",
            UnauthorizedAccessException => "service.ipc.access_denied",
            InvalidDataException => "service.ipc.protocol_invalid",
            IOException => "service.ipc.transport_failed",
            _ => "service.ipc.unavailable",
        };
    }

    private static bool IsProcessFatal(Exception exception)
    {
        return ExceptionGraphClassifier.IsProcessFatal(exception);
    }

    private sealed class MihomoServiceCommandException(string errorCode)
        : InvalidOperationException(errorCode), IStableDiagnosticCodeProvider
    {
        internal string ErrorCode { get; } = errorCode;

        public string DiagnosticCode => ErrorCode;
    }

    private readonly record struct IpcSessionObservation(
        int ProtocolVersion,
        MihomoServiceIpcSnapshot Snapshot);
}
