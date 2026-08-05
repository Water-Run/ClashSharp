using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.MihomoService;

public sealed class MihomoServiceControllerBrokerTests
{
    [Fact]
    public async Task GetConnections_UsesFixedRouteAndReturnsBoundedProjection()
    {
        FakeMihomoChildProcess process = new("broker", 501);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7890\nmode: rule\n");
        Assert.True((await context.Supervisor.StartAsync(
            70,
            hash,
            CancellationToken.None)).Succeeded);
        RecordingTransportFactory transport = new(
        [
            Step.Json(HttpMethod.Get, "/connections", """
                {
                  "connections": [{
                    "id": "connection-1",
                    "metadata": {
                      "host": "example.invalid\tname",
                      "process": "C:\\Users\\person\\client.exe",
                      "sourceIP": "192.0.2.10"
                    },
                    "rule": "MATCH",
                    "rulePayload": "",
                    "chains": ["Proxy A"],
                    "upload": 12,
                    "download": 34,
                    "start": "2026-08-04T12:00:00+08:00"
                  }]
                }
                """),
        ]);
        MihomoServiceControllerBroker broker = CreateBroker(context, transport);
        MihomoServiceIpcRequest request = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.GetConnections);

        MihomoServiceControllerBrokerResult result = await broker.ExecuteAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        MihomoServiceIpcConnection connection = Assert.Single(
            result.Payload!.ConnectionSnapshot!.Connections);
        Assert.Equal("connection-1", connection.Id);
        Assert.Equal("client.exe", connection.ProcessName);
        Assert.Equal("example.invalid name", connection.Host);
        Assert.Equal("Proxy A", connection.ProxyName);
        Assert.DoesNotContain("Users", connection.ProcessName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(connection.Host, char.IsControl);
        Assert.Equal([(HttpMethod.Get, "/connections")], transport.RequestShapes);
        Assert.Null(result.Payload.ConnectionSnapshot.Validate());
    }

    [Fact]
    public async Task ProxySnapshotAndSelection_UseOnlyTypedFixedRoutes()
    {
        FakeMihomoChildProcess process = new("broker", 502);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7890\n");
        Assert.True((await context.Supervisor.StartAsync(
            71,
            hash,
            CancellationToken.None)).Succeeded);
        const string proxies = """
            {"proxies":{"Choose Group":{"name":"Choose Group","type":"Selector","now":"Node/One","all":["Node/One","DIRECT"]}}}
            """;
        RecordingTransportFactory snapshotTransport = new(
        [
            Step.Json(HttpMethod.Get, "/proxies", proxies),
            Step.Json(HttpMethod.Get, "/providers/proxies", "{\"providers\":{\"remote\":{\"vehicleType\":\"HTTP\",\"proxies\":[{}]}}}"),
            Step.Json(HttpMethod.Get, "/providers/rules", "{\"providers\":{\"rules\":{\"vehicleType\":\"Inline\",\"behavior\":\"Domain\",\"ruleCount\":3}}}"),
        ]);
        MihomoServiceControllerBroker snapshotBroker = CreateBroker(context, snapshotTransport);

        MihomoServiceControllerBrokerResult snapshot = await snapshotBroker.ExecuteAsync(
            BrokerRequest(
                context.Supervisor.GetSnapshot(),
                MihomoServiceIpcCommand.GetProxyRuntimeSnapshot),
            CancellationToken.None);

        Assert.True(snapshot.Succeeded);
        Assert.Single(snapshot.Payload!.ProxyRuntimeSnapshot!.Groups);
        Assert.Equal(2, snapshot.Payload.ProxyRuntimeSnapshot.Providers.Count);
        Assert.Null(snapshot.Payload.ProxyRuntimeSnapshot.Validate());

        RecordingTransportFactory selectionTransport = new(
        [
            Step.Json(HttpMethod.Get, "/proxies", proxies),
            new Step(
                HttpMethod.Put,
                "/proxies/Choose%20Group",
                new MihomoControllerHttpResponse(HttpStatusCode.NoContent, ReadOnlyMemory<byte>.Empty)),
        ]);
        MihomoServiceControllerBroker selectionBroker = CreateBroker(context, selectionTransport);
        MihomoServiceIpcRequest selection = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.SelectProxy) with
        {
            ProxySelection = new MihomoServiceIpcProxySelection
            {
                GroupName = "Choose Group",
                ProxyName = "Node/One",
            },
        };

