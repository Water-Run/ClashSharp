using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for mihomo external-controller client behavior.</summary>
public sealed class MihomoControllerClientTests
{
    private const string ControllerSecret = "controller-test-secret";

    /// <summary>Verifies the production local client cannot forward controller credentials through a proxy.</summary>
    [Fact]
    public void CreateLocalHttpClient_DisablesProxyResolution()
    {
        using SocketsHttpHandler handler = MihomoControllerClient.CreateLocalHttpMessageHandler();
        using HttpClient client = MihomoControllerClient.CreateLocalHttpClient();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
    }

    /// <summary>Verifies every controller request carries the configured bearer credential.</summary>
    [Fact]
    public async Task GetActiveConnectionsAsync_WithSecret_SendsBearerAuthorization()
    {
        RecordingHttpHandler handler = new("""{"connections":[]}""");
        MihomoControllerClient client = new(
            new HttpClient(handler),
            new Uri("http://127.0.0.1:9090"),
            ControllerSecret);

        await client.GetActiveConnectionsAsync(CancellationToken.None);

        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal(ControllerSecret, handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task MatchesRuntimeConfigurationAsync_MatchingAuthenticatedConfig_ReturnsTrue()
    {
        RecordingHttpHandler handler = new("""
            {
              "mixed-port": 17890,
              "mode": "rule",
              "tun": { "enable": true }
            }
            """);
        MihomoControllerClient client = new(
            new HttpClient(handler),
            new Uri("http://127.0.0.1:9090"),
            ControllerSecret);
        RuntimeConfigurationActivationPlan plan = new(
            ClashSharpMode.RuleTakeover,
            TunEnabled: true,
            MixedPort: 17890,
            ProfileId: "profile-one");

        bool matches = await client.MatchesRuntimeConfigurationAsync(plan, CancellationToken.None);

        Assert.True(matches);
        Assert.Equal("/configs", handler.Requests[0].RequestUri?.AbsolutePath);
        Assert.Equal(ControllerSecret, handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task MatchesRuntimeConfigurationAsync_StaleTunPlan_ReturnsFalse()
    {
        RecordingHttpHandler handler = new("""
            {
              "mixed-port": 17890,
              "mode": "rule",
              "tun": { "enable": false }
            }
            """);
        MihomoControllerClient client = new(
            new HttpClient(handler),
            new Uri("http://127.0.0.1:9090"),
            ControllerSecret);
        RuntimeConfigurationActivationPlan plan = new(
            ClashSharpMode.RuleTakeover,
            TunEnabled: true,
            MixedPort: 17890,
            ProfileId: "profile-one");

        bool matches = await client.MatchesRuntimeConfigurationAsync(plan, CancellationToken.None);

        Assert.False(matches);
    }

    /// <summary>Verifies WebSocket snapshots use the same bounded connection model as REST snapshots.</summary>
    [Fact]
    public void ParseActiveConnectionsPayload_ParsesLiveSnapshot()
    {
        byte[] payload = Encoding.UTF8.GetBytes("""
            {
              "connections": [
                {
                  "id": "live-1",
                  "metadata": { "process": "browser", "host": "example.com" },
                  "rule": "DOMAIN",
                  "rulePayload": "example.com",
                  "chains": ["Proxy", "Node A"],
                  "upload": 12,
                  "download": 34,
                  "start": "2026-08-03T01:02:03Z"
                }
              ]
            }
            """);

        ActiveConnection connection = Assert.Single(
            MihomoControllerClient.ParseActiveConnectionsPayload(payload));

        Assert.Equal("live-1", connection.Id);
        Assert.Equal("browser", connection.ProcessName);
        Assert.Equal("example.com", connection.Host);
        Assert.Equal("Proxy / Node A", connection.ProxyName);
        Assert.Equal(12, connection.UploadBytes);
        Assert.Equal(34, connection.DownloadBytes);
    }

    /// <summary>Verifies runtime log messages normalize severity without changing payload text.</summary>
    [Theory]
    [InlineData("warn", "Warning")]
    [InlineData("error", "Error")]
    [InlineData("debug", "Debug")]
    [InlineData("info", "Info")]
    public void ParseRuntimeLogPayload_NormalizesLevel(string sourceLevel, string expectedLevel)
    {
        byte[] payload = Encoding.UTF8.GetBytes($$"""{"type":"{{sourceLevel}}","payload":"core message"}""");

        (string level, string message) = MihomoControllerClient.ParseRuntimeLogPayload(payload);

        Assert.Equal(expectedLevel, level);
        Assert.Equal("core message", message);
    }

    [Fact]
    public void ParseRuntimeLogPayload_BoundsUntrustedAppOwnedMessage()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "info",
            payload = new string('x',
                MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters + 1024),
        });

        (_, string message) = MihomoControllerClient.ParseRuntimeLogPayload(payload);

        Assert.Equal(MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters, message.Length);
    }

    /// <summary>Verifies close-all sends DELETE /connections.</summary>
    [Fact]
    public async Task CloseAllConnectionsAsync_SendsDeleteConnections()
    {
        RecordingHttpHandler handler = new("""{"connections":[]}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.CloseAllConnectionsAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/connections", handler.Requests[0].RequestUri?.AbsolutePath);
    }

    /// <summary>Verifies closing one connection sends DELETE /connections/{id} with URI escaping.</summary>
    [Fact]
    public async Task CloseConnectionAsync_SendsEscapedDeleteConnection()
    {
        RecordingHttpHandler handler = new("""{}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.CloseConnectionAsync("conn 1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/connections/conn%201", handler.Requests[0].RequestUri?.AbsolutePath);
    }

    /// <summary>Verifies strategy groups are parsed from /proxies and leaf proxy rows are ignored.</summary>
    [Fact]
    public async Task GetProxyGroupsAsync_ParsesSelectableGroups()
    {
        RecordingHttpHandler handler = new("""
            {
              "proxies": {
                "Proxy": { "name": "Proxy", "type": "Selector", "now": "Node A", "all": ["Node A", "DIRECT"] },
                "Node A": { "name": "Node A", "type": "Shadowsocks" }
              }
            }
            """);
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        IReadOnlyList<MihomoProxyGroup> groups = await client.GetProxyGroupsAsync(CancellationToken.None);

        MihomoProxyGroup group = Assert.Single(groups);
        Assert.Equal("Proxy", group.Name);
        Assert.Equal("Selector", group.Type);
        Assert.Equal("Node A", group.CurrentSelection);
        Assert.Equal(["Node A", "DIRECT"], group.Candidates);
    }

    /// <summary>Verifies selecting a strategy group proxy sends PUT /proxies/{group} with the selected name.</summary>
    [Fact]
    public async Task SelectProxyAsync_SendsPutProxySelection()
    {
        RecordingHttpHandler handler = new("""{}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.SelectProxyAsync("Proxy Group", "Node A", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/proxies/Proxy%20Group", handler.Requests[0].RequestUri?.AbsolutePath);
        Assert.Equal("""{"name":"Node A"}""", handler.Bodies[0]);
    }

    /// <summary>Verifies proxy provider resources are parsed from /providers/proxies.</summary>
    [Fact]
    public async Task GetProxyProvidersAsync_ParsesProviderResources()
    {
        RecordingHttpHandler handler = new("""
            {
              "providers": {
                "sub": {
                  "name": "sub",
                  "type": "Proxy",
                  "vehicleType": "HTTP",
                  "updatedAt": "2026-06-24T01:02:03Z",
                  "proxies": [{ "name": "Node A" }, { "name": "Node B" }]
                }
              }
            }
            """);
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        IReadOnlyList<MihomoProviderResource> providers = await client.GetProxyProvidersAsync(CancellationToken.None);

        MihomoProviderResource provider = Assert.Single(providers);
        Assert.Equal("sub", provider.Name);
        Assert.Equal(MihomoProviderKind.Proxy, provider.Kind);
        Assert.Equal("HTTP", provider.VehicleType);
        Assert.Equal(2, provider.ItemCount);
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 1, 2, 3, TimeSpan.Zero), provider.UpdatedAt);
    }

    /// <summary>Verifies rule provider resources are parsed from /providers/rules.</summary>
    [Fact]
    public async Task GetRuleProvidersAsync_ParsesProviderResources()
    {
        RecordingHttpHandler handler = new("""
            {
              "providers": {
                "reject": {
                  "name": "reject",
                  "type": "Rule",
                  "behavior": "domain",
                  "ruleCount": 123,
                  "updatedAt": "2026-06-24T01:02:03Z"
                }
              }
            }
            """);
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        IReadOnlyList<MihomoProviderResource> providers = await client.GetRuleProvidersAsync(CancellationToken.None);

        MihomoProviderResource provider = Assert.Single(providers);
        Assert.Equal("reject", provider.Name);
        Assert.Equal(MihomoProviderKind.Rule, provider.Kind);
        Assert.Equal("domain", provider.Behavior);
        Assert.Equal(123, provider.ItemCount);
    }

    /// <summary>Verifies provider updates target the correct provider namespace.</summary>
    [Theory]
    [InlineData(MihomoProviderKind.Proxy, "/providers/proxies/sub")]
    [InlineData(MihomoProviderKind.Rule, "/providers/rules/reject")]
    public async Task UpdateProviderAsync_SendsPutToProviderEndpoint(MihomoProviderKind kind, string expectedPath)
    {
        string providerName = kind == MihomoProviderKind.Proxy ? "sub" : "reject";
        RecordingHttpHandler handler = new("""{}""");
        MihomoControllerClient client = new(new HttpClient(handler), new Uri("http://127.0.0.1:9090"));

        await client.UpdateProviderAsync(new MihomoProviderResource(providerName, kind, "", "", 0, null), CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal(expectedPath, handler.Requests[0].RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetActiveConnectionsAsync_ServiceOwner_UsesBoundBrokerWithoutHttpFallback()
    {
        RecordingHttpHandler handler = new("""{"connections":[]}""");
        FakeControllerServiceBroker broker = FakeControllerServiceBroker.Running();
        MihomoControllerClient client = CreateOwnerAwareClient(
            handler,
            isAppCoreRunning: false,
            broker);

        IReadOnlyList<ActiveConnection> connections = await client
            .GetActiveConnectionsAsync(CancellationToken.None);

        ActiveConnection connection = Assert.Single(connections);
        Assert.Equal("connection-one", connection.Id);
        Assert.Empty(handler.Requests);
        Assert.Equal(MihomoServiceIpcCommand.GetConnections, Assert.Single(broker.Commands));
        Assert.Equal(broker.Status.ServiceSessionId, Assert.Single(broker.Bindings).ServiceSessionId);
        Assert.Equal(broker.Status.ActiveGeneration, broker.Bindings[0].Generation);
        Assert.Equal(broker.Status.ActiveConfigurationHash, broker.Bindings[0].ConfigurationHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetActiveConnectionsAsync_AppOwnerAndReleasedServiceChild_UsesDirectController(
        bool keepHostRunning)
    {
        RecordingHttpHandler handler = new("""{"connections":[]}""");
        FakeControllerServiceBroker broker = new(new MihomoServiceStatus(
            IsInstalled: true,
            IsRunning: false,
            Message: "stopped")
        {
            IsScmRunning = keepHostRunning,
            ProtocolVersion = keepHostRunning
                ? MihomoServiceIpcProtocol.CurrentVersion
                : null,
            ServiceSessionId = keepHostRunning ? Guid.NewGuid() : null,
            ServiceVersion = keepHostRunning ? "test" : null,
            ChildState = keepHostRunning ? MihomoServiceChildState.Stopped : null,
        });
        MihomoControllerClient client = CreateOwnerAwareClient(
            handler,
            isAppCoreRunning: true,
            broker);

        _ = await client.GetActiveConnectionsAsync(CancellationToken.None);

        Assert.Equal("/connections", Assert.Single(handler.Requests).RequestUri?.AbsolutePath);
        Assert.Empty(broker.Commands);
    }

    [Fact]
    public async Task GetActiveConnectionsAsync_AppOwnerAndUnauthenticatedIdleHost_FailsClosed()
    {
        RecordingHttpHandler handler = new("""{"connections":[]}""");
        FakeControllerServiceBroker broker = new(new MihomoServiceStatus(
            IsInstalled: true,
            IsRunning: false,
            Message: "IPC unavailable")
        {
            IsScmRunning = true,
            IpcFailureCode = "service.ipc.timeout",
        });
        MihomoControllerClient client = CreateOwnerAwareClient(
            handler,
            isAppCoreRunning: true,
            broker);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetActiveConnectionsAsync(CancellationToken.None));

        Assert.Equal("controller.owner_ambiguous", error.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(broker.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetActiveConnectionsAsync_UnknownOrDoubleOwner_FailsClosed(bool doubleOwner)
    {
        RecordingHttpHandler handler = new("""{"connections":[]}""");
        FakeControllerServiceBroker broker = doubleOwner
            ? FakeControllerServiceBroker.Running()
            : new FakeControllerServiceBroker(MihomoServiceStatus.Unknown("unknown"));
        MihomoControllerClient client = CreateOwnerAwareClient(
            handler,
            isAppCoreRunning: doubleOwner,
            broker);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetActiveConnectionsAsync(CancellationToken.None));

        Assert.Equal(
            doubleOwner ? "controller.owner_ambiguous" : "controller.owner_unavailable",
            error.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(broker.Commands);
    }

    [Fact]
    public async Task UpdateProviderAsync_ServiceOwner_UsesTypedBrokerWithoutDirectHttp()
    {
        RecordingHttpHandler handler = new("{}");
        FakeControllerServiceBroker broker = FakeControllerServiceBroker.Running();
        MihomoControllerClient client = CreateOwnerAwareClient(
            handler,
            isAppCoreRunning: false,
            broker);

        await client.UpdateProviderAsync(
            new MihomoProviderResource("subscription", MihomoProviderKind.Proxy, "HTTP", "", 1, null),
            CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Equal([MihomoServiceIpcCommand.UpdateProvider], broker.Commands);
        MihomoServiceIpcProviderUpdate update = Assert.Single(broker.ProviderUpdates);
        Assert.Equal(MihomoServiceIpcProviderKind.Proxy, update.Kind);
        Assert.Equal("subscription", update.Name);
    }

    [Fact]
    public async Task MatchesRuntimeConfigurationAsync_ServiceOwner_UsesTypedReadinessProjection()
    {
        RecordingHttpHandler handler = new("{}");
        FakeControllerServiceBroker broker = FakeControllerServiceBroker.Running();
        MihomoControllerClient client = CreateOwnerAwareClient(
            handler,
            isAppCoreRunning: false,
            broker);
        RuntimeConfigurationActivationPlan plan = new(
            ClashSharpMode.RuleTakeover,
            TunEnabled: true,
            MixedPort: 17890,
            ProfileId: "profile-one");

        bool matched = await client.MatchesRuntimeConfigurationAsync(
            plan,
            CancellationToken.None);

        Assert.True(matched);
        Assert.Empty(handler.Requests);
        Assert.Equal(
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration,
            Assert.Single(broker.Commands));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchesRuntimeConfigurationAsync_AppOwnerRequiresExactMixedListenerPid(
        bool listenerOwnedByRoot)
    {
        RecordingHttpHandler handler = new("""
            {
              "mixed-port": 17890,
              "mode": "rule",
              "tun": { "enable": false }
            }
            """);
        MihomoAppProcessIdentity identity = new(Guid.NewGuid(), 4321);
        FakeAppProcessIdentitySource identitySource = new(identity);
        FakeWindowsTcpOwnerVerifier ownerVerifier = new(listenerOwnedByRoot);
        MihomoAppControllerTransport transport = new(
            identitySource,
            ownerVerifier,
            ownerVerificationAttempts: 1,
            ownerVerificationRetryDelay: TimeSpan.Zero);
        FakeControllerServiceBroker broker = new(new MihomoServiceStatus(
            IsInstalled: true,
            IsRunning: false,
            Message: "stopped")
        {
            IsScmRunning = false,
        });
        MihomoControllerClient client = new(
            new HttpClient(handler),
            new Uri("http://127.0.0.1:9090"),
            static () => ControllerSecret,
            isAppCoreRunning: static () => true,
            broker,
            transport);
        RuntimeConfigurationActivationPlan plan = new(
            ClashSharpMode.RuleTakeover,
            TunEnabled: false,
            MixedPort: 17890,
            ProfileId: "profile-one");

        bool matched = await client.MatchesRuntimeConfigurationAsync(
            plan,
            CancellationToken.None);

        Assert.Equal(listenerOwnedByRoot, matched);
        Assert.Equal(1, ownerVerifier.ListenerVerificationCount);
        Assert.Equal(identity.RootProcessId, ownerVerifier.LastExpectedProcessId);
        Assert.Equal("/configs", Assert.Single(handler.Requests).RequestUri?.AbsolutePath);
    }

    private static MihomoControllerClient CreateOwnerAwareClient(
        RecordingHttpHandler handler,
        bool isAppCoreRunning,
        IMihomoControllerServiceBroker broker)
    {
        return new MihomoControllerClient(
            new HttpClient(handler),
            new Uri("http://127.0.0.1:9090"),
            static () => ControllerSecret,
            () => isAppCoreRunning,
            broker);
    }

    /// <summary>HTTP handler that records requests and returns a configured JSON response.</summary>
    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingHttpHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeAppProcessIdentitySource(MihomoAppProcessIdentity identity)
        : IMihomoAppProcessIdentitySource
    {
        public MihomoAppProcessIdentity? CaptureCurrent() => identity;

        public bool IsStillCurrent(MihomoAppProcessIdentity candidate) => candidate == identity;
    }

    private sealed class FakeWindowsTcpOwnerVerifier(bool listenerOwnedByRoot)
        : IWindowsTcpOwnerVerifier
    {
        public int ListenerVerificationCount { get; private set; }

        public int LastExpectedProcessId { get; private set; }

        public bool IsConnectedServerOwnedBy(Socket connectedClient, int expectedPid)
        {
            _ = connectedClient;
            _ = expectedPid;
            return true;
        }

        public bool IsLoopbackListenerOwnedBy(int port, int expectedPid)
        {
            Assert.Equal(17890, port);
            ListenerVerificationCount++;
            LastExpectedProcessId = expectedPid;
            return listenerOwnedByRoot;
        }
    }

    private sealed class FakeControllerServiceBroker(MihomoServiceStatus status)
        : IMihomoControllerServiceBroker
    {
        private static readonly Guid SessionId = Guid.Parse("9f3b490d-3373-4e9f-986a-0b905ef40c62");

        private const string ConfigurationHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public MihomoServiceStatus Status { get; } = status;

        public List<MihomoServiceIpcCommand> Commands { get; } = [];

        public List<MihomoServiceIpcControllerBinding> Bindings { get; } = [];

        public List<MihomoServiceIpcProviderUpdate> ProviderUpdates { get; } = [];

        public static FakeControllerServiceBroker Running()
        {
            return new FakeControllerServiceBroker(new MihomoServiceStatus(
                IsInstalled: true,
                IsRunning: true,
                Message: "running")
            {
                IsScmRunning = true,
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                ServiceSessionId = SessionId,
                ServiceVersion = "test",
                ChildState = MihomoServiceChildState.Running,
                ChildProcessId = 1234,
                ActiveGeneration = 7,
                ActiveConfigurationHash = ConfigurationHash,
            });
        }

        public MihomoServiceStatus GetLatestStatus()
        {
            return Status;
        }

        public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Status);
        }

        public Task<MihomoServiceIpcResponse> SendAsync(
            MihomoServiceIpcCommand command,
            MihomoServiceIpcControllerBinding expectedRuntime,
            string? connectionId,
            MihomoServiceIpcProxySelection? proxySelection,
            MihomoServiceIpcRuntimeLogQuery? runtimeLogQuery,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            Bindings.Add(expectedRuntime);
            MihomoServiceIpcResponse response = new()
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = Guid.NewGuid(),
                Succeeded = true,
                Snapshot = new MihomoServiceIpcSnapshot
                {
                    SessionId = expectedRuntime.ServiceSessionId,
                    ServiceVersion = "test",
                    ChildState = MihomoServiceChildState.Running,
                    ChildProcessId = 1234,
                    ActiveGeneration = expectedRuntime.Generation,
                    ActiveConfigurationHash = expectedRuntime.ConfigurationHash,
                },
                EffectiveConfiguration = command == MihomoServiceIpcCommand.ProbeEffectiveConfiguration
                    ? new MihomoServiceIpcEffectiveConfiguration
                    {
                        ControllerReady = true,
                        Mode = MihomoServiceIpcRoutingMode.Rule,
                        TunEnabled = true,
                        MixedPort = 0,
                    }
                    : null,
                ConnectionSnapshot = command == MihomoServiceIpcCommand.GetConnections
                    ? new MihomoServiceIpcConnectionSnapshot
                    {
                        Connections =
                        [
                            new MihomoServiceIpcConnection
                            {
                                Id = "connection-one",
                                ProcessName = "browser",
                                Host = "example.com",
                                RuleName = "DOMAIN",
                                RulePayload = "example.com",
                                ProxyName = "Proxy",
                                UploadBytes = 1,
                                DownloadBytes = 2,
                                StartedAt = new DateTimeOffset(
                                    2026,
                                    8,
                                    4,
                                    1,
                                    2,
                                    3,
                                    TimeSpan.Zero),
                            },
                        ],
                    }
                    : null,
            };
            return Task.FromResult(response);
        }

        public Task<MihomoServiceIpcResponse> UpdateProviderAsync(
            MihomoServiceIpcControllerBinding expectedRuntime,
            MihomoServiceIpcProviderUpdate providerUpdate,
            CancellationToken cancellationToken)
        {
            ProviderUpdates.Add(providerUpdate);
            return SendAsync(
                MihomoServiceIpcCommand.UpdateProvider,
                expectedRuntime,
                connectionId: null,
                proxySelection: null,
                runtimeLogQuery: null,
                cancellationToken);
        }
    }

}
