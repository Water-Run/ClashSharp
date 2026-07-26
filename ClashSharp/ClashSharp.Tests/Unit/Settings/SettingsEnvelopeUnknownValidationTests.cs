using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies unknown-state safe handling and registry-authority coverage rules.</summary>
public sealed class SettingsEnvelopeUnknownValidationTests
{
    [Fact]
    public void Validate_UnknownStateRequiresExplicitHandlingAndCoverage()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey port = SettingsEnvelopeTestData.Key("MixedPort");
        Dictionary<SettingKey, SettingAppliedState> queuedApplied = baseline.Applied.ToDictionary();
        queuedApplied[port] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.ProbeFailed,
            SettingAppliedUnknownHandling.QueueApplication);
        SettingsEnvelope uncovered = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            baseline.Desired,
            queuedApplied,
            [],
            []);
        Dictionary<SettingKey, SettingAppliedState> blockedApplied = baseline.Applied.ToDictionary();
        blockedApplied[port] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.BlockedProbe,
            SettingAppliedUnknownHandling.UseSafeFallback);
        SettingsEnvelope blocked = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            baseline.Desired,
            blockedApplied,
            [],
            []);

        Assert.Contains(
            Validate(uncovered).Errors,
            error => error.Code == "settings.envelope.pending.uncovered");
        Assert.True(Validate(blocked).IsValid);
        Assert.Equal(
            SettingAppliedUnknownHandling.UseSafeFallback,
            blocked.Applied[port].UnknownHandling);
    }

    [Fact]
    public void Validate_QueuedUnknownWithExactCurrentBatch_Succeeds()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey port = SettingsEnvelopeTestData.Key("MixedPort");
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        applied[port] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.ProbeFailed,
            SettingAppliedUnknownHandling.QueueApplication);
        SettingsApplicationBatch batch = new(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 1,
            Guid.NewGuid(),
            SettingsApplicationBatchState.Pending,
            SettingApplicationKind.Network,
            [SettingsApplicationBatchEntry.Create(port, baseline.Desired[port])]);
        SettingsEnvelope envelope = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            baseline.Desired,
            applied,
            [batch],
            []);

        Assert.True(Validate(envelope).IsValid);
    }

    [Fact]
    public void Validate_InternalBlockedProbeCannotBypassPendingCoverage()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey theme = SettingsEnvelopeTestData.Key("AppThemeMode");
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        applied[theme] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.BlockedProbe,
            SettingAppliedUnknownHandling.UseSafeFallback);
        SettingsEnvelope envelope = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            baseline.Desired,
            applied,
            [],
            []);

        SettingsEnvelopeValidationResult result = Validate(envelope);

        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.applied.blocked_probe_not_external");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.pending.uncovered");
    }

    [Fact]
    public void Validate_RestartBoundBlockedProbeCannotBypassRestartCoverage()
    {
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope(registry);
        SettingKey restart = registry.Get("RestartInternal").Key;
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        applied[restart] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.BlockedProbe,
            SettingAppliedUnknownHandling.BlockOperation);
        SettingsEnvelope envelope = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            baseline.Desired,
            applied,
            [],
            []);

        SettingsEnvelopeValidationResult result =
            new SettingsEnvelopeValidator(registry).Validate(envelope);

        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.applied.blocked_probe_not_external");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.pending.uncovered");
    }

    private static SettingsEnvelopeValidationResult Validate(SettingsEnvelope envelope) =>
        new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(envelope);
}