        MihomoServiceControllerBrokerResult selected = await selectionBroker.ExecuteAsync(
            selection,
            CancellationToken.None);

        Assert.True(selected.Succeeded);
        RecordedControllerRequest put = Assert.Single(
            selectionTransport.Requests,
            item => item.Method == HttpMethod.Put);
        Assert.Equal("/proxies/Choose%20Group", put.Path);
        Assert.Equal("{\"name\":\"Node/One\"}", Encoding.UTF8.GetString(put.Body.Span));
    }

    [Fact]
    public async Task StaleBindingAndInvalidSelection_PerformNoUnsafeWrite()
    {
        FakeMihomoChildProcess process = new("broker", 503);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7890\n");
        Assert.True((await context.Supervisor.StartAsync(
            72,
            hash,
            CancellationToken.None)).Succeeded);
        RecordingTransportFactory staleTransport = new([]);
        MihomoServiceControllerBroker staleBroker = CreateBroker(context, staleTransport);
        MihomoServiceIpcRequest stale = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.CloseAllConnections) with
        {
            ExpectedRuntime = new MihomoServiceIpcControllerBinding
            {
                ServiceSessionId = context.Supervisor.GetSnapshot().SessionId,
                Generation = 999,
                ConfigurationHash = hash,
            },
        };

        MihomoServiceControllerBrokerResult staleResult = await staleBroker.ExecuteAsync(
            stale,
            CancellationToken.None);

        Assert.False(staleResult.Succeeded);
        Assert.Equal("service.controller.stale_generation", staleResult.ErrorCode);
        Assert.Empty(staleTransport.Requests);

        RecordingTransportFactory selectionTransport = new(
        [
            Step.Json(
                HttpMethod.Get,
                "/proxies",
                "{\"proxies\":{\"group\":{\"type\":\"Selector\",\"now\":\"safe\",\"all\":[\"safe\"]}}}"),
        ]);
        MihomoServiceControllerBroker selectionBroker = CreateBroker(context, selectionTransport);
        MihomoServiceIpcRequest invalidSelection = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.SelectProxy) with
        {
            ProxySelection = new MihomoServiceIpcProxySelection
            {
                GroupName = "group",
                ProxyName = "not-a-member",
            },
        };

        MihomoServiceControllerBrokerResult invalid = await selectionBroker.ExecuteAsync(
            invalidSelection,
            CancellationToken.None);

        Assert.False(invalid.Succeeded);
        Assert.Equal("service.controller.proxy_selection_invalid", invalid.ErrorCode);
        Assert.DoesNotContain(selectionTransport.Requests, item => item.Method == HttpMethod.Put);
    }

    [Theory]
    [InlineData(MihomoServiceIpcProviderKind.Proxy, "/providers/proxies/subscription")]
    [InlineData(MihomoServiceIpcProviderKind.Rule, "/providers/rules/regional%20rules")]
    public async Task UpdateProvider_UsesOnlyTypedProviderNamespace(
        MihomoServiceIpcProviderKind kind,
        string expectedPath)
    {
        FakeMihomoChildProcess process = new("broker", 507);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7890\n");
        Assert.True((await context.Supervisor.StartAsync(
            75,
            hash,
            CancellationToken.None)).Succeeded);
        RecordingTransportFactory transport = new(
        [
            new Step(
                HttpMethod.Put,
                expectedPath,
                new MihomoControllerHttpResponse(HttpStatusCode.NoContent, ReadOnlyMemory<byte>.Empty)),
        ]);
        MihomoServiceControllerBroker broker = CreateBroker(context, transport);
        string name = kind == MihomoServiceIpcProviderKind.Proxy
            ? "subscription"
            : "regional rules";
        MihomoServiceIpcRequest request = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.UpdateProvider) with
        {
            ProviderUpdate = new MihomoServiceIpcProviderUpdate
            {
                Kind = kind,
                Name = name,
            },
        };

        MihomoServiceControllerBrokerResult result = await broker.ExecuteAsync(
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([(HttpMethod.Put, expectedPath)], transport.RequestShapes);
    }

    [Fact]
    public async Task CommandProcessor_ProjectsReadyStateAndOnlyChildRuntimeLogs()
    {
        FakeMihomoChildProcess process = new("broker", 504);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7890\nmode: global\n");
        Assert.True((await context.Supervisor.StartAsync(
            73,
            hash,
            CancellationToken.None)).Succeeded);
        MihomoControllerAuthority authority = Assert.Single(context.ReadinessProbe.Calls).Authority;
        context.Logs.Append("service", "service-only-log");
        context.RuntimeLogs.Append(
            "stdout",
            $"info runtime\tline secret={authority.Secret} pipe={authority.PipeName}");
        RecordingTransportFactory transport = new([]);
        MihomoServiceControllerBroker broker = CreateBroker(context, transport);
        MihomoServiceCommandProcessor processor = new(
            context.Options,
            context.Supervisor,
            context.Logs,
            broker);
        MihomoServiceIpcRequest probeRequest = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration);

        MihomoServiceIpcResponse probe = await processor.ProcessAsync(
            probeRequest,
            CancellationToken.None);
        MihomoServiceIpcRequest logsRequest = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.GetRuntimeLogs) with
        {
            RuntimeLogQuery = new MihomoServiceIpcRuntimeLogQuery
            {
                AfterSequence = 0,
                MaximumEntries = 10,
            },
        };
        MihomoServiceIpcResponse logs = await processor.ProcessAsync(
            logsRequest,
            CancellationToken.None);

        Assert.True(probe.Succeeded);
        Assert.True(probe.EffectiveConfiguration!.ControllerReady);
        Assert.Equal(MihomoServiceIpcRoutingMode.Global, probe.EffectiveConfiguration.Mode);
        Assert.Null(probe.ValidateFor(probeRequest));
        MihomoServiceIpcRuntimeLogEntry runtime = Assert.Single(logs.RuntimeLogSnapshot!.Entries);
        Assert.DoesNotContain("service-only-log", runtime.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(authority.Secret, runtime.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(authority.PipeName, runtime.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runtime.Message, char.IsControl);
        Assert.Contains("[redacted]", runtime.Message, StringComparison.Ordinal);
        Assert.Null(logs.ValidateFor(logsRequest));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task BrokerResponse_KeepsGateBoundSnapshotDuringConcurrentStop()
    {
        FakeMihomoChildProcess process = new("broker", 505);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7890\n");
        Assert.True((await context.Supervisor.StartAsync(
            74,
            hash,
            CancellationToken.None)).Succeeded);
        BlockingDeleteTransportFactory transport = new();
        MihomoServiceCommandProcessor processor = new(
            context.Options,
            context.Supervisor,
            context.Logs,
            CreateBroker(context, transport));
        MihomoServiceIpcRequest request = BrokerRequest(
            context.Supervisor.GetSnapshot(),
            MihomoServiceIpcCommand.CloseAllConnections);

        Task<MihomoServiceIpcResponse> brokerCall = processor.ProcessAsync(
            request,
            CancellationToken.None);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MihomoChildOperationResult> stop = context.Supervisor.StopAsync(
            CancellationToken.None);
        Assert.False(stop.IsCompleted);
        transport.Release.SetResult(null);

        MihomoServiceIpcResponse response = await brokerCall.WaitAsync(TimeSpan.FromSeconds(5));
        MihomoChildOperationResult stopped = await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Succeeded);
        Assert.Equal(MihomoServiceChildState.Running, response.Snapshot!.ChildState);
        Assert.Equal(74, response.Snapshot.ActiveGeneration);
        Assert.Null(response.ValidateFor(request));
        Assert.Equal(MihomoServiceChildState.Stopped, stopped.Snapshot.ChildState);
    }

    private static MihomoServiceControllerBroker CreateBroker(
        MihomoChildSupervisorTestContext context,
        IMihomoControllerTransportFactory transport) =>
        new(context.Supervisor, transport, context.RuntimeLogs, context.Logs);

    private static MihomoServiceIpcRequest BrokerRequest(
        MihomoServiceIpcSnapshot snapshot,
        MihomoServiceIpcCommand command) =>
        new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = MihomoServiceTestSupport.Token,
            Command = command,
            ExpectedRuntime = new MihomoServiceIpcControllerBinding
            {
                ServiceSessionId = snapshot.SessionId,
                Generation = snapshot.ActiveGeneration!.Value,
                ConfigurationHash = snapshot.ActiveConfigurationHash!,
            },
        };

    private sealed record Step(
        HttpMethod Method,
        string Path,
        MihomoControllerHttpResponse Response)
    {
        internal static Step Json(HttpMethod method, string path, string json) =>
            new(
                method,
                path,
                new MihomoControllerHttpResponse(
                    HttpStatusCode.OK,
                    Encoding.UTF8.GetBytes(json)));
    }

    private sealed record RecordedControllerRequest(
        HttpMethod Method,
        string Path,
        ReadOnlyMemory<byte> Body);

    private sealed class RecordingTransportFactory : IMihomoControllerTransportFactory
    {
        private readonly ConcurrentQueue<Step> _steps;
        private readonly ConcurrentQueue<RecordedControllerRequest> _requests = new();

        internal RecordingTransportFactory(IEnumerable<Step> steps)
        {
            _steps = new ConcurrentQueue<Step>(steps);
        }

        internal IReadOnlyList<RecordedControllerRequest> Requests => _requests.ToArray();

        internal IReadOnlyList<(HttpMethod Method, string Path)> RequestShapes =>
            _requests.Select(item => (item.Method, item.Path)).ToArray();

        public IMihomoControllerTransport Create(
            MihomoControllerAuthority authority,
            int expectedProcessId) => new RecordingTransport(this);

        private sealed class RecordingTransport : IMihomoControllerTransport
        {
            private readonly RecordingTransportFactory _owner;

            internal RecordingTransport(RecordingTransportFactory owner)
            {
                _owner = owner;
            }

            public Task<MihomoControllerHttpResponse> SendAsync(
                HttpMethod method,
                string relativePath,
                ReadOnlyMemory<byte>? jsonContent,
                int maximumResponseBytes,
                CancellationToken cancellationToken)
            {
                if (!_owner._steps.TryDequeue(out Step? step))
                {
                    throw new InvalidOperationException("No fake controller step remains.");
                }

                Assert.Equal(step.Method, method);
                Assert.Equal(step.Path, relativePath);
                ReadOnlyMemory<byte> body = jsonContent ?? ReadOnlyMemory<byte>.Empty;
                _owner._requests.Enqueue(new RecordedControllerRequest(method, relativePath, body));
                return Task.FromResult(step.Response);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDeleteTransportFactory : IMihomoControllerTransportFactory
    {
        internal TaskCompletionSource<object?> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<object?> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IMihomoControllerTransport Create(
            MihomoControllerAuthority authority,
            int expectedProcessId) => new BlockingTransport(this);

        private sealed class BlockingTransport : IMihomoControllerTransport
        {
            private readonly BlockingDeleteTransportFactory _owner;

            internal BlockingTransport(BlockingDeleteTransportFactory owner)
            {
                _owner = owner;
            }

            public async Task<MihomoControllerHttpResponse> SendAsync(
                HttpMethod method,
                string relativePath,
                ReadOnlyMemory<byte>? jsonContent,
                int maximumResponseBytes,
                CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Delete, method);
                Assert.Equal("/connections", relativePath);
                _owner.Entered.TrySetResult(null);
                await _owner.Release.Task.WaitAsync(cancellationToken);
                return new MihomoControllerHttpResponse(
                    HttpStatusCode.NoContent,
                    ReadOnlyMemory<byte>.Empty);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
