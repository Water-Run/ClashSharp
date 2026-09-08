using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Checks legacy normalization without claiming unobserved runtime settings are already applied.</summary>
public sealed class SettingsMigrationPlannerTests
{
    private static readonly Guid MigrationId = new("286626cc-94a3-4470-9b80-b32c4f1df2a9");

    [Fact]
    public void EmptySource_PreservesDefaultsAndQueuesEveryUnobservedApplication()
    {
        SettingsMigrationPlan plan = Plan([]);
        Assert.True(new SettingsEnvelopeValidator(SettingsRegistry.Default).Validate(plan.Envelope).IsValid);
        Assert.Equal(1, plan.Envelope.EnvelopeRevision);
        Assert.Equal(SettingsRegistry.Default.Definitions.Count, plan.Envelope.Desired.Count);
        foreach (SettingDefinition definition in SettingsRegistry.Default.Definitions)
        {
            Assert.Equal(definition.DefaultValue, plan.Envelope.Desired[definition.Key].Value);
            SettingAppliedState state = plan.Envelope.Applied[definition.Key];
            Assert.Equal(SettingAppliedStateKind.Unknown, state.Kind);
            Assert.Equal(SettingAppliedUnknownReason.NotObserved, state.UnknownReason);
            Assert.Null(state.Value);
            Assert.Null(state.ObservedHash);
            Assert.Single(plan.Envelope.PendingApplications.SelectMany(batch => batch.Entries), entry => entry.Key == definition.Key);
        }

        Assert.All(plan.Diagnostics, value => Assert.Equal("settings.migration.default", value.Code));
        Assert.Equal(MigrationId, Assert.Single(plan.Envelope.MigrationHistory).MigrationId);
    }

    [Theory]
    [InlineData("LaunchAtStartupEnabled", true, "true")]
    [InlineData("MixedPort", 7890, "7890")]
    [InlineData("ConnectionSamplingIntervalSeconds", 3, "3")]
    [InlineData("ConnectionSamplingIntervalSeconds", 300, "300")]
    [InlineData("AppThemeMode", 1, "Light")]
    [InlineData("AppAccentColorValue", "#00aa22", "#FF00AA22")]
    public void CanonicalLegacyPrimitives_AreNormalized(string key, object raw, string canonical)
    {
        SettingsMigrationPlan plan = Plan(new() { [key] = raw });
        Assert.Equal(canonical, plan.Envelope.Desired[new(key)].Value.CanonicalText);
        Assert.Equal("settings.migration.canonical", Assert.Single(plan.Diagnostics, item => item.Key.Value == key).Code);
    }

    [Theory]
    [InlineData("LaunchAtStartupEnabled", "true")]
    [InlineData("MixedPort", 0)]
    [InlineData("ConnectionSamplingIntervalSeconds", 301)]
    [InlineData("AppThemeMode", 999)]
    [InlineData("AppThemeMode", "Dark")]
    public void InvalidOrMistypedSource_UsesDeclaredFallbackWithoutUncontrolledConversion(string key, object raw)
    {
        SettingsMigrationPlan plan = Plan(new() { [key] = raw });
        Assert.Equal(SettingsRegistry.Default.Get(key).SafeFallback, plan.Envelope.Desired[new(key)].Value);
        Assert.Equal("settings.migration.invalid_fallback", Assert.Single(plan.Diagnostics, item => item.Key.Value == key).Code);
    }

    [Fact]
    public void EveryRegisteredLegacyEnum_PreservesEveryAllowedIntegerValue()
    {
        foreach (SettingDefinition definition in SettingsRegistry.Default.Definitions.Where(item => item.ValueType.IsEnum))
        {
            foreach (SettingValue allowed in definition.AllowedValues)
            {
                int raw = Convert.ToInt32(Enum.Parse(definition.ValueType, allowed.CanonicalText), System.Globalization.CultureInfo.InvariantCulture);
                SettingsMigrationPlan plan = Plan(new() { [definition.Key.Value] = raw });
                Assert.Equal(allowed, plan.Envelope.Desired[definition.Key].Value);
                Assert.Equal("settings.migration.canonical", Assert.Single(plan.Diagnostics, item => item.Key == definition.Key).Code);
            }
        }
    }

    [Fact]
    public void MixedApplicationTimings_QueueLiveWorkBeforeRestartWork()
    {
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        SettingsMigrationPlan plan = new SettingsMigrationPlanner(registry).CreatePlan(new(registry, new Dictionary<string, object?>()), MigrationId);
        Assert.True(new SettingsEnvelopeValidator(registry).Validate(plan.Envelope).IsValid);
        Assert.Equal(SettingsApplicationBatchKind.LiveReconcile, plan.Envelope.PendingApplications[0].Kind);
        Assert.Equal(SettingsApplicationBatchKind.Restart, plan.Envelope.PendingApplications[1].Kind);
    }

    [Theory]
    [InlineData(false, MainlandChinaFeatureMode.Disabled)]
    [InlineData(true, MainlandChinaFeatureMode.FlagReplacementAndTextCompletion)]
    public void LegacyRegionalBoolean_IsConvertedToCanonicalMode(bool alias, MainlandChinaFeatureMode expected)
    {
        SettingsMigrationPlan plan = Plan(new() { ["MainlandChinaDisplayEnabled"] = alias });
        Assert.Equal(expected, plan.Envelope.Desired[SettingsRegistry.Keys.MainlandChinaFeatureMode].Value.Get<MainlandChinaFeatureMode>());
        Assert.DoesNotContain(SettingsRegistry.Keys.MainlandChinaDisplayEnabled, plan.Envelope.Desired.Keys);
    }

