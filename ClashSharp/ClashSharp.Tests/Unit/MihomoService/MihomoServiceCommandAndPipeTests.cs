using System.IO.Pipes;
using System.Security.Principal;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Verifies authentication, command routing, correlation, and the real pipe transport.</summary>
public sealed class MihomoServiceCommandAndPipeTests
{
    [Fact]
    public void PipeServer_UsesFiveSecondWholeConnectionTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), MihomoServicePipeServer.ConnectionTimeout);
    }

    /// <summary>Verifies authentication is required before service state or version is disclosed.</summary>
    [Fact]
    public async Task ProcessAsync_RejectsWrongTokenWithoutSnapshot()
    {
        await using MihomoChildSupervisorTestContext context = new([]);
        MihomoServiceCommandProcessor processor = new(
            context.Options,
            context.Supervisor,
            context.Logs,
            context.ControllerBroker);
        Guid requestId = Guid.NewGuid();
        MihomoServiceIpcRequest request = CreateRequest(
            MihomoServiceIpcCommand.Hello,
            requestId) with
        {
            AuthenticationToken = new string('f', 64),
        };

        MihomoServiceIpcResponse response = await processor.ProcessAsync(
            request,
            CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("service.ipc.unauthorized", response.ErrorCode);
        Assert.Equal(requestId, response.RequestId);
        Assert.Null(response.Snapshot);
        Assert.Null(response.Validate());
    }

    /// <summary>Verifies incompatible versions receive a correlated current-version failure.</summary>
    [Fact]
    public async Task ProcessAsync_RejectsIncompatibleProtocolVersion()
    {
        await using MihomoChildSupervisorTestContext context = new([]);
        MihomoServiceCommandProcessor processor = new(
            context.Options,
            context.Supervisor,
            context.Logs,
            context.ControllerBroker);
        Guid requestId = Guid.NewGuid();
        MihomoServiceIpcRequest request = CreateRequest(
            MihomoServiceIpcCommand.Hello,
            requestId) with
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion + 1,
        };

        MihomoServiceIpcResponse response = await processor.ProcessAsync(
            request,
            CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("service.ipc.protocol_incompatible", response.ErrorCode);
        Assert.Equal(MihomoServiceIpcProtocol.CurrentVersion, response.ProtocolVersion);
        Assert.Equal(requestId, response.RequestId);
        Assert.NotNull(response.Snapshot);
        Assert.Null(response.Validate());
    }

    /// <summary>Verifies an empty correlation identity is rejected instead of generating a reply.</summary>
    [Fact]
    public async Task ProcessAsync_RejectsEmptyCorrelationIdentity()
    {
        await using MihomoChildSupervisorTestContext context = new([]);
        MihomoServiceCommandProcessor processor = new(
            context.Options,
            context.Supervisor,
            context.Logs,
            context.ControllerBroker);
        MihomoServiceIpcRequest request = CreateRequest(
            MihomoServiceIpcCommand.Status,
            Guid.Empty);

        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            request,
            CancellationToken.None));
    }

    /// <summary>Verifies every lifecycle and observation command is routed with one stable session.</summary>
    [Fact]
    public async Task ProcessAsync_RoutesHelloStartStatusReloadLogsAndStop()
    {
        FakeMihomoChildProcess first = new("first", 201);
        FakeMihomoChildProcess second = new("second", 202);
        await using MihomoChildSupervisorTestContext context = new([first, second]);
        MihomoServiceCommandProcessor processor = new(
            context.Options,
            context.Supervisor,
            context.Logs,
            context.ControllerBroker);
        MihomoServiceIpcResponse hello = await processor.ProcessAsync(
            CreateRequest(MihomoServiceIpcCommand.Hello),
            CancellationToken.None);
        Guid sessionId = Assert.IsType<MihomoServiceIpcSnapshot>(hello.Snapshot).SessionId;
        string firstHash = context.WriteConfiguration("mixed-port: 7601\n");

        MihomoServiceIpcResponse started = await processor.ProcessAsync(
            CreateRequest(MihomoServiceIpcCommand.Start) with
            {
                Generation = 61,
                ConfigurationHash = firstHash,
            },
            CancellationToken.None);
        MihomoServiceIpcResponse status = await processor.ProcessAsync(
            CreateRequest(MihomoServiceIpcCommand.Status),
            CancellationToken.None);
        string secondHash = context.WriteConfiguration("mixed-port: 7602\n");
        MihomoServiceIpcResponse reloaded = await processor.ProcessAsync(
            CreateRequest(MihomoServiceIpcCommand.Reload) with
            {
                Generation = 62,
                ConfigurationHash = secondHash,
            },
            CancellationToken.None);
        context.Logs.Append("test", "visible entry");
        MihomoServiceIpcResponse logs = await processor.ProcessAsync(
            CreateRequest(MihomoServiceIpcCommand.Logs) with
            {
                MaximumLogEntries = 10,
            },
            CancellationToken.None);
        MihomoServiceIpcResponse stopped = await processor.ProcessAsync(
            CreateRequest(MihomoServiceIpcCommand.Stop),
            CancellationToken.None);

        Assert.True(hello.Succeeded);
        Assert.True(started.Succeeded);
        Assert.True(status.Succeeded);
        Assert.True(reloaded.Succeeded);
        Assert.True(logs.Succeeded);
        Assert.True(stopped.Succeeded);
        Assert.Equal(MihomoServiceChildState.Running, status.Snapshot?.ChildState);
        Assert.Equal(62, reloaded.Snapshot?.ActiveGeneration);
        Assert.Contains(logs.Logs, entry => entry.Contains("visible entry", StringComparison.Ordinal));
        Assert.Equal(MihomoServiceChildState.Stopped, stopped.Snapshot?.ChildState);
        Assert.All(
            new[] { hello, started, status, reloaded, logs, stopped },
            response =>
            {
                Assert.Equal(sessionId, response.Snapshot?.SessionId);
                Assert.Null(response.Validate());
            });
    }

    /// <summary>Verifies the explicit-ACL server completes a strict framed Hello on Windows.</summary>
    [Fact]
    public async Task PipeServer_CompletesFramedHelloForAllowedUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier sid = identity.User
            ?? throw new InvalidOperationException("The test identity has no user SID.");
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            return;
        }

        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            .PadRight(64, '0')
            .ToLowerInvariant();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(
            temporaryDirectory.Path,
            sid,
            token);
        MihomoServiceLogBuffer logs = new(options);
        MihomoRuntimeLogBuffer runtimeLogs = new(logs);
        await using MihomoChildSupervisor supervisor = new(
            options,
            new MihomoGenerationStore(options, protectDirectory: false),
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false),
            new FakeMihomoChildProcessLauncher([]),
            new FakeMihomoControllerReadinessProbe(),
            logs,
            runtimeLogs,
            startupObservationDelay: TimeSpan.Zero,
            restartBackoffs: [TimeSpan.Zero],
            stopTimeout: TimeSpan.FromSeconds(1),
            serviceVersion: "pipe-test");
        MihomoServicePipeServer server = new(
            options,
            new MihomoServiceCommandProcessor(
                options,
                supervisor,
                logs,
                new MihomoServiceControllerBroker(
                    supervisor,
                    new MihomoNamedPipeControllerTransportFactory(),
                    runtimeLogs,
                    logs)),
            logs);
        using CancellationTokenSource stopping = new();
        Task serverTask = server.RunAsync(stopping.Token);
        try
        {
            await using NamedPipeClientStream client = new(
                ".",
                options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Guid requestId = Guid.NewGuid();
            await MihomoServiceIpcFrameCodec.WriteRequestAsync(
                client,
                CreateRequest(MihomoServiceIpcCommand.Hello, requestId, token),
                CancellationToken.None);
            MihomoServiceIpcResponse response = await MihomoServiceIpcFrameCodec
                .ReadResponseAsync(client, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(response.Succeeded);
            Assert.Equal(requestId, response.RequestId);
            Assert.Equal(MihomoServiceIpcProtocol.CurrentVersion, response.ProtocolVersion);
            Assert.Equal("pipe-test", response.Snapshot?.ServiceVersion);
            Assert.Equal(MihomoServiceChildState.Stopped, response.Snapshot?.ChildState);
            Assert.Null(response.Validate());
        }
        finally
        {
            stopping.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static MihomoServiceIpcRequest CreateRequest(
        MihomoServiceIpcCommand command,
        Guid? requestId = null,
        string? token = null)
    {
        return new MihomoServiceIpcRequest
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = requestId ?? Guid.NewGuid(),
            AuthenticationToken = token ?? MihomoServiceTestSupport.Token,
            Command = command,
        };
    }
}
