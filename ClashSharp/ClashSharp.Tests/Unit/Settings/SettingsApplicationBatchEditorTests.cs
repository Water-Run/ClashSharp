using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Checks that uncertain effects, stale callbacks, and incomplete probes cannot manufacture applied settings.</summary>
public sealed class SettingsApplicationBatchEditorTests
{
    private readonly SettingsApplicationBatchEditor _editor = new(SettingsRegistry.Default);
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BeginAttempt_InvalidatesOnlyItsPreviousEvidenceAndBlocksTouchedEdits()
    {
        SettingsEnvelope source = Pending();
        SettingsApplicationBatch batch = source.PendingApplications[0];
        SettingsEnvelope running = AssertUpdated(_editor.BeginAttempt(source, batch.BatchId, batch.AttemptId));
        Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single(running.PendingApplications).State);
        Assert.Equal(SettingAppliedStateKind.Unknown, running.Applied[SettingsRegistry.Keys.AppThemeMode].Kind);
        Assert.Equal(SettingAppliedStateKind.Verified, source.Applied[SettingsRegistry.Keys.AppThemeMode].Kind);
        Assert.Same(source.Applied[SettingsRegistry.Keys.MixedPort], running.Applied[SettingsRegistry.Keys.MixedPort]);
        Assert.All(source.Desired, item => Assert.Same(item.Value, running.Desired[item.Key]));
        SettingsEnvelopeEditResult edit = new SettingsEnvelopeEditor(SettingsRegistry.Default).ApplyChanges(running,
            [Change("AppThemeMode", "Light")], Guid.NewGuid());
        Assert.Equal(SettingsEnvelopeEditOutcome.Busy, edit.Outcome);
        Assert.Same(running, edit.Envelope);
        Assert.Same(running, _editor.BeginAttempt(running, batch.BatchId, batch.AttemptId).Envelope);
    }

    [Fact]
    public void CompleteAttempt_PreservesAnUnrelatedEditMadeWhileTheParticipantWasRunning()
    {
        SettingsEnvelope source = Pending();
        SettingsApplicationBatch batch = source.PendingApplications[0];
        SettingsEnvelope running = AssertUpdated(_editor.BeginAttempt(source, batch.BatchId, batch.AttemptId));
        SettingsEnvelope intervening = AssertUpdated(new SettingsEnvelopeEditor(SettingsRegistry.Default).ApplyChanges(running,
            [Change("MixedPort", "7890")], Guid.NewGuid()));
        SettingsApplicationBatch network = Assert.Single(intervening.PendingApplications, item => item.ApplicationKind == SettingApplicationKind.Network);

        SettingsEnvelope completed = AssertUpdated(_editor.CompleteAttempt(intervening, batch.BatchId, batch.AttemptId,
            [Change("AppThemeMode", "Dark"), Change("AppAccentColorValue", "#FF001122")],
            SettingAppliedValueSource.MutationVerification, ObservedAt));

        Assert.Same(network, Assert.Single(completed.PendingApplications));
        Assert.Same(intervening.Desired[SettingsRegistry.Keys.MixedPort], completed.Desired[SettingsRegistry.Keys.MixedPort]);
        Assert.Equal("7890", completed.Desired[SettingsRegistry.Keys.MixedPort].Value.CanonicalText);
        Assert.Equal("Dark", completed.Applied[SettingsRegistry.Keys.AppThemeMode].Value!.CanonicalText);
        Assert.Equal(ObservedAt, completed.Applied[SettingsRegistry.Keys.AppThemeMode].ObservedAt);
        Assert.Equal(SettingAppliedValueSource.MutationVerification, completed.Applied[SettingsRegistry.Keys.AppThemeMode].Source);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("mismatch")]
    [InlineData("wrong_type")]
    [InlineData("wrong_key")]
    [InlineData("stale_attempt")]
    [InlineData("default_source")]
    [InlineData("legacy_source")]
    [InlineData("non_utc")]
    [InlineData("missing_time")]
    public void UntrustworthyCompletion_CannotPartiallyPublishEvidence(string fault)
    {
        SettingsEnvelope source = Pending();
        SettingsApplicationBatch batch = source.PendingApplications[0];
        SettingsEnvelope running = AssertUpdated(_editor.BeginAttempt(source, batch.BatchId, batch.AttemptId));
        List<SettingValueChange> values = [Change("AppThemeMode", "Dark"), Change("AppAccentColorValue", "#FF001122")];
        Guid attempt = batch.AttemptId;
        SettingAppliedValueSource evidence = SettingAppliedValueSource.RuntimeProbe;
        DateTimeOffset time = ObservedAt;
        switch (fault)
        {
            case "missing": values.RemoveAt(1); break;
            case "duplicate": values[1] = values[0]; break;
            case "extra": values.Add(Change("MixedPort", "7890")); break;
            case "mismatch": values[1] = Change("AppAccentColorValue", "#FF334455"); break;
            case "wrong_type": values[1] = new(SettingsRegistry.Keys.AppAccentColorValue, SettingsRegistry.Default.Get("MixedPort").DefaultValue); break;
            case "wrong_key": values[1] = Change("MixedPort", "7890"); break;
            case "stale_attempt": attempt = Guid.NewGuid(); break;
            case "default_source": evidence = SettingAppliedValueSource.DefaultInitialization; break;
            case "legacy_source": evidence = SettingAppliedValueSource.LegacyMigration; break;
            case "non_utc": time = ObservedAt.ToOffset(TimeSpan.FromHours(8)); break;
            case "missing_time": time = default; break;
        }

        SettingsEnvelopeEditResult result = _editor.CompleteAttempt(running, batch.BatchId, attempt, values, evidence, time);
        Assert.False(result.IsSuccess);
        Assert.Same(running, result.Envelope);
        Assert.All(batch.Entries, entry => Assert.Equal(SettingAppliedStateKind.Unknown, result.Envelope.Applied[entry.Key].Kind));
        Assert.DoesNotContain("334455", result.ErrorCode!, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryFailed_ChangesAttemptIdentityAndRejectsAllLateCallbacksFromThePreviousAttempt()
    {
        SettingsEnvelope source = Pending();
        SettingsApplicationBatch batch = source.PendingApplications[0];
        SettingsEnvelope running = AssertUpdated(_editor.BeginAttempt(source, batch.BatchId, batch.AttemptId));
        SettingsEnvelope failed = AssertUpdated(_editor.FailAttempt(running, batch.BatchId, batch.AttemptId, new("settings.participant.failed")));
        Assert.All(batch.Entries, entry => Assert.Equal(SettingAppliedUnknownReason.ProbeFailed, failed.Applied[entry.Key].UnknownReason));
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(failed.PendingApplications).State);
        Assert.False(_editor.RetryFailed(failed, batch.BatchId, batch.AttemptId, batch.AttemptId).IsSuccess);
        Guid nextAttempt = Guid.NewGuid();
        SettingsEnvelope retry = AssertUpdated(_editor.RetryFailed(failed, batch.BatchId, batch.AttemptId, nextAttempt));
        SettingsApplicationBatch next = Assert.Single(retry.PendingApplications);
        Assert.Equal(batch.BatchId, next.BatchId);
        Assert.Equal(batch.CreationSequence, next.CreationSequence);
        Assert.Equal(batch.Entries, next.Entries);
        Assert.Null(next.LastError);
        Assert.False(_editor.BeginAttempt(retry, batch.BatchId, batch.AttemptId).IsSuccess);
        Assert.False(_editor.FailAttempt(retry, batch.BatchId, batch.AttemptId, new("settings.participant.failed")).IsSuccess);
        Assert.False(_editor.CompleteAttempt(retry, batch.BatchId, batch.AttemptId,
            [Change("AppThemeMode", "Dark"), Change("AppAccentColorValue", "#FF001122")],
            SettingAppliedValueSource.RuntimeProbe, ObservedAt).IsSuccess);
        AssertUpdated(_editor.BeginAttempt(retry, batch.BatchId, nextAttempt));
    }

    [Fact]
    public void CompletedAttempt_CannotLaterBeFailedByAnOldCallback()
    {
        SettingsEnvelope source = Pending();
        SettingsApplicationBatch batch = source.PendingApplications[0];
        SettingsEnvelope running = AssertUpdated(_editor.BeginAttempt(source, batch.BatchId, batch.AttemptId));
        SettingsEnvelope completed = AssertUpdated(_editor.CompleteAttempt(running, batch.BatchId, batch.AttemptId,
            [Change("AppThemeMode", "Dark"), Change("AppAccentColorValue", "#FF001122")],
            SettingAppliedValueSource.RuntimeProbe, ObservedAt));
        SettingsEnvelopeEditResult late = _editor.FailAttempt(completed, batch.BatchId, batch.AttemptId, new("settings.participant.failed"));
        Assert.False(late.IsSuccess);
        Assert.Same(completed, late.Envelope);
    }

    private static SettingsEnvelope Pending() => SettingsEnvelopeTestData.CreatePendingEnvelope(
        [("AppThemeMode", "Dark"), ("AppAccentColorValue", "#FF001122")]);

    private static SettingValueChange Change(string key, string value) => new(new(key), SettingsEnvelopeTestData.Value(key, value));

    private static SettingsEnvelope AssertUpdated(SettingsEnvelopeEditResult result)
    {
        Assert.Equal(SettingsEnvelopeEditOutcome.Updated, result.Outcome);
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(result.Envelope).IsValid);
        return result.Envelope;
    }
}
