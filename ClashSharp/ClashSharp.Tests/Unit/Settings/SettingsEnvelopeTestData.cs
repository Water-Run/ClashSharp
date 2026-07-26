using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

internal static class SettingsEnvelopeTestData
{
    public static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    public static SettingsEnvelope CreateMatchingEnvelope(SettingsRegistry? registry = null)
    {
        registry ??= SettingsRegistry.Default;
        Dictionary<SettingKey, SettingDesiredEntry> desired = [];
        Dictionary<SettingKey, SettingAppliedState> applied = [];

        foreach (SettingDefinition definition in registry.Definitions)
        {
            SettingDesiredEntry entry = new(definition.DefaultValue, keyDesiredRevision: 1);
            desired.Add(definition.Key, entry);
            applied.Add(
                definition.Key,
                SettingAppliedState.Verified(
                    definition.DefaultValue,
                    SettingAppliedValueSource.DefaultInitialization,
                    SettingsApplicationBatchEntry.ComputeValueHash(definition.DefaultValue),
                    ObservedAt));
        }

        return new SettingsEnvelope(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 1,
            desired,
            applied,
            pendingApplications: [],
            migrationHistory: []);
    }

    public static SettingsEnvelope CreatePendingEnvelope(
        IReadOnlyList<(string Key, string Value)> changes,
        SettingsApplicationBatchState state = SettingsApplicationBatchState.Pending,
        SettingsApplicationError? lastError = null)
    {
        SettingsEnvelope baseline = CreateMatchingEnvelope();
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary();
        List<SettingsApplicationBatchEntry> entries = [];
        SettingDefinition? firstDefinition = null;

        foreach ((string key, string text) in changes)
        {
            SettingDefinition definition = SettingsRegistry.Default.Get(key);
            firstDefinition ??= definition;
            if (definition.ApplicationKind != firstDefinition.ApplicationKind
                || definition.ApplicationTiming != firstDefinition.ApplicationTiming)
            {
                throw new ArgumentException(
                    "Test changes in one batch must use the same application kind and timing.",
                    nameof(changes));
            }

            SettingDesiredEntry entry = new(Value(key, text), keyDesiredRevision: 2);
            desired[definition.Key] = entry;
            entries.Add(SettingsApplicationBatchEntry.Create(definition.Key, entry));
        }

        SettingDefinition batchDefinition = firstDefinition
            ?? throw new ArgumentException("At least one test change is required.", nameof(changes));
        SettingsApplicationBatch batch = new(
            new Guid("10000000-0000-0000-0000-000000000001"),
            BatchKind(batchDefinition),
            creationSequence: 1,
            new Guid("20000000-0000-0000-0000-000000000001"),
            state,
            batchDefinition.ApplicationKind,
            entries,
            lastError);

        return new SettingsEnvelope(
            SettingsEnvelope.CurrentSchemaVersion,
            envelopeRevision: 2,
            desired,
            baseline.Applied,
            pendingApplications: [batch],
            migrationHistory: []);
    }

    public static SettingsRegistry CreateLiveAndRestartRegistry()
    {
        SettingDefinition live = SettingDefinition.CreateBoolean(
            new SettingKey("LiveAppearance"),
            defaultValue: false,
            safeFallback: false,
            Metadata(
                SettingAuthority.Internal,
                SettingApplicationKind.Appearance,
                SettingApplicationTiming.Live));
        SettingDefinition restart = SettingDefinition.CreateBoolean(
            new SettingKey("RestartInternal"),
            defaultValue: false,
            safeFallback: false,
            Metadata(
                SettingAuthority.RestartBound,
                SettingApplicationKind.Internal,
                SettingApplicationTiming.Restart));

        return SettingsRegistry.Create([live, restart]);
    }

    public static SettingValue Value(string key, string canonicalText)
    {
        SettingNormalizationResult result = SettingsRegistry.Default.Get(key).Normalize(canonicalText);
        return AssertSuccess(result);
    }

    public static SettingValue Value(
        SettingsRegistry registry,
        string key,
        string canonicalText)
    {
        SettingNormalizationResult result = registry.Get(key).Normalize(canonicalText);
        return AssertSuccess(result);
    }

    public static SettingKey Key(string value) =>
        SettingsRegistry.Default.Get(value).Key;

    public static Guid Transaction(int value)
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static SettingValue AssertSuccess(SettingNormalizationResult result)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error?.Code);
        }

        return result.Value!;
    }

    private static SettingsApplicationBatchKind BatchKind(SettingDefinition definition) =>
        definition.ApplicationTiming == SettingApplicationTiming.Restart
            ? SettingsApplicationBatchKind.Restart
            : SettingsApplicationBatchKind.LiveReconcile;

    private static SettingDefinitionMetadata Metadata(
        SettingAuthority authority,
        SettingApplicationKind applicationKind,
        SettingApplicationTiming applicationTiming)
    {
        return new SettingDefinitionMetadata(
            schemaVersion: 1,
            category: SettingCategory.General,
            resetScopes: SettingsResetScope.None,
            includeInDataPackage: false,
            authority,
            applicationKind,
            applicationTiming,
            localizationCategory: "Settings.Test",
            isSensitive: false);
    }
}
