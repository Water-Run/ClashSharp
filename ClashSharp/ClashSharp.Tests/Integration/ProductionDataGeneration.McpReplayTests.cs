extern alias ClashSharpUi;
using System.Text.Json;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.Model;
using ClashSharp.Settings;
using ModelContextProtocol.Protocol;
using Backend = ClashSharpUi::ClashSharp.Hosting.Agent.AppMcpAgentBackend;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Theory]
    [InlineData("property-order")]
    [InlineData("whitespace")]
    [InlineData("escaped-string")]
    [InlineData("uuid-format")]
    public async Task NativeAgent_EquivalentProfileWriteReplaysAcceptedReceipt(string representation)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ModifyConfiguration);
        Backend backend = agent.Backend;
        McpClientIdentity identity = agent.Identity;
        Guid operation = Guid.NewGuid();
        string content = JsonSerializer.Serialize("proxies: []\nproxy-groups: []\nrules: []\n");
        string original = $$$"""{"operation_id":"{{{operation:D}}}","name":"Agent 北京","content":{{{content}}}}""";
        string replay = representation switch
        {
            "property-order" => $$$"""{"content":{{{content}}},"name":"Agent 北京","operation_id":"{{{operation:D}}}"}""",
            "whitespace" => $$$"""{ "operation_id" : "{{{operation:D}}}", "name" : "Agent 北京", "content" : {{{content}}} }""",
            "escaped-string" => $$$"""{"operation_id":"{{{operation:D}}}","name":"Agent \u5317\u4eac","content":{{{content}}}}""",
            "uuid-format" => $$$"""{"operation_id":"{{{operation:B}}}","name":"Agent 北京","content":{{{content}}}}""",
            _ => throw new ArgumentOutOfRangeException(nameof(representation)),
        };
        var profiles = fixture.Containers[0].Profiles;
        int beforeCount = profiles.GetProfiles().Count;
        McpToolResponse first = await ExecuteReplayRequestAsync(backend, identity, McpToolId.SaveProfile, original);
        Assert.False(first.IsError, first.Content.GetRawText());
        string profileId = first.Content.GetProperty("profile_id").GetString()!;
        string expectedHistory = Assert.Single(profiles.GetProfileHistory(profileId)).VersionId;

        McpToolResponse repeated = await ExecuteReplayRequestAsync(backend, identity, McpToolId.SaveProfile, replay);

        Assert.False(repeated.IsError, repeated.Content.GetRawText());
        Assert.Equal(first.Content.GetRawText(), repeated.Content.GetRawText());
        Assert.Equal(beforeCount + 1, profiles.GetProfiles().Count);
        Assert.Equal(expectedHistory, Assert.Single(profiles.GetProfileHistory(profileId)).VersionId);
        string changed = $$$"""{"operation_id":"{{{operation:D}}}","name":"Changed profile","content":{{{content}}}}""";
        McpToolResponse conflict = await ExecuteReplayRequestAsync(backend, identity, McpToolId.SaveProfile, changed);
        Assert.Equal("mcp.operation_id_conflict", conflict.Content.GetProperty("error").GetString());
        Assert.Equal(beforeCount + 1, profiles.GetProfiles().Count);
        Assert.Equal(expectedHistory, Assert.Single(profiles.GetProfileHistory(profileId)).VersionId);
    }

    [Fact]
    public async Task NativeAgent_ReorderedSettingBatchDoesNotReapplyAndRejectsAChangedValue()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        AppearanceSurface appearance = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), appearance, new NetworkSurface());
        await StartAgentRecoveryFixtureAsync(fixture);
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ModifyConfiguration);
        Backend backend = agent.Backend;
        McpClientIdentity identity = agent.Identity;
        int appearanceCalls = 0;
        appearance.BeforeApply = _ => appearanceCalls++;
        Guid operation = Guid.NewGuid();
        string original = $$$"""{"operation_id":"{{{operation:D}}}","changes":{"RateUnit":"Bits","AppThemeMode":"Dark"}}""";
        McpToolResponse first = await ExecuteReplayRequestAsync(backend, identity, McpToolId.UpdateSettings, original);
        Assert.False(first.IsError, first.Content.GetRawText());
        long revision = fixture.Containers[0].Session.Snapshot.EnvelopeRevision;
        int acceptedCalls = appearanceCalls;
        Assert.True(acceptedCalls > 0);

        string reordered = $$$"""{"changes":{"AppThemeMode":"Dark","RateUnit":"Bits"},"operation_id":"{{{operation:D}}}"}""";
        McpToolResponse repeated = await ExecuteReplayRequestAsync(backend, identity, McpToolId.UpdateSettings, reordered);

        Assert.False(repeated.IsError, repeated.Content.GetRawText());
        Assert.Equal(first.Content.GetRawText(), repeated.Content.GetRawText());
        Assert.Equal(revision, fixture.Containers[0].Session.Snapshot.EnvelopeRevision);
        Assert.Equal(acceptedCalls, appearanceCalls);
        string changed = $$$"""{"changes":{"AppThemeMode":"Dark","RateUnit":"Bytes"},"operation_id":"{{{operation:D}}}"}""";
        Assert.Equal("mcp.operation_id_conflict", (await ExecuteReplayRequestAsync(backend, identity,
            McpToolId.UpdateSettings, changed)).Content.GetProperty("error").GetString());
        Assert.Equal(RateUnit.Bits, fixture.Containers[0].Session.Snapshot.Applied[SettingsRegistry.Keys.RateUnit].Value!.Get<RateUnit>());
        Assert.Equal(revision, fixture.Containers[0].Session.Snapshot.EnvelopeRevision);
        Assert.Equal(acceptedCalls, appearanceCalls);
    }

    [Fact]
    public async Task NativeAgent_OfficialClientReordersAnEquivalentWriteWithoutRepeatingEffects()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        AppearanceSurface appearance = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), appearance, new NetworkSurface());
        await StartAgentRecoveryFixtureAsync(fixture);
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ModifyConfiguration);
        int calls = 0;
        appearance.BeforeApply = _ => calls++;
        Guid operation = Guid.NewGuid();
        Dictionary<string, object?> original = new()
        {
            ["operation_id"] = operation,
            ["changes"] = new Dictionary<string, object?> { ["RateUnit"] = "Bits", ["AppThemeMode"] = "Dark" },
        };
        CallToolResult first = await agent.CallAsync(McpToolId.UpdateSettings, original);
        Assert.False(first.IsError ?? false, first.StructuredContent?.GetRawText());
        int acceptedCalls = calls;
        long revision = fixture.Containers[0].Session.Snapshot.EnvelopeRevision;
        Assert.True(acceptedCalls > 0);

        Dictionary<string, object?> reordered = new()
        {
            ["changes"] = new Dictionary<string, object?> { ["AppThemeMode"] = "Dark", ["RateUnit"] = "Bits" },
            ["operation_id"] = operation,
        };
        CallToolResult repeated = await agent.CallAsync(McpToolId.UpdateSettings, reordered);

        Assert.False(repeated.IsError ?? false, repeated.StructuredContent?.GetRawText());
        Assert.Equal(first.StructuredContent?.GetRawText(), repeated.StructuredContent?.GetRawText());
        Assert.Equal(acceptedCalls, calls);
        Assert.Equal(revision, fixture.Containers[0].Session.Snapshot.EnvelopeRevision);
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_EquivalentProxyReplayKeepsEffectsOnceAndStillChecksRevocation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        await StartAgentRecoveryFixtureAsync(fixture);
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ChangeProxyState);
        Backend backend = agent.Backend;
        McpClientIdentity identity = agent.Identity;
        int networkCalls = 0;
        network.BeforeApply = (_, _) => networkCalls++;
        Guid operation = Guid.NewGuid();
        string original = $$$"""{"operation_id":"{{{operation:D}}}","mode":"RuleTakeover"}""";
        McpToolResponse first = await ExecuteReplayRequestAsync(backend, identity, McpToolId.SetProxyState, original);
        Assert.False(first.IsError, first.Content.GetRawText());
        long revision = fixture.Containers[0].Session.Snapshot.EnvelopeRevision;
        int acceptedCalls = networkCalls;
        Assert.True(acceptedCalls > 0);
        string reordered = $$$"""{"mode":"RuleTakeover","operation_id":"{{{operation:D}}}"}""";

        McpToolResponse repeated = await ExecuteReplayRequestAsync(backend, identity, McpToolId.SetProxyState, reordered);

        Assert.False(repeated.IsError, repeated.Content.GetRawText());
        Assert.Equal(first.Content.GetRawText(), repeated.Content.GetRawText());
        Assert.Equal(acceptedCalls, networkCalls);
        Assert.Equal(revision, fixture.Containers[0].Session.Snapshot.EnvelopeRevision);
        await agent.SetScopesAsync(McpAccessScope.ReadStatus);
        Assert.Equal("mcp.access_denied", (await ExecuteReplayRequestAsync(backend, identity,
            McpToolId.SetProxyState, reordered)).Content.GetProperty("error").GetString());
        Assert.Equal(acceptedCalls, networkCalls);
        Assert.Equal(ClashSharpMode.RuleTakeover, (await network.ReadConfigurationAsync(CancellationToken.None)).Mode);
        await agent.SetScopesAsync(McpAccessScope.ChangeProxyState);
        string changed = $$$"""{"mode":"Disabled","operation_id":"{{{operation:D}}}"}""";
        Assert.Equal("mcp.operation_id_conflict", (await ExecuteReplayRequestAsync(backend, identity,
            McpToolId.SetProxyState, changed)).Content.GetProperty("error").GetString());
        Assert.Equal(acceptedCalls, networkCalls);
        Assert.Equal(revision, fixture.Containers[0].Session.Snapshot.EnvelopeRevision);
    }

    [Fact]
    public async Task NativeAgent_FailedWriteNeedsExplicitRetryAndEquivalentRetryIdentifiersReplayItsReceipt()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        await StartAgentRecoveryFixtureAsync(fixture);
        await using AgentSession agent = await AgentSession.StartAsync(fixture,
            McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState);
        int calls = 0;
        network.BeforeApply = (_, _) => { calls++; throw new IOException("replay-failure-fixture"); };
        Guid writeOperation = Guid.NewGuid();
        string write = $$$"""{"operation_id":"{{{writeOperation:D}}}","changes":{"MixedPort":19000}}""";
        McpToolResponse failed = await ExecuteReplayRequestAsync(agent.Backend, agent.Identity, McpToolId.UpdateSettings, write);
        Assert.True(failed.IsError);
        Assert.True(failed.Content.TryGetProperty("status", out _));
        var session = fixture.Containers[0].Session;
        SettingsApplicationBatch batch = Assert.Single(session.Snapshot.PendingApplications);
        long failedRevision = session.Snapshot.EnvelopeRevision;
        int failedCalls = calls;
        network.BeforeApply = (_, _) => calls++;

        string reorderedWrite = $$$"""{"changes":{"MixedPort":19000},"operation_id":"{{{writeOperation:D}}}"}""";
        McpToolResponse repeatedFailure = await ExecuteReplayRequestAsync(agent.Backend, agent.Identity,
            McpToolId.UpdateSettings, reorderedWrite);
        Assert.Equal(failed.Content.GetRawText(), repeatedFailure.Content.GetRawText());
        Assert.Equal(failedCalls, calls);
        Assert.Equal(failedRevision, session.Snapshot.EnvelopeRevision);

        Guid retryOperation = Guid.NewGuid();
        string retry = $$$"""{"operation_id":"{{{retryOperation:D}}}","batch_id":"{{{batch.BatchId:D}}}","attempt_id":"{{{batch.AttemptId:D}}}"}""";
        McpToolResponse recovered = await ExecuteReplayRequestAsync(agent.Backend, agent.Identity,
            McpToolId.RetrySettingsApplication, retry);
        Assert.False(recovered.IsError, recovered.Content.GetRawText());
        Assert.Empty(session.Snapshot.PendingApplications);
        int recoveredCalls = calls;
        long recoveredRevision = session.Snapshot.EnvelopeRevision;
        string retryAgain = $$$"""{"attempt_id":"{{{batch.AttemptId:N}}}","batch_id":"{{{batch.BatchId:B}}}","operation_id":"{{{retryOperation:D}}}"}""";
        McpToolResponse replayed = await ExecuteReplayRequestAsync(agent.Backend, agent.Identity,
            McpToolId.RetrySettingsApplication, retryAgain);
        Assert.False(replayed.IsError, replayed.Content.GetRawText());
        Assert.Equal(recovered.Content.GetRawText(), replayed.Content.GetRawText());
        Assert.Equal(recoveredCalls, calls);
        Assert.Equal(recoveredRevision, session.Snapshot.EnvelopeRevision);
        Assert.Equal(19000, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
    }

    private static Task<McpToolResponse> ExecuteReplayRequestAsync(Backend backend, McpClientIdentity identity,
        McpToolId tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return backend.ExecuteAsync(identity, new(tool, document.RootElement), CancellationToken.None);
    }
}
