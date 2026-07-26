using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies editor identity, routing, and adversarial input boundaries.</summary>
public sealed class SettingsEnvelopeEditorBoundaryTests
{
    [Fact]
    public void ApplyChanges_DerivedIdentityCollision_ReturnsTypedInvalidResult()
    {
        SettingsEnvelope source = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);
        Guid transactionId = SettingsEnvelopeTestData.Transaction(100);
        SettingValueChange portChange = new(
            SettingsEnvelopeTestData.Key("MixedPort"),
            SettingsEnvelopeTestData.Value("MixedPort", "10001"));
        SettingsEnvelopeEditResult probe = editor.ApplyChanges(
            source,
            [portChange],
            transactionId);
        SettingsApplicationBatch derivedNetworkBatch = probe.Envelope.PendingApplications.Single(
            batch => batch.ApplicationKind == SettingApplicationKind.Network);
        SettingsApplicationBatch original = Assert.Single(source.PendingApplications);
        SettingsApplicationBatch colliding = new(
            derivedNetworkBatch.BatchId,
            original.Kind,
            original.CreationSequence,
            original.AttemptId,
            original.State,
            original.ApplicationKind,
            original.Entries,
            original.LastError);
        SettingsEnvelope adversarial = new(
            source.SchemaVersion,
            source.EnvelopeRevision,
            source.Desired,
            source.Applied,
            [colliding],
            source.MigrationHistory);
        Assert.True(
            new SettingsEnvelopeValidator(SettingsRegistry.Default)
                .Validate(adversarial)
                .IsValid);

        SettingsEnvelopeEditResult result = editor.ApplyChanges(
            adversarial,
            [portChange],
            transactionId);

