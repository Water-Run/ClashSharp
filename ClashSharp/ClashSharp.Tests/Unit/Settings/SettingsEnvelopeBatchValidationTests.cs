using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies durable batch identity, routing, and total ordering validation.</summary>
public sealed class SettingsEnvelopeBatchValidationTests
{
    [Fact]
    public void Validate_DuplicateBatchIdentityAndSequence_AreRejected()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey theme = SettingsEnvelopeTestData.Key("AppThemeMode");
        SettingKey accent = SettingsEnvelopeTestData.Key("AppAccentColorMode");
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        desired[theme] = new SettingDesiredEntry(
            SettingsEnvelopeTestData.Value("AppThemeMode", "Dark"),
            keyDesiredRevision: 2);
        desired[accent] = new SettingDesiredEntry(
            SettingsEnvelopeTestData.Value("AppAccentColorMode", "Custom"),
            keyDesiredRevision: 2);
        Guid batchId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        SettingsApplicationBatch first = new(
            batchId,
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 1,
            attemptId,
            SettingsApplicationBatchState.Pending,
            SettingApplicationKind.Appearance,
            [SettingsApplicationBatchEntry.Create(theme, desired[theme])]);
        SettingsApplicationBatch second = new(
            batchId,
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 1,
            attemptId,
            SettingsApplicationBatchState.Pending,
            SettingApplicationKind.Appearance,
            [SettingsApplicationBatchEntry.Create(accent, desired[accent])]);
        SettingsEnvelope envelope = new(
            baseline.SchemaVersion,
            envelopeRevision: 2,
            desired,
            baseline.Applied,
            [first, second],
            []);

        SettingsEnvelopeValidationResult result = Validate(envelope);

        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.batch.id_duplicate");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.batch.attempt_duplicate");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.batch.sequence_duplicate");
    }

    [Fact]
    public void Validate_BatchKindAndApplicationMustMatchDefinition()
    {
        SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        SettingsApplicationBatch original = Assert.Single(pending.PendingApplications);
        SettingsApplicationBatch misrouted = new(
            original.BatchId,
            SettingsApplicationBatchKind.Restart,
            original.CreationSequence,
            original.AttemptId,
            original.State,
            SettingApplicationKind.Network,
            original.Entries);
        SettingsEnvelope envelope = new(
            pending.SchemaVersion,
            pending.EnvelopeRevision,
            pending.Desired,
            pending.Applied,
            [misrouted],
            []);

        SettingsEnvelopeValidationResult result = Validate(envelope);

        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.batch.kind_mismatch");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.batch.application_mismatch");
    }

    [Fact]
    public void Validate_BatchesMustUseTotalOrderAndDefinitionRouting()
    {
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope(registry);
        SettingDesiredEntry liveDesired = new(
            SettingsEnvelopeTestData.Value(registry, "LiveAppearance", "true"),
            keyDesiredRevision: 2);
        SettingDesiredEntry restartDesired = new(
            SettingsEnvelopeTestData.Value(registry, "RestartInternal", "true"),
            keyDesiredRevision: 2);
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        desired[registry.Get("LiveAppearance").Key] = liveDesired;
        desired[registry.Get("RestartInternal").Key] = restartDesired;
        SettingsApplicationBatch restart = new(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.Restart,
            creationSequence: 1,
            Guid.NewGuid(),
            SettingsApplicationBatchState.Pending,
            SettingApplicationKind.Internal,
            [SettingsApplicationBatchEntry.Create(registry.Get("RestartInternal").Key, restartDesired)]);
        SettingsApplicationBatch live = new(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 2,
            Guid.NewGuid(),
            SettingsApplicationBatchState.Pending,
            SettingApplicationKind.Appearance,
            [SettingsApplicationBatchEntry.Create(registry.Get("LiveAppearance").Key, liveDesired)]);
        SettingsEnvelope envelope = new(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 2,
            desired,
            baseline.Applied,
            [restart, live],
            []);

        SettingsEnvelopeValidationResult result =
            new SettingsEnvelopeValidator(registry).Validate(envelope);

        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.batches.not_ordered");
    }

    private static SettingsEnvelopeValidationResult Validate(SettingsEnvelope envelope) =>
        new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(envelope);
}