    [Fact]
    public void CanonicalRegionalMode_TakesPriorityOverTheLegacyAlias()
    {
        SettingsMigrationPlan plan = Plan(new() { ["MainlandChinaFeatureMode"] = 1, ["MainlandChinaDisplayEnabled"] = false });
        Assert.Equal(MainlandChinaFeatureMode.FlagReplacementOnly,
            plan.Envelope.Desired[SettingsRegistry.Keys.MainlandChinaFeatureMode].Value.Get<MainlandChinaFeatureMode>());
    }

    [Fact]
    public void DeprecatedCombinedRegionalMode_PreservesBothEffectiveLegacyChoices()
    {
        SettingsMigrationPlan plan = Plan(new() { ["MainlandChinaFeatureMode"] = 4, ["MainlandChinaUrlBlockingEnabled"] = false });
        Assert.Equal(MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter,
            plan.Envelope.Desired[SettingsRegistry.Keys.MainlandChinaFeatureMode].Value.Get<MainlandChinaFeatureMode>());
        Assert.True(plan.Envelope.Desired[SettingsRegistry.Keys.MainlandChinaUrlBlockingEnabled].Value.Get<bool>());
        Assert.Equal(2, plan.Diagnostics.Count(item => item.Code == "settings.migration.legacy_mode_split"));
    }

    [Fact]
    public void InternalCredentialsAndUnknownKeys_AreExcludedFromMigrationIdentityAndOutput()
    {
        LegacySettingsSnapshot clean = new(SettingsRegistry.Default, new Dictionary<string, object?> { ["MixedPort"] = 7890 });
        LegacySettingsSnapshot privateValues = new(SettingsRegistry.Default, new Dictionary<string, object?>
        {
            ["MixedPort"] = 7890,
            ["MihomoControllerSecret"] = new ExplosiveValue(),
            ["unregistered"] = new ExplosiveValue(),
        });

        Assert.Equal(clean.SourceHash, privateValues.SourceHash);
        Assert.False(privateValues.TryGetValue("MihomoControllerSecret", out _));
        SettingsMigrationPlan plan = new SettingsMigrationPlanner(SettingsRegistry.Default).CreatePlan(privateValues, MigrationId);
        Assert.DoesNotContain(plan.Diagnostics, item => item.Key.Value.Contains("Secret", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedRegisteredObject_IsNotStringified()
    {
        SettingsMigrationPlan plan = Plan(new() { ["MixedPort"] = new ExplosiveValue() });
        Assert.Equal(10000, plan.Envelope.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
    }

    [Fact]
    public void SourceComparer_CannotPromoteAnUnregisteredKeyCasingIntoTheAllowlist()
    {
        LegacySettingsSnapshot source = new(SettingsRegistry.Default, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mixedport"] = 7890,
        });
        Assert.False(source.TryGetValue("MixedPort", out _));
        Assert.Equal(new LegacySettingsSnapshot(SettingsRegistry.Default, new Dictionary<string, object?>()).SourceHash, source.SourceHash);
    }

    [Fact]
    public void SnapshotAndPlanIdentity_AreImmutableAndIndependentOfInputEnumerationOrder()
    {
        Dictionary<string, object?> source = new() { ["MixedPort"] = 7890, ["LaunchAtStartupEnabled"] = true };
        LegacySettingsSnapshot snapshot = new(SettingsRegistry.Default, source);
        LegacySettingsSnapshot reordered = new(SettingsRegistry.Default, new Dictionary<string, object?>
        {
            ["LaunchAtStartupEnabled"] = true,
            ["MixedPort"] = 7890,
        });
        source["MixedPort"] = 10001;
        SettingsMigrationPlanner planner = new(SettingsRegistry.Default);
        SettingsMigrationPlan first = planner.CreatePlan(snapshot, MigrationId);
        SettingsMigrationPlan second = planner.CreatePlan(reordered, MigrationId);

        Assert.Equal(snapshot.SourceHash, reordered.SourceHash);
        Assert.Equal(7890, first.Envelope.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.Equal(SettingsEnvelopeCodec.Encode(first.Envelope, SettingsRegistry.Default).ContentHash,
            SettingsEnvelopeCodec.Encode(second.Envelope, SettingsRegistry.Default).ContentHash);
        Assert.Equal(snapshot.SourceHash, Assert.Single(first.Envelope.MigrationHistory).SourceHash);
    }

    [Fact]
    public void OversizedRegisteredValue_RejectsWithoutIncludingTheValueInDiagnostics()
    {
        string input = new('x', 1024 * 1024 + 1);
        ArgumentException result = Assert.Throws<ArgumentException>(() =>
            new LegacySettingsSnapshot(SettingsRegistry.Default, new Dictionary<string, object?> { ["ConnectionTestUrl"] = input }));
        Assert.DoesNotContain(input, result.Message, StringComparison.Ordinal);
    }

    private static SettingsMigrationPlan Plan(Dictionary<string, object?> values) =>
        new SettingsMigrationPlanner(SettingsRegistry.Default).CreatePlan(new LegacySettingsSnapshot(SettingsRegistry.Default, values), MigrationId);

    private sealed class ExplosiveValue
    {
        public override string ToString() => throw new InvalidOperationException("Arbitrary legacy objects must not be inspected.");
    }
}
