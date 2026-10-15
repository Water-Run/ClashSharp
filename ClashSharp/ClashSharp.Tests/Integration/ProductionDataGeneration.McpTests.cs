extern alias ClashSharpUi;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Infrastructure.Agent;
using ClashSharp.Model;
using ClashSharp.Settings;
using ModelContextProtocol.Client;
using Backend = ClashSharpUi::ClashSharp.Hosting.Agent.AppMcpAgentBackend;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    private static readonly string[] AgentSettingReadKeys = ["RateUnit"];
    private static readonly string[] AgentHiddenSettingReadKeys = ["McpAllowedScopes"];
    [Fact]
    public async Task NativeAgent_OfficialClientUsesRealCatalogAndCannotBypassSeparateProxyGrant()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        var repositories = Assert.Single(fixture.Containers);
        McpAccessState access = new();
        Backend backend = new(repositories, fixture.Manager, fixture.Admission, fixture.Authority, new AgentNetwork(), access, DateTimeOffset.UtcNow);
        AgentStore store = new();
        McpHttpServer server = new(backend, new AgentErrors());
        await using McpRuntimeController controller = new(fixture.Admission, store, server, access);
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        McpAccessScope grants = McpAccessScope.ReadStatus | McpAccessScope.ReadConfiguration | McpAccessScope.ModifyConfiguration
            | McpAccessScope.ReadHistorySummary | McpAccessScope.ReadHistoryDetails;
        await using (MutationAdmissionLease exclusive = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None))
        {
            await controller.ApplyAdmittedAsync(new(port, new(true, grants)), exclusive, CancellationToken.None);
        }
        using HttpClient http = new(new SocketsHttpHandler { UseProxy = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Secret);
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new($"http://127.0.0.1:{port}/mcp") }, http));
        var status = await client.CallToolAsync("clashsharp_get_status");
        Assert.False(status.IsError ?? false);
        Assert.Equal("Disabled", status.StructuredContent!.Value.GetProperty("mode").GetString());
        Assert.False((await client.CallToolAsync("clashsharp_get_settings", new Dictionary<string, object?> { ["keys"] = AgentSettingReadKeys })).IsError ?? false);
        Assert.True((await client.CallToolAsync("clashsharp_get_settings", new Dictionary<string, object?> { ["keys"] = AgentHiddenSettingReadKeys })).IsError);
        Assert.False((await client.CallToolAsync("clashsharp_read_profile", new Dictionary<string, object?> { ["profile_id"] = "builtin-direct" })).IsError ?? false);
        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name == "clashsharp_set_proxy_state");
        long revision = repositories.Session.Snapshot.EnvelopeRevision;
        foreach (string key in new[] { "CurrentMode", "ActiveProfileId", "TransparentProxyEnabled", "MixedPort", "McpServerEnabled", "McpServerPort", "McpAllowedScopes", "TriggersEnabled" })
        {
            var denied = await client.CallToolAsync("clashsharp_update_settings", new Dictionary<string, object?>
            { ["operation_id"] = Guid.NewGuid().ToString(), ["changes"] = new Dictionary<string, object?> { [key] = "1" } });
            Assert.True(denied.IsError);
        }
        Assert.Equal(revision, repositories.Session.Snapshot.EnvelopeRevision);
        var operation = Guid.NewGuid().ToString();
        Dictionary<string, object?> create = new()
        {
            ["operation_id"] = operation,
            ["name"] = "Agent fixture",
            ["content"] = "proxies: []\nproxy-groups: []\nrules: []\n# private-fixture\n",
        };
        var saved = await client.CallToolAsync("clashsharp_save_profile", create);
        Assert.False(saved.IsError ?? false);
        string id = saved.StructuredContent!.Value.GetProperty("profile_id").GetString()!;
        Assert.DoesNotContain("private-fixture", saved.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        Assert.False((await client.CallToolAsync("clashsharp_save_profile", create)).IsError ?? false);
        Assert.Single(repositories.Profiles.GetProfileHistory(id));
        create["name"] = "Changed retry";
        Assert.True((await client.CallToolAsync("clashsharp_save_profile", create)).IsError);
        var projection = await client.CallToolAsync("clashsharp_read_profile", new Dictionary<string, object?> { ["profile_id"] = id });
        Assert.False(projection.IsError ?? false);
        Assert.False(projection.StructuredContent!.Value.TryGetProperty("content", out _));
        Assert.DoesNotContain("private-fixture", projection.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        Assert.True((await client.CallToolAsync("clashsharp_read_sensitive_profile", new Dictionary<string, object?> { ["profile_id"] = id })).IsError);
        var entry = Assert.Single(repositories.Profiles.GetProfileHistory(id));
        var historical = await client.CallToolAsync("clashsharp_read_profile_history", new Dictionary<string, object?> { ["profile_id"] = id, ["version_id"] = entry.VersionId });
        Assert.False(historical.IsError ?? false);
        Assert.Equal(entry.ContentSha256, historical.StructuredContent!.Value.GetProperty("sha256").GetString());
        Assert.DoesNotContain("private-fixture", historical.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(repositories.Logs.GetLogs(100), record => record.Source == "Mcp" && record.Message == "clashsharp_save_profile");
        Assert.All(repositories.Logs.GetLogs(100), record => Assert.DoesNotContain("private-fixture", record.Message + record.Detail, StringComparison.Ordinal));
        Assert.True(access.TryAuthenticate(store.Secret!, out McpClientIdentity? before));
        await using (MutationAdmissionLease exclusive = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None))
        {
            await controller.RotateCredentialAdmittedAsync(exclusive, CancellationToken.None);
        }
        Assert.True((await backend.ExecuteAsync(before!, new(McpToolId.GetStatus, JsonSerializer.SerializeToElement(new { })), CancellationToken.None)).IsError);
    }

    [Fact]
    public async Task NativeAgent_RealSettingsManagementOwnsProtectedCredentialAndListenerLifecycle()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        _ = await fixture.StartAsync();
        var repositories = Assert.Single(fixture.Containers);
        var participant = Assert.IsType<McpSettingsParticipant>(repositories.GetService(typeof(McpSettingsParticipant)));
        var management = new ClashSharpUi::ClashSharp.Hosting.Agent.GenerationMcpServerManagement(fixture.Manager, fixture.Admission, fixture.Authority);
        Assert.False((await management.ReadAsync(CancellationToken.None)).Desired.Access.Enabled);
        Assert.False(Directory.Exists(Path.Combine(repositories.Generation.RootPath, "Agent")));
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        McpSettingsSnapshot enabled = await management.ApplyAsync(new(port, new(true, McpAccessScope.ReadConfiguration)), CancellationToken.None);
        Assert.NotNull(enabled.Runtime.BoundEndpoint);
        Assert.True(enabled.Runtime.HasCredential);
        using JsonDocument configuration = JsonDocument.Parse(await management.GetClientConfigurationAsync(CancellationToken.None));
        string authorization = configuration.RootElement.GetProperty("headers").GetProperty("Authorization").GetString()!;
        using HttpClient http = new(new SocketsHttpHandler { UseProxy = false });
        http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(authorization);
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = enabled.Runtime.BoundEndpoint! }, http));
        Assert.False((await client.CallToolAsync("clashsharp_list_profiles")).IsError ?? false);
        Assert.True((await client.CallToolAsync("clashsharp_set_proxy_state", new Dictionary<string, object?> { ["operation_id"] = Guid.NewGuid().ToString(), ["mode"] = "Disabled" })).IsError);
        _ = await management.RotateCredentialAsync(CancellationToken.None);
        using JsonDocument rotated = JsonDocument.Parse(await management.GetClientConfigurationAsync(CancellationToken.None));
        Assert.NotEqual(authorization, rotated.RootElement.GetProperty("headers").GetProperty("Authorization").GetString());
        using HttpRequestMessage old = new(HttpMethod.Post, enabled.Runtime.BoundEndpoint);
        old.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage refused = await http.SendAsync(old);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var paused = await participant.QuiesceAsync(CancellationToken.None);
        Assert.True(paused.WasRunning);
        Assert.Null(participant.Runtime.CaptureSnapshot().BoundEndpoint);
        await participant.ResumeAsync(paused, CancellationToken.None);
        Assert.NotNull(participant.Runtime.CaptureSnapshot().BoundEndpoint);
        McpSettingsSnapshot disabled = await management.ApplyAsync(new(port, McpAccessPolicy.Default), CancellationToken.None);
        Assert.False(disabled.Desired.Access.Enabled);
        Assert.Null(disabled.Runtime.BoundEndpoint);
    }

    private sealed class AgentStore : IMcpCredentialStore
    {
        public string? Secret { get; private set; }
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Secret);
        public Task WriteAsync(string secret, CancellationToken cancellationToken) { Secret = secret; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AgentNetwork : INetworkStateObserver
    {
        public Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NetworkStateSnapshot(ClashSharpMode.Disabled, false, false, false, 10000, "agent-fixture"));
    }

    private sealed class AgentErrors : IApplicationErrorSink
    {
        public List<ApplicationError> Records { get; } = [];
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) { Records.Add(error); return Task.CompletedTask; }
    }
}
