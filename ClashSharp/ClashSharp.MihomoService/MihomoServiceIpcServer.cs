using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.MihomoService;

/// <summary>Builds the service pipe DACL without relying on the service process identity.</summary>
internal static class MihomoServicePipeSecurity
{
    internal static PipeSecurity Create(SecurityIdentifier allowedUserSid)
    {
        ArgumentNullException.ThrowIfNull(allowedUserSid);
        SecurityIdentifier network = new(WellKnownSidType.NetworkSid, null);
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            network,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            allowedUserSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            localSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            administrators,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}

/// <summary>Authenticates and serially routes one decoded request to the child supervisor.</summary>
internal sealed class MihomoServiceCommandProcessor
{
    private readonly byte[] _authenticationDigest;
    private readonly MihomoChildSupervisor _supervisor;
    private readonly MihomoServiceLogBuffer _logs;
    private readonly MihomoServiceControllerBroker _controllerBroker;

    internal MihomoServiceCommandProcessor(
        MihomoServiceOptions options,
        MihomoChildSupervisor supervisor,
        MihomoServiceLogBuffer logs,
        MihomoServiceControllerBroker controllerBroker)
    {
        ArgumentNullException.ThrowIfNull(options);
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _controllerBroker = controllerBroker
            ?? throw new ArgumentNullException(nameof(controllerBroker));
        _authenticationDigest = SHA256.HashData(Encoding.UTF8.GetBytes(options.IpcToken));
    }

    internal async Task<MihomoServiceIpcResponse> ProcessAsync(
        MihomoServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
        {
            throw new InvalidDataException("A service request without correlation identity cannot be answered.");
        }

        if (!Authenticate(request.AuthenticationToken))
        {
            return Failure(request.RequestId, "service.ipc.unauthorized", includeSnapshot: false);
        }

        if (request.ProtocolVersion != MihomoServiceIpcProtocol.CurrentVersion)
        {
            return Failure(request.RequestId, "service.ipc.protocol_incompatible");
        }

        string? validationError = request.Validate();
        if (validationError is not null)
        {
            return Failure(request.RequestId, validationError);
        }

        MihomoServiceIpcResponse response = request.Command switch
        {
            MihomoServiceIpcCommand.Hello or MihomoServiceIpcCommand.Status =>
                Success(request.RequestId, _supervisor.GetSnapshot()),
            MihomoServiceIpcCommand.Start => FromOperation(
                request.RequestId,
                await _supervisor.StartAsync(
                        request.Generation!.Value,
                        request.ConfigurationHash!,
                        cancellationToken)
                    .ConfigureAwait(false)),
            MihomoServiceIpcCommand.Reload => FromOperation(
                request.RequestId,
                await _supervisor.ReloadAsync(
                        request.Generation!.Value,
                        request.ConfigurationHash!,
                        cancellationToken)
                    .ConfigureAwait(false)),
            MihomoServiceIpcCommand.Stop => FromOperation(
                request.RequestId,
                await _supervisor.StopAsync(cancellationToken).ConfigureAwait(false)),
            MihomoServiceIpcCommand.Logs => Success(
                request.RequestId,
                _supervisor.GetSnapshot(),
                _logs.ReadLatest(request.MaximumLogEntries!.Value)),
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration
                or MihomoServiceIpcCommand.GetConnections
                or MihomoServiceIpcCommand.CloseConnection
                or MihomoServiceIpcCommand.CloseAllConnections
                or MihomoServiceIpcCommand.GetProxyRuntimeSnapshot
                or MihomoServiceIpcCommand.SelectProxy
                or MihomoServiceIpcCommand.GetRuntimeLogs
                or MihomoServiceIpcCommand.UpdateProvider => FromBroker(
                    request.RequestId,
                    await _controllerBroker.ExecuteAsync(request, cancellationToken)
                        .ConfigureAwait(false)),
            _ => Failure(request.RequestId, "service.ipc.command_invalid"),
        };
        EnsureValidResponse(response);
        string? correlationError = response.ValidateFor(request);
        if (correlationError is not null)
        {
            throw new InvalidDataException(
                $"The service generated an invalid correlated IPC response ({correlationError}).");
        }

        return response;
    }

