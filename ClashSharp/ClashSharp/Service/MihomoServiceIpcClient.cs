using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Sends one correlated authenticated command to the mihomo Windows service.</summary>
internal interface IMihomoServiceIpcClient
{
    /// <summary>Sends a validated request and returns a validated correlated response.</summary>
    Task<MihomoServiceIpcResponse> SendAsync(
        MihomoServiceIpcRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Uses a bounded owner/deployment-specific named pipe for App-to-service commands.</summary>
internal sealed class NamedPipeMihomoServiceIpcClient : IMihomoServiceIpcClient
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly IMihomoServicePipeServerIdentityVerifier _serverIdentityVerifier;
    private readonly TimeSpan _operationTimeout;

    /// <summary>Initializes a client for one deployment-specific pipe.</summary>
    internal NamedPipeMihomoServiceIpcClient(
        string pipeName,
        IMihomoServicePipeServerIdentityVerifier serverIdentityVerifier,
        TimeSpan? operationTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        TimeSpan timeout = operationTimeout ?? DefaultOperationTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _pipeName = pipeName;
        _serverIdentityVerifier = serverIdentityVerifier
            ?? throw new ArgumentNullException(nameof(serverIdentityVerifier));
        _operationTimeout = timeout;
    }

    /// <inheritdoc />
    public async Task<MihomoServiceIpcResponse> SendAsync(
        MihomoServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? requestError = request.Validate();
        if (requestError is not null)
        {
            throw new ArgumentException(
                $"The service IPC request is invalid ({requestError}).",
                nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            // CurrentUserOnly cannot be used here: the Windows service runs as
            // LocalSystem and grants the interactive owner through an explicit ACL.
            await using NamedPipeClientStream pipe = new(
                serverName: ".",
                pipeName: _pipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous,
                impersonationLevel: TokenImpersonationLevel.Anonymous,
                inheritability: HandleInheritability.None);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            // Connecting does not authenticate a stable pipe name. Bind this exact
            // handle to the running SCM service before sending credentials or commands.
            _serverIdentityVerifier.Verify(pipe.SafePipeHandle);
            await MihomoServiceIpcFrameCodec
                .WriteRequestAsync(pipe, request, timeout.Token)
                .ConfigureAwait(false);
            MihomoServiceIpcResponse response = await MihomoServiceIpcFrameCodec
                .ReadResponseAsync(pipe, timeout.Token)
                .ConfigureAwait(false);
            ValidateResponse(request, response);
            return response;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The mihomo service IPC operation did not complete before its deadline.",
                exception);
        }
    }

    private static void ValidateResponse(
        MihomoServiceIpcRequest request,
        MihomoServiceIpcResponse response)
    {
        string? responseError = response.ValidateFor(request);
        if (responseError is not null)
        {
            throw new InvalidDataException(
                $"The mihomo service IPC response is invalid ({responseError}).");
        }
    }
}
