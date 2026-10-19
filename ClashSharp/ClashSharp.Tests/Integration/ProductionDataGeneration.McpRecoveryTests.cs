extern alias ClashSharpUi;
using System.Text.Json;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Settings;
using ModelContextProtocol.Protocol;
using SamplingFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationSamplingRuntime;
using SettingsService = ClashSharpUi::ClashSharp.Service.AppSettingsService;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    private static readonly string[] AgentRetryTriggerSettingKeys = ["TriggerNotificationsEnabled"];

    [Fact]
    public async Task NativeAgent_FailedNetworkApplicationCanBeExplicitlyRetriedWithoutRepeatingDesiredChanges()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        await StartAgentRecoveryFixtureAsync(fixture);
        await using AgentSession agent = await AgentSession.StartAsync(fixture,
            McpAccessScope.ReadConfiguration | McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState);
        network.BeforeApply = (_, _) => throw new IOException("private-recovery-fixture");
        CallToolResult failed = await agent.CallAsync(McpToolId.UpdateSettings, new { operation_id = Guid.NewGuid(), changes = new { MixedPort = 19000 } });
        Assert.True(failed.IsError);
        var session = fixture.Containers[0].Session;
        SettingDesiredEntry desired = session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort];
        JsonElement pending = Assert.Single((await agent.CallAsync(McpToolId.GetSettings, new { keys = AgentPortSettingKeys }))
            .StructuredContent!.Value.GetProperty("pending_applications").EnumerateArray());
        Assert.Equal("Failed", pending.GetProperty("state").GetString());
        Assert.True(pending.GetProperty("retry_allowed").GetBoolean());
        Assert.DoesNotContain("private-recovery-fixture", pending.GetRawText(), StringComparison.Ordinal);
        network.BeforeApply = null;
        var retry = new
        {
            operation_id = Guid.NewGuid(),
            batch_id = pending.GetProperty("batch_id").GetGuid(),
            attempt_id = pending.GetProperty("attempt_id").GetGuid()
        };

        CallToolResult recovered = await agent.CallAsync(McpToolId.RetrySettingsApplication, retry);

        Assert.False(recovered.IsError ?? false, recovered.StructuredContent?.GetRawText());
        Assert.Equal(desired, session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort]);
        Assert.Equal("19000", session.Snapshot.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
        Assert.Empty(session.Snapshot.PendingApplications);
        Assert.Equal(19000, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
        long revision = session.Snapshot.EnvelopeRevision;
        Assert.False((await agent.CallAsync(McpToolId.RetrySettingsApplication, retry)).IsError ?? false);
        Assert.Equal(revision, session.Snapshot.EnvelopeRevision);
        Assert.True((await agent.CallAsync(McpToolId.RetrySettingsApplication,
            new { operation_id = Guid.NewGuid(), retry.batch_id, retry.attempt_id })).IsError);
        Assert.Equal(revision, session.Snapshot.EnvelopeRevision);
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_RetryRequiresTheCurrentProxyGrantAndDoesNotConsumeADeniedOperation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        await StartAgentRecoveryFixtureAsync(fixture);
        SettingsApplicationBatch batch = await SeedFailedAgentBatchAsync(fixture, "MixedPort");
        await using AgentSession agent = await AgentSession.StartAsync(fixture,
            McpAccessScope.ReadConfiguration | McpAccessScope.ModifyConfiguration);
        var retry = new { operation_id = Guid.NewGuid(), batch_id = batch.BatchId, attempt_id = batch.AttemptId };
        var session = fixture.Containers[0].Session;
        long revision = session.Snapshot.EnvelopeRevision;

        JsonElement pending = Assert.Single((await agent.CallAsync(McpToolId.GetSettings, new { keys = AgentPortSettingKeys }))
            .StructuredContent!.Value.GetProperty("pending_applications").EnumerateArray());
        Assert.False(pending.GetProperty("retry_allowed").GetBoolean());
        Assert.Equal("mcp.setting_access_denied", (await agent.CallAsync(McpToolId.RetrySettingsApplication, retry))
            .StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Equal(revision, session.Snapshot.EnvelopeRevision);
        await agent.SetScopesAsync(McpAccessScope.ReadConfiguration | McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState);

        Assert.False((await agent.CallAsync(McpToolId.RetrySettingsApplication, retry)).IsError ?? false);
        Assert.Empty(session.Snapshot.PendingApplications);
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_TriggerNotificationRetryDrainsProducersAndKeepsTheOfficialConnectionUsable()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        await StartAgentRecoveryFixtureAsync(fixture);
        SettingsApplicationBatch batch = await SeedFailedAgentBatchAsync(fixture, "TriggerNotificationsEnabled");
        await using AgentSession agent = await AgentSession.StartAsync(fixture,
            McpAccessScope.ReadStatus | McpAccessScope.ReadConfiguration | McpAccessScope.ModifyConfiguration);

        CallToolResult result = await agent.CallAsync(McpToolId.RetrySettingsApplication,
            new { operation_id = Guid.NewGuid(), batch_id = batch.BatchId, attempt_id = batch.AttemptId }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.IsError ?? false, result.StructuredContent?.GetRawText());
        Assert.Empty(fixture.Containers[0].Session.Snapshot.PendingApplications);
        Assert.False((await agent.CallAsync(McpToolId.GetStatus, new { })).IsError ?? false);
        Assert.Empty(agent.Errors.Records);
    }

    [Theory]
    [InlineData("McpAllowedScopes")]
    [InlineData("TriggersEnabled")]
    public async Task NativeAgent_UnclassifiedFailedApplicationsCannotBeRetriedEvenWithAllGrants(string key)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        await StartAgentRecoveryFixtureAsync(fixture);
        SettingsApplicationBatch batch = await SeedFailedAgentBatchAsync(fixture, key);
        await using AgentSession agent = await AgentSession.StartAsync(fixture, (McpAccessScope)255);
        var session = fixture.Containers[0].Session;
        long revision = session.Snapshot.EnvelopeRevision;

        CallToolResult denied = await agent.CallAsync(McpToolId.RetrySettingsApplication,
            new { operation_id = Guid.NewGuid(), batch_id = batch.BatchId, attempt_id = batch.AttemptId });

        Assert.True(denied.IsError);
        Assert.Equal("mcp.setting_access_denied", denied.StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Equal(revision, session.Snapshot.EnvelopeRevision);
        if (key == "McpAllowedScopes")
        {
            Assert.Empty((await agent.CallAsync(McpToolId.GetSettings, new { })).StructuredContent!.Value.GetProperty("pending_applications").EnumerateArray());
        }
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_RetryAuthorizesEveryCompanionAndRejectsStaleAttemptsWithoutPublishing()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        await StartAgentRecoveryFixtureAsync(fixture);
        SettingsApplicationBatch batch = await SeedFailedAgentBatchAsync(fixture, "TriggerNotificationsEnabled", "TriggersEnabled");
        await using AgentSession agent = await AgentSession.StartAsync(fixture, (McpAccessScope)255);
        var session = fixture.Containers[0].Session;
        long revision = session.Snapshot.EnvelopeRevision;

        JsonElement pending = Assert.Single((await agent.CallAsync(McpToolId.GetSettings, new { keys = AgentRetryTriggerSettingKeys }))
            .StructuredContent!.Value.GetProperty("pending_applications").EnumerateArray());
        Assert.Equal(2, pending.GetProperty("keys").GetArrayLength());
        Assert.False(pending.GetProperty("retry_allowed").GetBoolean());
        Assert.Equal("mcp.setting_access_denied", (await agent.CallAsync(McpToolId.RetrySettingsApplication,
            new { operation_id = Guid.NewGuid(), batch_id = batch.BatchId, attempt_id = batch.AttemptId }))
            .StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Equal("mcp.settings_application_unavailable", (await agent.CallAsync(McpToolId.RetrySettingsApplication,
            new { operation_id = Guid.NewGuid(), batch_id = batch.BatchId, attempt_id = Guid.NewGuid() }))
            .StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Equal(revision, session.Snapshot.EnvelopeRevision);
        Assert.Equal(batch.AttemptId, Assert.Single(session.Snapshot.PendingApplications).AttemptId);
        Assert.Empty(agent.Errors.Records);
    }

    private static async Task StartAgentRecoveryFixtureAsync(Fixture fixture)
    {
        var result = await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(), new SamplingFacade(fixture.Manager),
            new SettingsService(new Dictionary<string, object>())).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.Empty(fixture.Containers[0].Session.Snapshot.PendingApplications);
    }

    private static async Task<SettingsApplicationBatch> SeedFailedAgentBatchAsync(Fixture fixture, params string[] names)
    {
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        var session = fixture.Containers[0].Session;
        SettingValueChange[] changes = names.Select(name =>
        {
            SettingDefinition setting = SettingsRegistry.Default.Get(name);
            string value = name switch { "MixedPort" => "19000", "McpAllowedScopes" => "255", _ => "false" };
            return new SettingValueChange(setting.Key, setting.Normalize(value).Value!);
        }).ToArray();
        var changed = await session.ChangeAdmittedAsync(changes, Guid.NewGuid(), lease, CancellationToken.None);
        Assert.True(changed.IsSucceeded, changed.Code);
        SettingsApplicationBatch pending = Assert.Single(session.Snapshot.PendingApplications);
        var failed = await session.ApplyBatchAdmittedAsync(pending.BatchId, pending.AttemptId,
            new AgentFailureParticipant(pending.ApplicationKind), SettingsApplicationPhase.Live, lease, CancellationToken.None);
        Assert.False(failed.IsSucceeded);
        return Assert.Single(session.Snapshot.PendingApplications);
    }

    private sealed class AgentFailureParticipant(SettingApplicationKind kind) : ISettingsApplicationParticipant
    {
        public SettingApplicationKind ApplicationKind => kind;
        public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("private-recovery-fixture"));
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsApplicationObservation(request.Generation, request.Batch.BatchId, request.Batch.AttemptId,
                request.Values.Keys.Select(key => new SettingValueChange(key, SettingsRegistry.Default.Get(key.Value).DefaultValue))));
    }
}
