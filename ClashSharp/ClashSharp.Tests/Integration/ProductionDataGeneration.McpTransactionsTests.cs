extern alias ClashSharpUi;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml.Linq;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Infrastructure.Agent;
using ClashSharp.Model;
using ClashSharp.Settings;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Backend = ClashSharpUi::ClashSharp.Hosting.Agent.AppMcpAgentBackend;
using Management = ClashSharpUi::ClashSharp.Hosting.Agent.GenerationMcpServerManagement;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Theory]
    [InlineData(ClashSharpMode.Disabled)]
    [InlineData(ClashSharpMode.Standby)]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public async Task NativeAgent_GrantedProxyActionMatchesObservedRuntime(ClashSharpMode mode)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        _ = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture,
            McpAccessScope.ReadStatus | McpAccessScope.ChangeProxyState, new AgentSurfaceObserver(network));

        CallToolResult changed = await agent.CallAsync(McpToolId.SetProxyState, new { operation_id = Guid.NewGuid(), mode = mode.ToString() });

        Assert.False(changed.IsError ?? false, changed.StructuredContent?.GetRawText());
        Assert.Equal(mode, (await network.ReadConfigurationAsync(CancellationToken.None)).Mode);
        var envelope = fixture.Containers[0].Session.Snapshot;
        Assert.Equal(mode, envelope.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>());
        Assert.Equal(mode, envelope.Applied[SettingsRegistry.Keys.CurrentMode].Value!.Get<ClashSharpMode>());
        CallToolResult status = await agent.CallAsync(McpToolId.GetStatus, new { });
        Assert.Equal(mode.ToString(), status.StructuredContent!.Value.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task NativeAgent_RejectedWriteCanRetryAfterTheUserGrantsProxyControl()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        _ = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ModifyConfiguration);
        var request = new { operation_id = Guid.NewGuid(), changes = new { MixedPort = 19000 } };
        long before = fixture.Containers[0].Session.Snapshot.EnvelopeRevision;
        CallToolResult denied = await agent.CallAsync(McpToolId.UpdateSettings, request);
        Assert.True(denied.IsError);
        Assert.Equal(before, fixture.Containers[0].Session.Snapshot.EnvelopeRevision);
        Assert.Equal(10000, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);

        await agent.SetScopesAsync(McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState);
        CallToolResult applied = await agent.CallAsync(McpToolId.UpdateSettings, request);

        Assert.False(applied.IsError ?? false, applied.StructuredContent?.GetRawText());
        Assert.Equal(19000, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
    }

    [Fact]
    public async Task NativeAgent_SettingsBatchUsesObservedParticipantsAndDrainsNotificationChanges()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ModifyConfiguration);
        var before = fixture.Containers[0].Session.Snapshot;
        CallToolResult rejected = await agent.CallAsync(McpToolId.UpdateSettings,
            new { operation_id = Guid.NewGuid(), changes = new { RateUnit = "Bits", CurrentMode = "Standby" } });
        Assert.True(rejected.IsError);
        Assert.Equal(before.EnvelopeRevision, fixture.Containers[0].Session.Snapshot.EnvelopeRevision);
        Assert.Equal(RateUnit.Bytes, fixture.Containers[0].Session.Snapshot.Desired[SettingsRegistry.Keys.RateUnit].Value.Get<RateUnit>());

        CallToolResult preferences = await agent.CallAsync(McpToolId.UpdateSettings,
            new { operation_id = Guid.NewGuid(), changes = new { RateUnit = "Bits", AppThemeMode = "Dark" } });
        Assert.False(preferences.IsError ?? false, preferences.StructuredContent?.GetRawText());
        Assert.Equal(RateUnit.Bits, fixture.Containers[0].Session.Snapshot.Applied[SettingsRegistry.Keys.RateUnit].Value!.Get<RateUnit>());
        Assert.Equal(AppThemeMode.Dark, fixture.Containers[0].Session.Snapshot.Applied[SettingsRegistry.Keys.AppThemeMode].Value!.Get<AppThemeMode>());

        CallToolResult notifications = await agent.CallAsync(McpToolId.UpdateSettings,
            new { operation_id = Guid.NewGuid(), changes = new { TriggerNotificationsEnabled = false } });
        Assert.False(notifications.IsError ?? false, notifications.StructuredContent?.GetRawText());
        Assert.False(fixture.Containers[0].Session.Snapshot.Applied[SettingsRegistry.Keys.TriggerNotificationsEnabled].Value!.Get<bool>());
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task NativeAgent_ClosedAdmissionReturnsBusyWithoutUnexpectedFailure()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadStatus);
        await using MutationAdmissionLease exclusive = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);

        CallToolResult busy = await agent.CallAsync(McpToolId.GetStatus, new { });

        Assert.True(busy.IsError);
        Assert.Equal("mcp.busy", busy.StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_GenerationDrainReturnsBusyAndAbortRestoresReads()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var baseline = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadStatus);
        await using var transition = await fixture.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        CallToolResult busy = await agent.CallAsync(McpToolId.GetStatus, new { });

        Assert.True(busy.IsError);
        Assert.Equal("mcp.busy", busy.StructuredContent!.Value.GetProperty("error").GetString());
        await transition.AbortAsync(directory.Store, CancellationToken.None);
        Assert.False((await agent.CallAsync(McpToolId.GetStatus, new { })).IsError ?? false);
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_NodeSelectionPersistsAndRevocationDeniesTheExistingClient()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        SelectionController selection = ConfigureSelections(fixture);
        _ = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadStatus | McpAccessScope.ChangeProxyState);
        CallToolResult changed = await agent.CallAsync(McpToolId.SelectProxy,
            new { operation_id = Guid.NewGuid(), group = "GLOBAL", node = "Node B" });
        Assert.False(changed.IsError ?? false, changed.StructuredContent?.GetRawText());
        Assert.Equal("Node B", selection.Selection);
        var store = new ClashSharpUi::ClashSharp.Service.ProxySelectionStore(
            Path.Combine(fixture.Containers[0].Configuration.GetState().DirectoryPath, "proxy-selections.json"));
        Assert.Equal("Node B", store.Read("profile-a")["GLOBAL"]);

        await agent.SetScopesAsync(McpAccessScope.ReadStatus);
        Assert.DoesNotContain(await agent.Client.ListToolsAsync(), tool => tool.Name == "clashsharp_select_proxy");
        CallToolResult denied = await agent.CallAsync(McpToolId.SelectProxy,
            new { operation_id = Guid.NewGuid(), group = "GLOBAL", node = "Node C" });
        Assert.True(denied.IsError);
        Assert.Equal("Node B", selection.Selection);
        Assert.Equal("Node B", store.Read("profile-a")["GLOBAL"]);
    }

    [Fact]
    public async Task NativeAgent_RevocationCancelsAnUncommittedProfileWithoutReportingAnUnexpectedFailure()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ModifyConfiguration);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Validator.OnValidate = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        Task<CallToolResult> saving = agent.CallAsync(McpToolId.SaveProfile,
            new { operation_id = Guid.NewGuid(), name = "Cancelled fixture", content = "proxies: []\nproxy-groups: []\nrules: []\n" });
        await entered.Task.WaitAsync(deadline.Token);

        await agent.SetScopesAsync(McpAccessScope.ReadStatus).WaitAsync(deadline.Token);
        CallToolResult cancelled = await saving.WaitAsync(deadline.Token);

        Assert.True(cancelled.IsError);
        Assert.Equal("mcp.cancelled", cancelled.StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Empty(agent.Errors.Records);
        Assert.DoesNotContain(fixture.Containers[0].Profiles.GetProfiles(), profile => profile.Name == "Cancelled fixture");
    }

    [Fact]
    public async Task NativeAgent_ActiveProfileEditReportsPermissionDenialAndPreservesTheSource()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        _ = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ModifyConfiguration | McpAccessScope.ReadConfiguration);
        CallToolResult created = await agent.CallAsync(McpToolId.SaveProfile,
            new { operation_id = Guid.NewGuid(), name = "Active fixture", content = "proxies: []\nproxy-groups: []\nrules: []\n" });
        Assert.False(created.IsError ?? false, created.StructuredContent?.GetRawText());
        string profileId = created.StructuredContent!.Value.GetProperty("profile_id").GetString()!;
        CallToolResult read = await agent.CallAsync(McpToolId.ReadProfile, new { profile_id = profileId });
        string hash = read.StructuredContent!.Value.GetProperty("sha256").GetString()!;
        var activated = await fixture.Authority.ApplyRuntimeChangesAsync([new(SettingsRegistry.Keys.ActiveProfileId,
            SettingsRegistry.Default.Get(SettingsRegistry.Keys.ActiveProfileId.Value).Normalize(profileId).Value!)], Guid.NewGuid(), CancellationToken.None);
        Assert.True(activated.IsSucceeded, activated.Code);

        CallToolResult denied = await agent.CallAsync(McpToolId.SaveProfile,
            new { operation_id = Guid.NewGuid(), profile_id = profileId, name = "Denied fixture", content = "proxies: []\nproxy-groups: []\nrules: [MATCH,DIRECT]\n", expected_sha256 = hash });

        Assert.True(denied.IsError);
        Assert.Equal("mcp.access_denied", denied.StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Empty(agent.Errors.Records);
        CallToolResult unchanged = await agent.CallAsync(McpToolId.ReadProfile, new { profile_id = profileId });
        Assert.Equal(hash, unchanged.StructuredContent!.Value.GetProperty("sha256").GetString());
        Assert.Single(fixture.Containers[0].Profiles.GetProfileHistory(profileId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeAgent_GenerationReplacementDoesNotCarryTheOldCredentialOrImportedGrants(bool reset)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        var original = fixture.Containers[0];
        var originalRuntime = RuntimeOf(original);
        Management management = new(fixture.Manager, fixture.Admission, fixture.Authority);
        int port = AgentSession.ReservePort();
        _ = await management.ApplyAsync(new(port, new(true, McpAccessScope.ReadConfiguration)), CancellationToken.None);
        using JsonDocument originalConfiguration = JsonDocument.Parse(await management.GetClientConfigurationAsync(CancellationToken.None));
        string oldAuthorization = originalConfiguration.RootElement.GetProperty("headers").GetProperty("Authorization").GetString()!;
        if (reset)
        {
            _ = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);
        }
        else
        {
            XDocument package = EmptyPackage();
            package.Root!.Element("Settings")!.Add(
                new XElement("Setting", new XAttribute("Name", "McpServerEnabled"), new XAttribute("Value", "true")),
                new XElement("Setting", new XAttribute("Name", "McpAllowedScopes"), new XAttribute("Value", "255")));
            string path = Path.Combine(directory.RootPath, "agent-import.xml");
            await File.WriteAllTextAsync(path, package.ToString());
            _ = await fixture.CreateReplacementCoordinator().ImportAsync(path, CancellationToken.None);
        }
        var current = fixture.Containers[^1];
        Assert.NotEqual(original.Generation.GenerationId, current.Generation.GenerationId);
        McpSettingsSnapshot replaced = await management.ReadAsync(CancellationToken.None);
        Assert.Equal(reset ? McpAccessScope.ReadStatus : McpAccessScope.ReadConfiguration, replaced.Desired.Access.Scopes);
        Assert.Equal(!reset, replaced.Desired.Access.Enabled);
        if (reset) { Assert.False(Directory.Exists(Path.Combine(current.Generation.RootPath, "Agent"))); }
        Assert.Null(originalRuntime.Mcp.Runtime.CaptureSnapshot().BoundEndpoint);
        McpSettingsSnapshot enabled = await management.ApplyAsync(new(port, new(true, McpAccessScope.ReadConfiguration)), CancellationToken.None);
        using JsonDocument currentConfiguration = JsonDocument.Parse(await management.GetClientConfigurationAsync(CancellationToken.None));
        string newAuthorization = currentConfiguration.RootElement.GetProperty("headers").GetProperty("Authorization").GetString()!;
        Assert.False(StringComparer.Ordinal.Equals(oldAuthorization, newAuthorization));
        using HttpClient http = new(new SocketsHttpHandler { UseProxy = false });
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(oldAuthorization);
        using HttpRequestMessage old = new(HttpMethod.Post, enabled.Runtime.BoundEndpoint);
        old.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.SendAsync(old);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class AgentSurfaceObserver(NetworkSurface surface) : INetworkStateObserver
    {
        public async Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken)
        {
            var configuration = await surface.ReadConfigurationAsync(cancellationToken);
            return new(configuration.Mode, configuration.Mode != ClashSharpMode.Disabled,
                configuration.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover,
                configuration.EffectiveTunEnabled, configuration.MixedPort, "agent-surface");
        }
    }

    private sealed class AgentSession(Fixture fixture, McpRuntimeController controller,
        HttpClient http, McpClient client, int port, AgentErrors errors) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;
        public AgentErrors Errors { get; } = errors;

        public Task<CallToolResult> CallAsync(McpToolId tool, object arguments) => Client.CallToolAsync(
            McpToolCatalog.All.Single(definition => definition.Id == tool).Name,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))).AsTask();

        public async Task SetScopesAsync(McpAccessScope scopes)
        {
            await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            await controller.ApplyAdmittedAsync(new(port, new(true, scopes)), lease, CancellationToken.None);
        }

        public static async Task<AgentSession> StartAsync(Fixture fixture, McpAccessScope scopes, INetworkStateObserver? observer = null)
        {
            McpAccessState access = new();
            AgentStore store = new();
            AgentErrors errors = new();
            Backend backend = new(fixture.Containers[^1], fixture.Manager, fixture.Admission, fixture.Authority,
                observer ?? new AgentNetwork(), access, DateTimeOffset.UtcNow);
            McpRuntimeController controller = new(fixture.Admission, store, new McpHttpServer(backend, errors), access);
            int port = ReservePort();
            HttpClient http = new(new SocketsHttpHandler { UseProxy = false });
            try
            {
                await using (MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None))
                {
                    await controller.ApplyAdmittedAsync(new(port, new(true, scopes)), lease, CancellationToken.None);
                }
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Secret);
                McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new($"http://127.0.0.1:{port}/mcp") }, http));
                return new(fixture, controller, http, client, port, errors);
            }
            catch { http.Dispose(); await controller.DisposeAsync(); throw; }
        }

        public static int ReservePort()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); }
            finally { http.Dispose(); await controller.DisposeAsync(); }
        }
    }
}