    private bool Authenticate(string? presentedToken)
    {
        byte[] presentedDigest = SHA256.HashData(
            Encoding.UTF8.GetBytes(presentedToken ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(
            _authenticationDigest,
            presentedDigest);
    }

    private MihomoServiceIpcResponse Failure(
        Guid requestId,
        string errorCode,
        bool includeSnapshot = true)
    {
        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = requestId,
            Succeeded = false,
            ErrorCode = errorCode,
            Snapshot = includeSnapshot ? _supervisor.GetSnapshot() : null,
        };
        EnsureValidResponse(response);
        return response;
    }

    private static MihomoServiceIpcResponse FromOperation(
        Guid requestId,
        MihomoChildOperationResult operation)
    {
        return operation.Succeeded
            ? Success(requestId, operation.Snapshot)
            : new MihomoServiceIpcResponse
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = requestId,
                Succeeded = false,
                ErrorCode = operation.ErrorCode ?? "service.ipc.command_failed",
                Snapshot = operation.Snapshot,
            };
    }

    private MihomoServiceIpcResponse FromBroker(
        Guid requestId,
        MihomoServiceControllerBrokerResult result)
    {
        if (!result.Succeeded || result.Payload is null)
        {
            return new MihomoServiceIpcResponse
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = requestId,
                Succeeded = false,
                ErrorCode = result.ErrorCode ?? "service.controller.request_failed",
                Snapshot = result.Snapshot,
            };
        }

        return new MihomoServiceIpcResponse
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = requestId,
            Succeeded = true,
            Snapshot = result.Snapshot,
            Logs = Array.Empty<string>(),
            EffectiveConfiguration = result.Payload.EffectiveConfiguration,
            ConnectionSnapshot = result.Payload.ConnectionSnapshot,
            ProxyRuntimeSnapshot = result.Payload.ProxyRuntimeSnapshot,
            RuntimeLogSnapshot = result.Payload.RuntimeLogSnapshot,
        };
    }

    private static MihomoServiceIpcResponse Success(
        Guid requestId,
        MihomoServiceIpcSnapshot snapshot,
        IReadOnlyList<string>? logs = null)
    {
        return new MihomoServiceIpcResponse
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = requestId,
            Succeeded = true,
            Snapshot = snapshot,
            Logs = logs ?? Array.Empty<string>(),
        };
    }

    private static void EnsureValidResponse(MihomoServiceIpcResponse response)
    {
        string? error = response.Validate();
        if (error is not null)
        {
            throw new InvalidDataException($"The service generated an invalid IPC response ({error}).");
        }
    }
}

/// <summary>Accepts one strict framed request per explicitly ACL-protected pipe connection.</summary>
internal sealed class MihomoServicePipeServer
{
    internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);
    private const int PipeBufferBytes = 64 * 1024;

    private readonly MihomoServiceOptions _options;
    private readonly MihomoServiceCommandProcessor _processor;
    private readonly MihomoServiceLogBuffer _logs;

    internal MihomoServicePipeServer(
        MihomoServiceOptions options,
        MihomoServiceCommandProcessor processor,
        MihomoServiceLogBuffer logs)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
    }

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        _logs.Append("service", "Authenticated named-pipe server starting.");
        while (!stoppingToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = CreateServerStream(_options);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsRecoverableConnectionFailure(exception))
            {
                _logs.Append(
                    "ipc",
                    exception is OperationCanceledException
                        ? "Pipe connection exceeded the 5-second frame/operation timeout."
                        : $"Pipe connection rejected ({exception.GetType().Name}).");
            }
        }

        _logs.Append("service", "Authenticated named-pipe server stopped.");
    }

    internal static NamedPipeServerStream CreateServerStream(MihomoServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PipeSecurity security = MihomoServicePipeSecurity.Create(options.AllowedSid);
        return NamedPipeServerStreamAcl.Create(
            options.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            PipeBufferBytes,
            PipeBufferBytes,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }

    private async Task ServeConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken);
        timeout.CancelAfter(ConnectionTimeout);
        MihomoServiceIpcRequest request = await MihomoServiceIpcFrameCodec
            .ReadRequestAsync(pipe, timeout.Token)
            .ConfigureAwait(false);
        MihomoServiceIpcResponse response = await _processor
            .ProcessAsync(request, timeout.Token)
            .ConfigureAwait(false);
        await MihomoServiceIpcFrameCodec
            .WriteResponseAsync(pipe, response, timeout.Token)
            .ConfigureAwait(false);
    }

    private static bool IsRecoverableConnectionFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or OperationCanceledException;
    }
}
