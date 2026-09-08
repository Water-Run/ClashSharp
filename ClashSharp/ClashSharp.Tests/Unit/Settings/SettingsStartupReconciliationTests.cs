using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Prevents historical applied values from being treated as observations of a new process.</summary>
public sealed class SettingsStartupReconciliationTests
{
    [Fact]
    public void PreviousProcessEvidence_IsInvalidatedWithoutChangingDesiredValues()
    {
        SettingsEnvelope previous = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsApplicationBatchEditor editor = new(SettingsRegistry.Default);
        Guid startupId = Guid.NewGuid();
        SettingsEnvelopeEditResult prepared = editor.ScheduleStartupReconciliation(previous, startupId);
        Assert.Equal(SettingsEnvelopeEditOutcome.Updated, prepared.Outcome);
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(prepared.Envelope).IsValid);
        Assert.All(prepared.Envelope.Applied.Values, value => Assert.Equal(SettingAppliedUnknownReason.NotObserved, value.UnknownReason));
        Assert.All(previous.Desired, item => Assert.Same(item.Value, prepared.Envelope.Desired[item.Key]));
        Assert.Equal(previous.Desired.Count, prepared.Envelope.PendingApplications.SelectMany(batch => batch.Entries).Count());
        Assert.Equal(SettingsEnvelopeEditOutcome.NoChange, editor.ScheduleStartupReconciliation(prepared.Envelope, startupId).Outcome);
        SettingsEnvelope repeated = editor.ScheduleStartupReconciliation(previous, startupId).Envelope;
        Assert.Equal(SettingsEnvelopeCodec.Encode(prepared.Envelope, SettingsRegistry.Default).ContentHash,
            SettingsEnvelopeCodec.Encode(repeated, SettingsRegistry.Default).ContentHash);
    }

    [Theory]
    [InlineData(SettingsApplicationBatchState.Running)]
    [InlineData(SettingsApplicationBatchState.Failed)]
    public void InterruptedAndFailedWork_RetainsItsIdentityAndExplicitRetryRequirement(SettingsApplicationBatchState state)
    {
        SettingsEnvelope previous = SettingsEnvelopeTestData.CreatePendingEnvelope([("AppThemeMode", "Dark")], state,
            state == SettingsApplicationBatchState.Failed ? new("settings.application.verification_failed") : null);
        SettingsApplicationBatch attempt = Assert.Single(previous.PendingApplications);
        SettingsEnvelope prepared = new SettingsApplicationBatchEditor(SettingsRegistry.Default)
            .ScheduleStartupReconciliation(previous, Guid.NewGuid()).Envelope;
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(prepared).IsValid);
        Assert.Same(attempt, Assert.Single(prepared.PendingApplications, batch => batch.BatchId == attempt.BatchId));
        Assert.Equal(SettingAppliedStateKind.Unknown, prepared.Applied[SettingsRegistry.Keys.AppThemeMode].Kind);
    }

    [Theory]
    [InlineData(SettingAppliedUnknownHandling.BlockOperation)]
    [InlineData(SettingAppliedUnknownHandling.UseSafeFallback)]
    public void BlockedExternalProbe_IsNotConvertedIntoAutomaticApplication(SettingAppliedUnknownHandling handling)
    {
        SettingsEnvelope source = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey key = SettingsRegistry.Keys.MixedPort;
        Dictionary<SettingKey, SettingAppliedState> applied = new(source.Applied)
        {
            [key] = SettingAppliedState.Unknown(SettingAppliedUnknownReason.BlockedProbe, handling),
        };
        SettingsEnvelope blocked = new(source.SchemaVersion, source.EnvelopeRevision, source.Desired, applied,
            source.PendingApplications, source.MigrationHistory);
        SettingsEnvelope prepared = new SettingsApplicationBatchEditor(SettingsRegistry.Default)
            .ScheduleStartupReconciliation(blocked, Guid.NewGuid()).Envelope;
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(prepared).IsValid);
        Assert.Same(applied[key], prepared.Applied[key]);
        Assert.DoesNotContain(prepared.PendingApplications.SelectMany(batch => batch.Entries), entry => entry.Key == key);
    }

    [Fact]
    public void JustMigratedSource_DoesNotCreateDuplicateWorkOrInventEffectiveValues()
    {
        SettingsEnvelope migrated = new SettingsMigrationPlanner(SettingsRegistry.Default).CreatePlan(
            new(SettingsRegistry.Default, new Dictionary<string, object?>()), Guid.NewGuid()).Envelope;
        SettingsEnvelopeEditResult result = new SettingsApplicationBatchEditor(SettingsRegistry.Default)
            .ScheduleStartupReconciliation(migrated, Guid.NewGuid());
        Assert.Equal(SettingsEnvelopeEditOutcome.NoChange, result.Outcome);
        Assert.Same(migrated, result.Envelope);
    }
}
