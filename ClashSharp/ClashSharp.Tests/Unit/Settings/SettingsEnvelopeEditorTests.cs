using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies pure atomic edit, split, grouping, and revert behavior.</summary>
public sealed class SettingsEnvelopeEditorTests
{
    private readonly SettingsEnvelopeEditor _editor = new(SettingsRegistry.Default);

    [Fact]
    public void ApplyChanges_EqualDesiredValue_ReturnsOriginalInstance()
    {
        SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")],
            SettingsApplicationBatchState.Failed,
            new SettingsApplicationError("settings.apply.failed"));

        SettingsEnvelopeEditResult result = _editor.ApplyChanges(
            pending,
            [
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("AppThemeMode"),
                    SettingsEnvelopeTestData.Value("AppThemeMode", "Dark")),
            ],
            SettingsEnvelopeTestData.Transaction(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(SettingsEnvelopeEditOutcome.NoChange, result.Outcome);
        Assert.Same(pending, result.Envelope);
        Assert.Same(pending.PendingApplications[0], result.Envelope.PendingApplications[0]);
    }

    [Fact]
    public void ApplyChanges_ChangedKeyAdvancesOnlyItsRevisionAndCreatesCanonicalBatch()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey theme = SettingsEnvelopeTestData.Key("AppThemeMode");

        SettingsEnvelopeEditResult result = _editor.ApplyChanges(
            baseline,
            [
                new SettingValueChange(
                    theme,
                    SettingsEnvelopeTestData.Value("AppThemeMode", "Dark")),
            ],
            SettingsEnvelopeTestData.Transaction(2));

        Assert.True(result.IsSuccess);
        Assert.Equal(SettingsEnvelopeEditOutcome.Updated, result.Outcome);
        Assert.NotSame(baseline, result.Envelope);
        Assert.Equal(2, result.Envelope.EnvelopeRevision);
        Assert.Equal(2, result.Envelope.Desired[theme].KeyDesiredRevision);
        Assert.Equal("Dark", result.Envelope.Desired[theme].Value.CanonicalText);
        Assert.All(
            baseline.Desired.Keys.Where(key => key != theme),
            key => Assert.Same(baseline.Desired[key], result.Envelope.Desired[key]));

