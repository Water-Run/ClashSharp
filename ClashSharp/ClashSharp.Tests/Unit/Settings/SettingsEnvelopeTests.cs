using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies immutable settings-envelope state and cross-record invariants.</summary>
public sealed class SettingsEnvelopeTests
{
    [Fact]
    public void Validate_CompleteCanonicalMatchingEnvelope_Succeeds()
    {
        SettingsEnvelope envelope = SettingsEnvelopeTestData.CreateMatchingEnvelope();

        SettingsEnvelopeValidationResult result =
            new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(envelope);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Constructors_RejectNonPositiveRevisionsAndInvalidBatchIdentity()
    {
        SettingValue value = SettingsEnvelopeTestData.Value("AppThemeMode", "Dark");
        SettingsEnvelope valid = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsApplicationBatchEntry entry = new(
            SettingsEnvelopeTestData.Key("AppThemeMode"),
            keyDesiredRevision: 1,
            SettingsApplicationBatchEntry.ComputeValueHash(value));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SettingDesiredEntry(value, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SettingsEnvelope(
                schemaVersion: 0,
                envelopeRevision: 1,
                valid.Desired,
                valid.Applied,
                [],
                []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SettingsEnvelope(
                schemaVersion: 1,
                envelopeRevision: 0,
                valid.Desired,
                valid.Applied,
                [],
                []));
        Assert.Throws<ArgumentException>(() =>
            new SettingsApplicationBatch(
                Guid.Empty,
                SettingsApplicationBatchKind.LiveReconcile,
                creationSequence: 1,
                Guid.NewGuid(),
                SettingsApplicationBatchState.Pending,
                SettingApplicationKind.Appearance,
                [entry]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SettingsApplicationBatch(
                Guid.NewGuid(),
                SettingsApplicationBatchKind.LiveReconcile,
                creationSequence: 0,
                Guid.NewGuid(),
                SettingsApplicationBatchState.Pending,
                SettingApplicationKind.Appearance,
                [entry]));
    }

    [Fact]
    public void Validate_MissingAndUnregisteredMapEntries_ReportsStablePaths()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        desired.Remove(SettingsEnvelopeTestData.Key("AppThemeMode"));
        SettingKey unknown = new("UnknownSetting");
        desired.Add(
            unknown,
            new SettingDesiredEntry(
                SettingsEnvelopeTestData.Value("DisplayLanguage", "English"),
                keyDesiredRevision: 1));
        applied.Remove(SettingsEnvelopeTestData.Key("MixedPort"));
        applied.Add(
            unknown,
            SettingAppliedState.Unknown(
                SettingAppliedUnknownReason.BlockedProbe,
                SettingAppliedUnknownHandling.BlockOperation));
        SettingsEnvelope envelope = new(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 1,
            desired,
            applied,
            [],
            []);

        SettingsEnvelopeValidationResult result = Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.desired.missing"
                && error.Path == "desired.AppThemeMode");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.applied.missing"
                && error.Path == "applied.MixedPort");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.key.unregistered"
                && error.Path == "desired.UnknownSetting");
    }

    [Fact]
    public void Validate_WrongDefinitionValueAndObservedHash_AreRejected()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        SettingKey language = SettingsEnvelopeTestData.Key("DisplayLanguage");
        desired[language] = new SettingDesiredEntry(
            SettingsEnvelopeTestData.Value("AppThemeMode", "Dark"),
            keyDesiredRevision: 1);
        SettingKey port = SettingsEnvelopeTestData.Key("MixedPort");
        applied[port] = SettingAppliedState.Verified(
            SettingsEnvelopeTestData.Value("MixedPort", "10000"),
            SettingAppliedValueSource.RuntimeProbe,
            new string('0', 64),
            SettingsEnvelopeTestData.ObservedAt);
        SettingsEnvelope envelope = new(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 1,
            desired,
            applied,
            [],
            []);

        SettingsEnvelopeValidationResult result = Validate(envelope);

        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.value.invalid"
                && error.Path == "desired.DisplayLanguage.value");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.applied.hash_mismatch"
                && error.Path == "applied.MixedPort.observedHash");
    }

    [Fact]
    public void Validate_MismatchWithoutBatchAndMatchWithBatch_AreRejected()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey theme = SettingsEnvelopeTestData.Key("AppThemeMode");
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        SettingDesiredEntry changed = new(
            SettingsEnvelopeTestData.Value("AppThemeMode", "Dark"),
            keyDesiredRevision: 2);
        desired[theme] = changed;
        SettingsEnvelope uncovered = new(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 2,
            desired,
            baseline.Applied,
            [],
            []);
        SettingsApplicationBatchEntry unexpectedEntry = SettingsApplicationBatchEntry.Create(
            theme,
            baseline.Desired[theme]);
        SettingsApplicationBatch unexpectedBatch = Batch(unexpectedEntry);
        SettingsEnvelope unexpected = new(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 1,
            baseline.Desired,
            baseline.Applied,
            [unexpectedBatch],
            []);

        SettingsEnvelopeValidationResult uncoveredResult = Validate(uncovered);
        SettingsEnvelopeValidationResult unexpectedResult = Validate(unexpected);

        Assert.Contains(
            uncoveredResult.Errors,
            error => error.Code == "settings.envelope.pending.uncovered");
        Assert.Contains(
            unexpectedResult.Errors,
            error => error.Code == "settings.envelope.pending.unexpected");
    }

    [Fact]
    public void Validate_OverlappingStaleRevisionAndStaleHashEntries_AreRejected()
    {
        SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        SettingsApplicationBatch original = Assert.Single(pending.PendingApplications);
        SettingsApplicationBatchEntry originalEntry = Assert.Single(original.Entries);
        SettingsApplicationBatch overlap = new(
            new Guid("10000000-0000-0000-0000-000000000002"),
            original.Kind,
            creationSequence: 2,
            new Guid("20000000-0000-0000-0000-000000000002"),
            SettingsApplicationBatchState.Pending,
            original.ApplicationKind,
            [originalEntry]);
        SettingsApplicationBatchEntry stale = new(
            originalEntry.Key,
            keyDesiredRevision: 1,
            new string('0', 64));
        SettingsApplicationBatch staleBatch = new(
            original.BatchId,
            original.Kind,
            original.CreationSequence,
            original.AttemptId,
            original.State,
            original.ApplicationKind,
            [stale],
            original.LastError);
        SettingsEnvelope overlapping = new(
            pending.SchemaVersion,
            pending.EnvelopeRevision,
            pending.Desired,
            pending.Applied,
            [original, overlap],
            []);
        SettingsEnvelope staleEnvelope = new(
            pending.SchemaVersion,
            pending.EnvelopeRevision,
            pending.Desired,
            pending.Applied,
            [staleBatch],
            []);

        SettingsEnvelopeValidationResult overlapResult = Validate(overlapping);
        SettingsEnvelopeValidationResult staleResult = Validate(staleEnvelope);

        Assert.Contains(
            overlapResult.Errors,
            error => error.Code == "settings.envelope.pending.overlap");
        Assert.Contains(
            staleResult.Errors,
            error => error.Code == "settings.envelope.batch.entry.stale_revision");
        Assert.Contains(
            staleResult.Errors,
            error => error.Code == "settings.envelope.batch.entry.stale_hash");
    }

    private static SettingsEnvelopeValidationResult Validate(SettingsEnvelope envelope) =>
        new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(envelope);

    private static SettingsApplicationBatch Batch(SettingsApplicationBatchEntry entry) =>
        new(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 1,
            Guid.NewGuid(),
            SettingsApplicationBatchState.Pending,
            SettingApplicationKind.Appearance,
            [entry]);
}
