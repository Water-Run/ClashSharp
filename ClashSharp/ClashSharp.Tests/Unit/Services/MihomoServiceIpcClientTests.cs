using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using ClashSharp.Service;
using ClashSharp.ServiceProtocol;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies the App side of the local mihomo service pipe.</summary>
public sealed class MihomoServiceIpcClientTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Verifies composition binds the pipe to both a valid Windows owner and deployment token.</summary>
    [Fact]
    public void EndpointCreate_ValidIdentity_BuildsBoundPipeName()
    {
        MihomoServiceIpcEndpoint endpoint = MihomoServiceIpcEndpoint.Create(
            "S-1-5-21-100-200-300-1001",
            Token);

        Assert.Equal("S-1-5-21-100-200-300-1001", endpoint.UserSid);
        Assert.Equal(Token, endpoint.AuthenticationToken);
        Assert.Equal(
            MihomoServiceIpcProtocol.BuildPipeName(endpoint.UserSid, Token),
            endpoint.PipeName);
    }

    /// <summary>Verifies the production Win32 seam reads the server PID from a client pipe handle.</summary>
    [Fact]
    public async Task NativeIdentityApi_ConnectedClientHandle_ReturnsServerProcessId()
    {
        string pipeName = "ClashSharp.Test.NativeIdentity." + Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using NamedPipeClientStream client = new(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous,
            HandleInheritability.None);

        Task serverConnection = server.WaitForConnectionAsync(CancellationToken.None);
        await client.ConnectAsync(CancellationToken.None);
        await serverConnection;

        uint serverProcessId = WindowsMihomoServiceIdentityNativeApi.Instance
            .GetNamedPipeServerProcessId(client.SafePipeHandle);

        Assert.Equal((uint)Environment.ProcessId, serverProcessId);
    }

    /// <summary>Verifies a correlated strict response is returned to the caller.</summary>
    [Fact]
    public async Task SendAsync_CorrelatedResponse_RoundTrips()
    {
        string pipeName = "ClashSharp.Test." + Guid.NewGuid().ToString("N");
        MihomoServiceIpcRequest request = CreateRequest();
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Task serverTask = ServeOnceAsync(server, request.RequestId);
        const uint processId = 41001;
        FakeMihomoServiceIdentityNativeApi nativeApi = new()
        {
            PipeServerProcessId = processId,
            ServiceStatus = new MihomoWindowsServiceProcessStatus(
                WindowsMihomoServicePipeServerIdentityVerifier.OwnProcessServiceType,
                WindowsMihomoServicePipeServerIdentityVerifier.RunningServiceState,
                processId),
        };
        NamedPipeMihomoServiceIpcClient client = new(
            pipeName,
            new WindowsMihomoServicePipeServerIdentityVerifier(
                MihomoServiceManager.ServiceName,
                nativeApi),
            TimeSpan.FromSeconds(2));

        MihomoServiceIpcResponse response = await client.SendAsync(
            request,
            CancellationToken.None);
        await serverTask;

        Assert.True(response.Succeeded);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(MihomoServiceChildState.Stopped, response.Snapshot?.ChildState);
        Assert.Equal(MihomoServiceManager.ServiceName, nativeApi.QueriedServiceName);
    }

    /// <summary>Verifies every untrusted SCM/PID observation is rejected before credentials are written.</summary>
    [Theory]
    [InlineData(0x20, 0x04, 41001, 41001)]
    [InlineData(0x10, 0x01, 41001, 41001)]
    [InlineData(0x10, 0x04, 0, 41001)]
    [InlineData(0x10, 0x04, 41002, 41001)]
    [InlineData(0x10, 0x04, 41001, 0)]
    public async Task SendAsync_UntrustedServiceIdentity_WritesZeroBytes(
        int serviceType,
        int serviceState,
        int serviceProcessId,
        int pipeServerProcessId)
    {
        FakeMihomoServiceIdentityNativeApi nativeApi = new()
        {
            PipeServerProcessId = (uint)pipeServerProcessId,
            ServiceStatus = new MihomoWindowsServiceProcessStatus(
                (uint)serviceType,
                (uint)serviceState,
                (uint)serviceProcessId),
        };
        WindowsMihomoServicePipeServerIdentityVerifier verifier = new(
            MihomoServiceManager.ServiceName,
            nativeApi);

        Exception failure = await SendAndObserveZeroBytesAsync(verifier);

        Assert.IsType<MihomoServicePipeServerIdentityException>(failure);
        Assert.Equal(MihomoServiceManager.ServiceName, nativeApi.QueriedServiceName);
    }

    /// <summary>Verifies either native identity query can fail without disclosing request bytes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendAsync_IdentityApiFailure_WritesZeroBytes(bool pipeQueryFails)
    {
        IOException nativeFailure = new("native identity query failed");
        FakeMihomoServiceIdentityNativeApi nativeApi = new()
        {
            PipeServerProcessId = 41001,
            ServiceStatus = new MihomoWindowsServiceProcessStatus(
                WindowsMihomoServicePipeServerIdentityVerifier.OwnProcessServiceType,
                WindowsMihomoServicePipeServerIdentityVerifier.RunningServiceState,
                41001),
            PipeQueryFailure = pipeQueryFails ? nativeFailure : null,
            ServiceQueryFailure = pipeQueryFails ? null : nativeFailure,
        };
        WindowsMihomoServicePipeServerIdentityVerifier verifier = new(
            MihomoServiceManager.ServiceName,
            nativeApi);

        Exception failure = await SendAndObserveZeroBytesAsync(verifier);

        Assert.Same(nativeFailure, failure);
    }

    /// <summary>
    /// Verifies SECURITY_ANONYMOUS prevents a deliberately allowed server from identifying the App.
    /// </summary>
    [Fact]
    public async Task SendAsync_AnonymousImpersonation_DoesNotExposeAppIdentity()
    {
        // Load identity dependencies before entering an anonymous impersonation context; otherwise
        // the runtime can cache a failed assembly bind performed without filesystem credentials.
        using (WindowsIdentity.GetCurrent())
        {
        }

        string pipeName = "ClashSharp.Test." + Guid.NewGuid().ToString("N");
        MihomoServiceIpcRequest request = CreateRequest();
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        bool obtainedNonAnonymousIdentity = false;
        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            _ = await MihomoServiceIpcFrameCodec.ReadRequestAsync(
                server,
                CancellationToken.None);
            try
            {
                server.RunAsClient(() =>
                {
                    try
                    {
                        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                        obtainedNonAnonymousIdentity = !identity.IsAnonymous;
                    }
                    catch (Exception exception) when (exception is IOException
                        or InvalidOperationException
                        or SecurityException
                        or UnauthorizedAccessException)
                    {
                        // SECURITY_ANONYMOUS deliberately prevents opening an impersonation token.
                    }
                });
            }
            catch (Exception exception) when (exception is IOException
                or InvalidOperationException
                or SecurityException
                or UnauthorizedAccessException)
            {
                // Some Windows versions reject RunAsClient before invoking the callback.
            }

            await WriteSuccessfulResponseAsync(server, request.RequestId);
        });
        NamedPipeMihomoServiceIpcClient client = new(
            pipeName,
            AcceptingMihomoServicePipeServerIdentityVerifier.Instance,
            TimeSpan.FromSeconds(2));

        _ = await client.SendAsync(request, CancellationToken.None);
        await serverTask;

        Assert.False(obtainedNonAnonymousIdentity);
    }

    /// <summary>Verifies a response for another request is rejected as an integrity failure.</summary>
    [Fact]
    public async Task SendAsync_MismatchedRequestId_RejectsResponse()
    {
        string pipeName = "ClashSharp.Test." + Guid.NewGuid().ToString("N");
        MihomoServiceIpcRequest request = CreateRequest();
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Task serverTask = ServeOnceAsync(server, Guid.NewGuid());
        NamedPipeMihomoServiceIpcClient client = new(
            pipeName,
            AcceptingMihomoServicePipeServerIdentityVerifier.Instance,
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SendAsync(request, CancellationToken.None));
        await serverTask;
    }

    /// <summary>Verifies a correlated response from an incompatible service build is rejected.</summary>
    [Fact]
    public async Task SendAsync_IncompatibleProtocolVersion_RejectsResponse()
    {
        string pipeName = "ClashSharp.Test." + Guid.NewGuid().ToString("N");
        MihomoServiceIpcRequest request = CreateRequest();
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Task serverTask = ServeOnceAsync(
            server,
            request.RequestId,
            MihomoServiceIpcProtocol.CurrentVersion + 1);
        NamedPipeMihomoServiceIpcClient client = new(
            pipeName,
            AcceptingMihomoServicePipeServerIdentityVerifier.Instance,
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SendAsync(request, CancellationToken.None));
        await serverTask;
    }

    /// <summary>Verifies an absent service endpoint is converted into a bounded timeout.</summary>
    [Fact]
    public async Task SendAsync_NoServer_TimesOut()
    {
        NamedPipeMihomoServiceIpcClient client = new(
            "ClashSharp.Test." + Guid.NewGuid().ToString("N"),
            AcceptingMihomoServicePipeServerIdentityVerifier.Instance,
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SendAsync(CreateRequest(), CancellationToken.None));
    }

    private static MihomoServiceIpcRequest CreateRequest()
    {
        return new MihomoServiceIpcRequest
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = Token,
            Command = MihomoServiceIpcCommand.Hello,
        };
    }

    private static async Task<Exception> SendAndObserveZeroBytesAsync(
        IMihomoServicePipeServerIdentityVerifier verifier)
    {
        string pipeName = "ClashSharp.Test.Identity." + Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Task<int> serverRead = ReadOneByteAsync(server);
        NamedPipeMihomoServiceIpcClient client = new(
            pipeName,
            verifier,
            TimeSpan.FromSeconds(2));

        Exception? failure = await Record.ExceptionAsync(() =>
            client.SendAsync(CreateRequest(), CancellationToken.None));
        int bytesRead = await serverRead.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(failure);
        Assert.Equal(0, bytesRead);
        return failure;
    }

    private static async Task<int> ReadOneByteAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync(CancellationToken.None);
        byte[] buffer = new byte[1];
        return await server.ReadAsync(buffer, CancellationToken.None);
    }

    private static async Task ServeOnceAsync(
        NamedPipeServerStream server,
        Guid responseRequestId,
        int responseProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion)
    {
        await server.WaitForConnectionAsync();
        _ = await MihomoServiceIpcFrameCodec.ReadRequestAsync(
            server,
            CancellationToken.None);
        await WriteSuccessfulResponseAsync(server, responseRequestId, responseProtocolVersion);
    }

    private static Task WriteSuccessfulResponseAsync(
        NamedPipeServerStream server,
        Guid responseRequestId,
        int responseProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion)
    {
        MihomoServiceIpcResponse response = new()
        {
            ProtocolVersion = responseProtocolVersion,
            RequestId = responseRequestId,
            Succeeded = true,
            Snapshot = new MihomoServiceIpcSnapshot
            {
                SessionId = Guid.NewGuid(),
                ServiceVersion = "test",
                ChildState = MihomoServiceChildState.Stopped,
            },
        };
        return MihomoServiceIpcFrameCodec.WriteResponseAsync(
            server,
            response,
            CancellationToken.None);
    }

    private sealed class AcceptingMihomoServicePipeServerIdentityVerifier
        : IMihomoServicePipeServerIdentityVerifier
    {
        internal static AcceptingMihomoServicePipeServerIdentityVerifier Instance { get; } = new();

        public void Verify(SafePipeHandle connectedPipeHandle)
        {
            Assert.False(connectedPipeHandle.IsClosed);
            Assert.False(connectedPipeHandle.IsInvalid);
        }
    }

    private sealed class FakeMihomoServiceIdentityNativeApi
        : IMihomoServiceIdentityNativeApi
    {
        internal uint PipeServerProcessId { get; init; }

        internal MihomoWindowsServiceProcessStatus ServiceStatus { get; init; }

        internal Exception? PipeQueryFailure { get; init; }

        internal Exception? ServiceQueryFailure { get; init; }

        internal string? QueriedServiceName { get; private set; }

        public uint GetNamedPipeServerProcessId(SafePipeHandle connectedPipeHandle)
        {
            Assert.False(connectedPipeHandle.IsClosed);
            Assert.False(connectedPipeHandle.IsInvalid);
            if (PipeQueryFailure is not null)
            {
                throw PipeQueryFailure;
            }

            return PipeServerProcessId;
        }

        public MihomoWindowsServiceProcessStatus QueryServiceProcessStatus(
            string serviceName)
        {
            QueriedServiceName = serviceName;
            if (ServiceQueryFailure is not null)
            {
                throw ServiceQueryFailure;
            }

            return ServiceStatus;
        }
    }
}