        SettingsApplicationBatch batch = Assert.Single(result.Envelope.PendingApplications);
        Assert.Equal(SettingsApplicationBatchKind.LiveReconcile, batch.Kind);
        Assert.Equal(SettingApplicationKind.Appearance, batch.ApplicationKind);
        Assert.Equal(SettingsApplicationBatchState.Pending, batch.State);
        Assert.NotEqual(Guid.Empty, batch.BatchId);
        Assert.NotEqual(Guid.Empty, batch.AttemptId);
        SettingsApplicationBatchEntry entry = Assert.Single(batch.Entries);
        Assert.Equal(theme, entry.Key);
        Assert.Equal(2, entry.KeyDesiredRevision);
        Assert.Equal(
            SettingsApplicationBatchEntry.ComputeValueHash(result.Envelope.Desired[theme].Value),
            entry.ValueHash);
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(result.Envelope).IsValid);
    }

    [Fact]
    public void ApplyChanges_SplitsFailedBatchWithoutChangingUntouchedSiblingIdentity()
    {
        SettingsApplicationError error = new("settings.apply.failed");
        SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [
                ("AppThemeMode", "Dark"),
                ("AppAccentColorMode", "Custom"),
            ],
            SettingsApplicationBatchState.Failed,
            error);
        SettingsApplicationBatch oldBatch = Assert.Single(pending.PendingApplications);
        SettingsApplicationBatchEntry accentEntry = oldBatch.Entries.Single(
            entry => entry.Key == SettingsEnvelopeTestData.Key("AppAccentColorMode"));

        SettingsEnvelopeEditResult result = _editor.ApplyChanges(
            pending,
            [
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("AppThemeMode"),
                    SettingsEnvelopeTestData.Value("AppThemeMode", "Light")),
            ],
            SettingsEnvelopeTestData.Transaction(3));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Envelope.PendingApplications.Count);
        SettingsApplicationBatch retained = result.Envelope.PendingApplications.Single(
            batch => batch.BatchId == oldBatch.BatchId);
        Assert.Equal(oldBatch.Kind, retained.Kind);
        Assert.Equal(oldBatch.CreationSequence, retained.CreationSequence);
        Assert.Equal(oldBatch.AttemptId, retained.AttemptId);
        Assert.Equal(oldBatch.State, retained.State);
        Assert.Same(error, retained.LastError);
        Assert.Same(accentEntry, Assert.Single(retained.Entries));

        SettingsApplicationBatch replacement = result.Envelope.PendingApplications.Single(
            batch => batch.BatchId != oldBatch.BatchId);
        Assert.Equal(
            SettingsEnvelopeTestData.Key("AppThemeMode"),
            Assert.Single(replacement.Entries).Key);
    }

    [Fact]
    public void ApplyChanges_ValueEqualToVerifiedApplied_RemovesOnlyChangedPendingEntry()
    {
        SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [
                ("AppThemeMode", "Dark"),
                ("AppAccentColorMode", "Custom"),
            ]);
        SettingsApplicationBatch oldBatch = Assert.Single(pending.PendingApplications);
        SettingKey theme = SettingsEnvelopeTestData.Key("AppThemeMode");
        SettingKey accent = SettingsEnvelopeTestData.Key("AppAccentColorMode");
        SettingsApplicationBatchEntry accentEntry = oldBatch.Entries.Single(entry => entry.Key == accent);

        SettingsEnvelopeEditResult result = _editor.ApplyChanges(
            pending,
            [
                new SettingValueChange(
                    theme,
                    SettingsEnvelopeTestData.Value("AppThemeMode", "FollowSystem")),
            ],
            SettingsEnvelopeTestData.Transaction(4));

        Assert.True(result.IsSuccess);
        SettingsApplicationBatch retained = Assert.Single(result.Envelope.PendingApplications);
        Assert.Equal(oldBatch.BatchId, retained.BatchId);
        Assert.Same(accentEntry, Assert.Single(retained.Entries));
        Assert.Equal("FollowSystem", result.Envelope.Desired[theme].Value.CanonicalText);
        Assert.Equal(3, result.Envelope.Desired[theme].KeyDesiredRevision);
    }

    [Fact]
    public void ApplyChanges_TouchedRunningBatch_ReturnsBusyWithoutMutation()
    {
        SettingsEnvelope running = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")],
            SettingsApplicationBatchState.Running);

        SettingsEnvelopeEditResult result = _editor.ApplyChanges(
            running,
            [
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("AppThemeMode"),
                    SettingsEnvelopeTestData.Value("AppThemeMode", "Light")),
            ],
            SettingsEnvelopeTestData.Transaction(5));

        Assert.False(result.IsSuccess);
        Assert.Equal(SettingsEnvelopeEditOutcome.Busy, result.Outcome);
        Assert.Equal("settings.envelope.edit.running_batch", result.ErrorCode);
        Assert.Same(running, result.Envelope);
    }

    [Fact]
    public void ApplyChanges_GroupsNewWorkByTimingAndApplicationKindWithoutMergingOldBatch()
    {
        SettingsEnvelope old = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        SettingsApplicationBatch oldBatch = Assert.Single(old.PendingApplications);

        SettingsEnvelopeEditResult result = _editor.ApplyChanges(
            old,
            [
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("AppAccentColorMode"),
                    SettingsEnvelopeTestData.Value("AppAccentColorMode", "Custom")),
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("MixedPort"),
                    SettingsEnvelopeTestData.Value("MixedPort", "10001")),
            ],
            SettingsEnvelopeTestData.Transaction(6));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Envelope.PendingApplications.Count);
        Assert.Same(
            oldBatch,
            result.Envelope.PendingApplications.Single(batch => batch.BatchId == oldBatch.BatchId));
        Assert.Contains(
            result.Envelope.PendingApplications,
            batch => batch.BatchId != oldBatch.BatchId
                && batch.ApplicationKind == SettingApplicationKind.Appearance);
        Assert.Contains(
            result.Envelope.PendingApplications,
            batch => batch.BatchId != oldBatch.BatchId
                && batch.ApplicationKind == SettingApplicationKind.Network);
    }

    [Fact]
    public void ApplyChanges_SameInputAndTransaction_ProducesStableAttemptIdentity()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingValueChange change = new(
            SettingsEnvelopeTestData.Key("AppThemeMode"),
            SettingsEnvelopeTestData.Value("AppThemeMode", "Dark"));
        Guid transactionId = SettingsEnvelopeTestData.Transaction(7);

        SettingsEnvelopeEditResult first = _editor.ApplyChanges(baseline, [change], transactionId);
        SettingsEnvelopeEditResult second = _editor.ApplyChanges(baseline, [change], transactionId);
        SettingsApplicationBatch firstBatch = Assert.Single(first.Envelope.PendingApplications);
        SettingsApplicationBatch secondBatch = Assert.Single(second.Envelope.PendingApplications);

        Assert.Equal(firstBatch.BatchId, secondBatch.BatchId);
        Assert.Equal(firstBatch.AttemptId, secondBatch.AttemptId);
        Assert.Equal(firstBatch.Entries, secondBatch.Entries);
    }

    [Fact]
    public void Revert_UsesVerifiedAppliedOrSafeFallbackAndPreservesUnrelatedFailure()
    {
        SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [
                ("AppThemeMode", "Dark"),
                ("AppAccentColorMode", "Custom"),
            ],
            SettingsApplicationBatchState.Failed,
            new SettingsApplicationError("settings.apply.failed"));
        SettingKey theme = SettingsEnvelopeTestData.Key("AppThemeMode");
        SettingKey accent = SettingsEnvelopeTestData.Key("AppAccentColorMode");
        SettingsApplicationBatch oldBatch = Assert.Single(pending.PendingApplications);
        SettingsApplicationBatchEntry accentEntry =
            oldBatch.Entries.Single(entry => entry.Key == accent);

        SettingsEnvelopeEditResult result = _editor.Revert(
            pending,
            [theme],
            SettingsEnvelopeTestData.Transaction(8));

        Assert.True(result.IsSuccess);
        Assert.Equal("FollowSystem", result.Envelope.Desired[theme].Value.CanonicalText);
        SettingsApplicationBatch retained = Assert.Single(result.Envelope.PendingApplications);
        Assert.Equal(oldBatch.BatchId, retained.BatchId);
        Assert.Equal(oldBatch.AttemptId, retained.AttemptId);
        Assert.Equal(SettingsApplicationBatchState.Failed, retained.State);
        Assert.Same(accentEntry, Assert.Single(retained.Entries));
    }

    [Fact]
    public void Revert_UnknownBlockedProbeUsesFallbackWithoutAutomaticBatch()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey tun = SettingsEnvelopeTestData.Key("TransparentProxyEnabled");
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        desired[tun] = new SettingDesiredEntry(
            SettingsEnvelopeTestData.Value("TransparentProxyEnabled", "true"),
            keyDesiredRevision: 2);
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        applied[tun] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.BlockedProbe,
            SettingAppliedUnknownHandling.UseSafeFallback);
        SettingsEnvelope unknown = new(
            baseline.SchemaVersion,
            envelopeRevision: 2,
            desired,
            applied,
            [],
            []);
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(unknown).IsValid);

        SettingsEnvelopeEditResult result = _editor.Revert(
            unknown,
            [tun],
            SettingsEnvelopeTestData.Transaction(9));

        Assert.True(result.IsSuccess);
        Assert.Equal("false", result.Envelope.Desired[tun].Value.CanonicalText);
        Assert.Empty(result.Envelope.PendingApplications);
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(result.Envelope).IsValid);
    }

    [Fact]
    public void ApplyChanges_AliasDuplicateAndWrongDefinitionValue_ReturnInvalidWithoutMutation()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingValueChange duplicate = new(
            SettingsEnvelopeTestData.Key("AppThemeMode"),
            SettingsEnvelopeTestData.Value("AppThemeMode", "Dark"));

        SettingsEnvelopeEditResult alias = _editor.ApplyChanges(
            baseline,
            [
                new SettingValueChange(
                    new SettingKey("MainlandChinaDisplayEnabled"),
                    SettingsEnvelopeTestData.Value(
                        "MainlandChinaFeatureMode",
                        "FlagReplacementOnly")),
            ],
            SettingsEnvelopeTestData.Transaction(10));
        SettingsEnvelopeEditResult duplicates = _editor.ApplyChanges(
            baseline,
            [duplicate, duplicate],
            SettingsEnvelopeTestData.Transaction(11));
        SettingsEnvelopeEditResult wrongValue = _editor.ApplyChanges(
            baseline,
            [
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("DisplayLanguage"),
                    SettingsEnvelopeTestData.Value("AppThemeMode", "Dark")),
            ],
            SettingsEnvelopeTestData.Transaction(12));

        Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, alias.Outcome);
        Assert.Equal("settings.envelope.edit.alias_read_only", alias.ErrorCode);
        Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, duplicates.Outcome);
        Assert.Equal("settings.envelope.edit.duplicate_key", duplicates.ErrorCode);
        Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, wrongValue.Outcome);
        Assert.Equal("settings.envelope.edit.value_invalid", wrongValue.ErrorCode);
        Assert.Same(baseline, alias.Envelope);
        Assert.Same(baseline, duplicates.Envelope);
        Assert.Same(baseline, wrongValue.Envelope);
    }
}