        Assert.False(result.IsSuccess);
        Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, result.Outcome);
        Assert.Equal("settings.envelope.edit.identity_collision", result.ErrorCode);
        Assert.Same(adversarial, result.Envelope);
    }

    [Fact]
    public void ApplyChanges_DroppedBatchIdentityCollision_ReturnsTypedInvalidResult()
    {
        SettingsEnvelope source = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);
        Guid transactionId = SettingsEnvelopeTestData.Transaction(107);
        SettingValueChange change = new(
            SettingsEnvelopeTestData.Key("AppThemeMode"),
            SettingsEnvelopeTestData.Value("AppThemeMode", "Light"));
        SettingsApplicationBatch derived = Assert.Single(
            editor.ApplyChanges(source, [change], transactionId)
                .Envelope
                .PendingApplications);
        SettingsApplicationBatch original = Assert.Single(source.PendingApplications);
        SettingsApplicationBatch colliding = new(
            derived.BatchId,
            original.Kind,
            original.CreationSequence,
            derived.AttemptId,
            original.State,
            original.ApplicationKind,
            original.Entries,
            original.LastError);
        SettingsEnvelope adversarial = new(
            source.SchemaVersion,
            source.EnvelopeRevision,
            source.Desired,
            source.Applied,
            [colliding],
            source.MigrationHistory);
        Assert.True(
            new SettingsEnvelopeValidator(SettingsRegistry.Default)
                .Validate(adversarial)
                .IsValid);

        SettingsEnvelopeEditResult result = editor.ApplyChanges(
            adversarial,
            [change],
            transactionId);

        Assert.False(result.IsSuccess);
        Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, result.Outcome);
        Assert.Equal("settings.envelope.edit.identity_collision", result.ErrorCode);
        Assert.Same(adversarial, result.Envelope);
    }

    [Fact]
    public void ApplyChanges_LiveAndRestartGroupsUseTotalOrder()
    {
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope(registry);
        SettingsEnvelopeEditor editor = new(registry);

        SettingsEnvelopeEditResult result = editor.ApplyChanges(
            baseline,
            [
                new SettingValueChange(
                    registry.Get("RestartInternal").Key,
                    SettingsEnvelopeTestData.Value(registry, "RestartInternal", "true")),
                new SettingValueChange(
                    registry.Get("LiveAppearance").Key,
                    SettingsEnvelopeTestData.Value(registry, "LiveAppearance", "true")),
            ],
            SettingsEnvelopeTestData.Transaction(101));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                SettingsApplicationBatchKind.LiveReconcile,
                SettingsApplicationBatchKind.Restart,
            ],
            result.Envelope.PendingApplications.Select(static batch => batch.Kind));
        Assert.True(new SettingsEnvelopeValidator(registry).Validate(result.Envelope).IsValid);
    }

    [Fact]
    public void ApplyChanges_UnrelatedToRunningBatch_PreservesRunningIdentity()
    {
        SettingsEnvelope running = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")],
            SettingsApplicationBatchState.Running);
        SettingsApplicationBatch runningBatch = Assert.Single(running.PendingApplications);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);

        SettingsEnvelopeEditResult result = editor.ApplyChanges(
            running,
            [
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("MixedPort"),
                    SettingsEnvelopeTestData.Value("MixedPort", "10001")),
            ],
            SettingsEnvelopeTestData.Transaction(102));

        Assert.True(result.IsSuccess);
        Assert.Same(
            runningBatch,
            result.Envelope.PendingApplications.Single(
                batch => batch.BatchId == runningBatch.BatchId));
        Assert.True(
            new SettingsEnvelopeValidator(SettingsRegistry.Default)
                .Validate(result.Envelope)
                .IsValid);
    }

    [Fact]
    public void ApplyChanges_ReplacingHighestBatch_AdvancesOriginalSequenceHighWaterMark()
    {
        SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        SettingsApplicationBatch original = Assert.Single(pending.PendingApplications);
        SettingsApplicationBatch highSequence = new(
            original.BatchId,
            original.Kind,
            creationSequence: 41,
            original.AttemptId,
            original.State,
            original.ApplicationKind,
            original.Entries,
            original.LastError);
        SettingsEnvelope source = new(
            pending.SchemaVersion,
            pending.EnvelopeRevision,
            pending.Desired,
            pending.Applied,
            [highSequence],
            pending.MigrationHistory);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);

        SettingsEnvelopeEditResult result = editor.ApplyChanges(
            source,
            [
                new SettingValueChange(
                    SettingsEnvelopeTestData.Key("AppThemeMode"),
                    SettingsEnvelopeTestData.Value("AppThemeMode", "Light")),
            ],
            SettingsEnvelopeTestData.Transaction(104));

        Assert.True(result.IsSuccess);
        Assert.Equal(42, Assert.Single(result.Envelope.PendingApplications).CreationSequence);
    }

    [Fact]
    public void ApplyChanges_EnvelopeRevisionAloneDoesNotAffectAttemptIdentity()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsEnvelope unrelatedRevision = new(
            baseline.SchemaVersion,
            envelopeRevision: 99,
            baseline.Desired,
            baseline.Applied,
            baseline.PendingApplications,
            baseline.MigrationHistory);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);
        SettingValueChange change = new(
            SettingsEnvelopeTestData.Key("AppThemeMode"),
            SettingsEnvelopeTestData.Value("AppThemeMode", "Dark"));
        Guid transactionId = SettingsEnvelopeTestData.Transaction(105);

        SettingsApplicationBatch first = Assert.Single(
            editor.ApplyChanges(baseline, [change], transactionId)
                .Envelope
                .PendingApplications);
        SettingsApplicationBatch second = Assert.Single(
            editor.ApplyChanges(unrelatedRevision, [change], transactionId)
                .Envelope
                .PendingApplications);

        Assert.Equal(first.BatchId, second.BatchId);
        Assert.Equal(first.AttemptId, second.AttemptId);
        Assert.Equal(first.Entries, second.Entries);
    }

    [Fact]
    public void ApplyChanges_DerivedIdsUseUuidVersion8AndRfcVariant()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsEnvelopeEditResult result = new SettingsEnvelopeEditor(SettingsRegistry.Default)
            .ApplyChanges(
                baseline,
                [
                    new SettingValueChange(
                        SettingsEnvelopeTestData.Key("AppThemeMode"),
                        SettingsEnvelopeTestData.Value("AppThemeMode", "Dark")),
                ],
                SettingsEnvelopeTestData.Transaction(108));
        SettingsApplicationBatch batch = Assert.Single(result.Envelope.PendingApplications);

        AssertUuidVersion8(batch.BatchId);
        AssertUuidVersion8(batch.AttemptId);
    }

    [Fact]
    public void Revert_QueuedUnknownUsesSafeFallbackAndCreatesCurrentBatch()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey tun = SettingsEnvelopeTestData.Key("TransparentProxyEnabled");
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        applied[tun] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.ProbeFailed,
            SettingAppliedUnknownHandling.QueueApplication);
        SettingsApplicationBatch existing = new(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 1,
            Guid.NewGuid(),
            SettingsApplicationBatchState.Failed,
            SettingApplicationKind.Network,
            [SettingsApplicationBatchEntry.Create(tun, baseline.Desired[tun])],
            new SettingsApplicationError("settings.probe.failed"));
        SettingsEnvelope unknown = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            baseline.Desired,
            applied,
            [existing],
            []);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);

        SettingsEnvelopeEditResult result = editor.Revert(
            unknown,
            [tun],
            SettingsEnvelopeTestData.Transaction(103));

        Assert.True(result.IsSuccess);
        Assert.Equal("false", result.Envelope.Desired[tun].Value.CanonicalText);
        SettingsApplicationBatch replacement = Assert.Single(result.Envelope.PendingApplications);
        Assert.NotEqual(existing.BatchId, replacement.BatchId);
        Assert.Equal(SettingsApplicationBatchState.Pending, replacement.State);
        Assert.True(
            new SettingsEnvelopeValidator(SettingsRegistry.Default)
                .Validate(result.Envelope)
                .IsValid);
    }

    [Fact]
    public void Revert_UnknownAlreadyAtFallback_IsNoOpAndDoesNotRetryFailedAttempt()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey tun = SettingsEnvelopeTestData.Key("TransparentProxyEnabled");
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        desired[tun] = new SettingDesiredEntry(
            SettingsEnvelopeTestData.Value("TransparentProxyEnabled", "false"),
            keyDesiredRevision: 2);
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        applied[tun] = SettingAppliedState.Unknown(
            SettingAppliedUnknownReason.ProbeFailed,
            SettingAppliedUnknownHandling.QueueApplication);
        SettingsApplicationBatch failed = new(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 1,
            Guid.NewGuid(),
            SettingsApplicationBatchState.Failed,
            SettingApplicationKind.Network,
            [SettingsApplicationBatchEntry.Create(tun, desired[tun])],
            new SettingsApplicationError("settings.probe.failed"));
        SettingsEnvelope unknown = new(
            baseline.SchemaVersion,
            envelopeRevision: 2,
            desired,
            applied,
            [failed],
            []);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);

        SettingsEnvelopeEditResult result = editor.Revert(
            unknown,
            [tun],
            SettingsEnvelopeTestData.Transaction(106));

        Assert.Equal(SettingsEnvelopeEditOutcome.NoChange, result.Outcome);
        Assert.Same(unknown, result.Envelope);
        Assert.Same(failed, Assert.Single(result.Envelope.PendingApplications));
    }

    private static void AssertUuidVersion8(Guid value)
    {
        string canonical = value.ToString("D");
        Assert.Equal('8', canonical[14]);
        Assert.Contains(canonical[19], "89ab");
    }
}
