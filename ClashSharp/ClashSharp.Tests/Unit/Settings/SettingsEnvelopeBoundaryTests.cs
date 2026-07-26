using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies local envelope record boundaries before cross-record validation.</summary>
public sealed class SettingsEnvelopeBoundaryTests
{
    [Fact]
    public void VerifiedAppliedState_RejectsMissingObservationTimestamp()
    {
        SettingValue value = SettingsEnvelopeTestData.Value("MixedPort", "10000");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SettingAppliedState.Verified(
                value,
                SettingAppliedValueSource.RuntimeProbe,
                SettingsApplicationBatchEntry.ComputeValueHash(value),
                default));
    }

    [Fact]
    public void UnknownBlockedProbe_RejectsAutomaticApplication()
    {
        Assert.Throws<ArgumentException>(() =>
            SettingAppliedState.Unknown(
                SettingAppliedUnknownReason.BlockedProbe,
                SettingAppliedUnknownHandling.QueueApplication));
    }

    [Theory]
    [InlineData(SettingAppliedUnknownHandling.UseSafeFallback)]
    [InlineData(SettingAppliedUnknownHandling.BlockOperation)]
    public void UnknownNonBlockedReason_RejectsMissingApplicationCoverage(
        SettingAppliedUnknownHandling handling)
    {
        Assert.Throws<ArgumentException>(() =>
            SettingAppliedState.Unknown(
                SettingAppliedUnknownReason.ProbeFailed,
                handling));
    }

    [Fact]
    public void Batch_RequiresFailureErrorPairingAndUniqueEntries()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey key = SettingsEnvelopeTestData.Key("AppThemeMode");
        SettingsApplicationBatchEntry entry =
            SettingsApplicationBatchEntry.Create(key, baseline.Desired[key]);

        Assert.Throws<ArgumentException>(() =>
            CreateBatch(SettingsApplicationBatchState.Failed, [entry]));
        Assert.Throws<ArgumentException>(() =>
            CreateBatch(
                SettingsApplicationBatchState.Pending,
                [entry],
                new SettingsApplicationError("settings.apply.failed")));
        Assert.Throws<ArgumentException>(() =>
            CreateBatch(SettingsApplicationBatchState.Pending, [entry, entry]));
    }

    [Fact]
    public void Batch_SortsAndSnapshotsEntriesIntoReadOnlyStorage()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingKey theme = SettingsEnvelopeTestData.Key("AppThemeMode");
        SettingKey accent = SettingsEnvelopeTestData.Key("AppAccentColorMode");
        List<SettingsApplicationBatchEntry> entries =
        [
            SettingsApplicationBatchEntry.Create(theme, baseline.Desired[theme]),
            SettingsApplicationBatchEntry.Create(accent, baseline.Desired[accent]),
        ];

        SettingsApplicationBatch batch = CreateBatch(
            SettingsApplicationBatchState.Pending,
            entries);
        entries.Clear();

        Assert.Equal(
            ["AppAccentColorMode", "AppThemeMode"],
            batch.Entries.Select(static entry => entry.Key.Value));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<SettingsApplicationBatchEntry>>(batch.Entries)
                .Clear());
    }

    [Fact]
    public void Envelope_RejectsDuplicateMapKeys()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        KeyValuePair<SettingKey, SettingDesiredEntry> duplicate =
            baseline.Desired.First();

        Assert.Throws<ArgumentException>(() =>
            new SettingsEnvelope(
                baseline.SchemaVersion,
                baseline.EnvelopeRevision,
                [duplicate, duplicate],
                baseline.Applied,
                [],
                []));
    }

    [Fact]
    public void MigrationRecord_RejectsInvalidIdentityVersionAndHash()
    {
        string hash = SettingsApplicationBatchEntry.ComputeValueHash(
            SettingsEnvelopeTestData.Value("DisplayLanguage", "AutoDetect"));

        Assert.Throws<ArgumentException>(() =>
            new SettingsMigrationRecord(Guid.Empty, 0, 1, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SettingsMigrationRecord(Guid.NewGuid(), -1, 1, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SettingsMigrationRecord(Guid.NewGuid(), 0, 0, hash));
        Assert.Throws<ArgumentException>(() =>
            new SettingsMigrationRecord(Guid.NewGuid(), 1, 1, hash));
        Assert.Throws<ArgumentException>(() =>
            new SettingsMigrationRecord(Guid.NewGuid(), 0, 1, hash.ToUpperInvariant()));
    }

    [Fact]
    public void Validate_MigrationHistoryMustBeUniqueContiguousAndWithinSchema()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Guid migrationId = Guid.NewGuid();
        string hash = SettingsApplicationBatchEntry.ComputeValueHash(
            SettingsEnvelopeTestData.Value("DisplayLanguage", "AutoDetect"));
        SettingsEnvelope envelope = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            baseline.Desired,
            baseline.Applied,
            [],
            [
                new SettingsMigrationRecord(migrationId, 0, 1, hash),
                new SettingsMigrationRecord(migrationId, 2, 3, hash),
            ]);

        SettingsEnvelopeValidationResult result =
            new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(envelope);

        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.migration.id_duplicate");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.migration.not_contiguous");
        Assert.Contains(
            result.Errors,
            error => error.Code == "settings.envelope.migration.target_ahead");
    }

    [Fact]
    public void EnvelopeAndNestedCollections_AreDefensivelyReadOnly()
    {
        SettingsEnvelope baseline = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        Dictionary<SettingKey, SettingAppliedState> applied = baseline.Applied.ToDictionary();
        List<SettingsMigrationRecord> migrations =
        [
            new(
                Guid.NewGuid(),
                fromSchemaVersion: 0,
                toSchemaVersion: 1,
                SettingsApplicationBatchEntry.ComputeValueHash(
                    SettingsEnvelopeTestData.Value("DisplayLanguage", "AutoDetect"))),
        ];
        SettingsEnvelope envelope = new(
            baseline.SchemaVersion,
            baseline.EnvelopeRevision,
            desired,
            applied,
            [],
            migrations);

        desired.Clear();
        applied.Clear();
        migrations.Clear();

        Assert.Equal(SettingsRegistry.Default.Definitions.Count, envelope.Desired.Count);
        Assert.Equal(SettingsRegistry.Default.Definitions.Count, envelope.Applied.Count);
        Assert.Single(envelope.MigrationHistory);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IDictionary<SettingKey, SettingDesiredEntry>>(envelope.Desired)
                .Clear());
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<SettingsMigrationRecord>>(envelope.MigrationHistory)
                .Clear());
    }

    private static SettingsApplicationBatch CreateBatch(
        SettingsApplicationBatchState state,
        IEnumerable<SettingsApplicationBatchEntry> entries,
        SettingsApplicationError? lastError = null) =>
        new(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.LiveReconcile,
            creationSequence: 1,
            Guid.NewGuid(),
            state,
            SettingApplicationKind.Appearance,
            entries,
            lastError);
}
