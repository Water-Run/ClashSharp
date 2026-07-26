using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Stress-tests invariant preservation across deterministic randomized edit sequences.</summary>
public sealed class SettingsEnvelopeEditorStressTests
{
    private static readonly ChangeDomain[] Domains =
    [
        new("AppThemeMode", "FollowSystem", "Dark"),
        new("AppAccentColorMode", "FollowSystem", "Custom"),
        new("LaunchAtStartupEnabled", "false", "true"),
        new("MixedPort", "10000", "10001"),
        new("ConnectionSamplingEnabled", "true", "false"),
        new("NotificationEnabled", "true", "false"),
        new(
            "MainlandChinaFeatureMode",
            "FlagReplacementAndTextCompletion",
            "Disabled"),
    ];

    [Fact]
    public void ApplyAndRevert_TwoThousandRandomizedTransitions_PreserveEveryInvariant()
    {
        SettingsRegistry registry = SettingsRegistry.Default;
        SettingsEnvelopeValidator validator = new(registry);
        SettingsEnvelopeEditor editor = new(registry);
        SettingsEnvelope current = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Random random = new(0x5E771A65);

        for (int iteration = 1; iteration <= 2_000; iteration++)
        {
            HashSet<SettingKey> selected = SelectTouchedKeys(random);
            bool revert = iteration % 4 == 0 && HasNonDefaultValue(current, selected);
            HashSet<SettingKey> touched = revert
                ? selected
                    .Where(key => IsNonDefault(current, key))
                    .ToHashSet()
                : selected;
            IReadOnlyDictionary<SettingKey, SettingDesiredEntry> priorDesired = current.Desired;
            Dictionary<SettingKey, AttemptSnapshot> priorAttempts =
                SnapshotAttempts(current, touched);
            SettingsEnvelopeEditResult result = revert
                ? editor.Revert(
                    current,
                    touched,
                    SettingsEnvelopeTestData.Transaction(iteration + 1_000))
                : editor.ApplyChanges(
                    current,
                    CreateToggleChanges(current, touched),
                    SettingsEnvelopeTestData.Transaction(iteration + 1_000));

            Assert.True(result.IsSuccess);
            Assert.Equal(SettingsEnvelopeEditOutcome.Updated, result.Outcome);
            Assert.Equal(current.EnvelopeRevision + 1, result.Envelope.EnvelopeRevision);
            Assert.True(validator.Validate(result.Envelope).IsValid);

            foreach (SettingKey key in current.Desired.Keys)
            {
                if (touched.Contains(key))
                {
                    Assert.Equal(
                        checked(priorDesired[key].KeyDesiredRevision + 1),
                        result.Envelope.Desired[key].KeyDesiredRevision);
                }
                else
                {
                    Assert.Same(priorDesired[key], result.Envelope.Desired[key]);
                }
            }

            AssertUnrelatedAttemptsUnchanged(result.Envelope, priorAttempts);
            AssertExactCoverage(result.Envelope);
            current = result.Envelope;
        }
    }

    private static HashSet<SettingKey> SelectTouchedKeys(Random random)
    {
        int count = random.Next(1, 4);
        HashSet<SettingKey> keys = [];
        while (keys.Count < count)
        {
            keys.Add(SettingsEnvelopeTestData.Key(Domains[random.Next(Domains.Length)].Key));
        }

        return keys;
    }

    private static bool HasNonDefaultValue(
        SettingsEnvelope envelope,
        IEnumerable<SettingKey> keys) =>
        keys.Any(key => IsNonDefault(envelope, key));

    private static bool IsNonDefault(SettingsEnvelope envelope, SettingKey key)
    {
        ChangeDomain domain = Domains.Single(candidate => candidate.Key == key.Value);
        return envelope.Desired[key].Value.CanonicalText != domain.DefaultValue;
    }

    private static IEnumerable<SettingValueChange> CreateToggleChanges(
        SettingsEnvelope envelope,
        IEnumerable<SettingKey> keys)
    {
        foreach (SettingKey key in keys)
        {
            ChangeDomain domain = Domains.Single(candidate => candidate.Key == key.Value);
            string target = envelope.Desired[key].Value.CanonicalText == domain.DefaultValue
                ? domain.AlternateValue
                : domain.DefaultValue;
            yield return new SettingValueChange(
                key,
                SettingsEnvelopeTestData.Value(key.Value, target));
        }
    }

    private static Dictionary<SettingKey, AttemptSnapshot> SnapshotAttempts(
        SettingsEnvelope envelope,
        IReadOnlySet<SettingKey> touched)
    {
        Dictionary<SettingKey, AttemptSnapshot> snapshots = [];
        foreach (SettingsApplicationBatch batch in envelope.PendingApplications)
        {
            foreach (SettingsApplicationBatchEntry entry in batch.Entries)
            {
                if (!touched.Contains(entry.Key))
                {
                    snapshots.Add(
                        entry.Key,
                        new AttemptSnapshot(
                            batch.BatchId,
                            batch.Kind,
                            batch.CreationSequence,
                            batch.AttemptId,
                            batch.State,
                            batch.LastError?.Code,
                            entry));
                }
            }
        }

        return snapshots;
    }

    private static void AssertUnrelatedAttemptsUnchanged(
        SettingsEnvelope envelope,
        IReadOnlyDictionary<SettingKey, AttemptSnapshot> expected)
    {
        foreach ((SettingKey key, AttemptSnapshot snapshot) in expected)
        {
            SettingsApplicationBatch batch = envelope.PendingApplications.Single(
                candidate => candidate.Entries.Any(entry => entry.Key == key));
            SettingsApplicationBatchEntry entry =
                batch.Entries.Single(candidate => candidate.Key == key);

            Assert.Equal(snapshot.BatchId, batch.BatchId);
            Assert.Equal(snapshot.Kind, batch.Kind);
            Assert.Equal(snapshot.CreationSequence, batch.CreationSequence);
            Assert.Equal(snapshot.AttemptId, batch.AttemptId);
            Assert.Equal(snapshot.State, batch.State);
            Assert.Equal(snapshot.LastErrorCode, batch.LastError?.Code);
            Assert.Same(snapshot.Entry, entry);
        }
    }

    private static void AssertExactCoverage(SettingsEnvelope envelope)
    {
        foreach (SettingDefinition definition in SettingsRegistry.Default.Definitions)
        {
            SettingDesiredEntry desired = envelope.Desired[definition.Key];
            SettingAppliedState applied = envelope.Applied[definition.Key];
            int coverage = envelope.PendingApplications.Sum(
                batch => batch.Entries.Count(entry => entry.Key == definition.Key));
            bool expected = !desired.Value.Equals(applied.Value);
            Assert.Equal(expected ? 1 : 0, coverage);
        }
    }

    private sealed record ChangeDomain(
        string Key,
        string DefaultValue,
        string AlternateValue);

    private sealed record AttemptSnapshot(
        Guid BatchId,
        SettingsApplicationBatchKind Kind,
        long CreationSequence,
        Guid AttemptId,
        SettingsApplicationBatchState State,
        string? LastErrorCode,
        SettingsApplicationBatchEntry Entry);
}
