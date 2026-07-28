using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Verifies the canonical settings registry and its strict value normalization contracts.</summary>
public sealed class SettingsRegistryTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedDefaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DisplayLanguage"] = "AutoDetect",
            ["AppThemeMode"] = "FollowSystem",
            ["AppAccentColorMode"] = "FollowSystem",
            ["AppAccentColorValue"] = "#FF0078D4",
            ["LaunchAtStartupEnabled"] = "false",
            ["CurrentMode"] = "Disabled",
            ["ActiveProfileId"] = "builtin-direct",
            ["TransparentProxyEnabled"] = "true",
            ["MixedPort"] = "10000",
            ["ConnectionSamplingEnabled"] = "true",
            ["ConnectionSamplingIntervalSeconds"] = "30",
            ["RestoreProxyOnExit"] = "true",
            ["CheckStaleProxyOnStartup"] = "true",
            ["StartupConflictCheckEnabled"] = "true",
            ["StartupBehaviorMode"] = "LastSetting",
            ["ShowStartupGuideOnStartup"] = "true",
            ["TriggersEnabled"] = "true",
            ["TriggerNotificationsEnabled"] = "true",
            ["CloseBehaviorMode"] = "MinimizeToTray",
            ["TrayUseMonochromeInactiveIcon"] = "false",
            ["TrayVisibleFeatureIds"] = "status,mode,pages,transparent-proxy,settings,safe-exit",
            ["MainlandChinaFeatureMode"] = "FlagReplacementAndTextCompletion",
            ["MainlandChinaUrlBlockingEnabled"] = "false",
            ["NotificationEnabled"] = "true",
            ["NotificationLevel"] = "Default",
            ["ConnectionTestUrl"] = "https://www.google.com/generate_204",
            ["MasterHeroStatusLayout"] =
                "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability",
            ["MasterInfoTileLayout"] =
                "core,upload-rate,download-rate,active-connections,transparent-proxy,latency,active-profile,current-mode",
            ["ConnectionTestProxyUrl1"] = "https://www.google.com",
            ["ConnectionTestProxyUrl2"] = "https://github.com",
            ["ConnectionTestDirectUrl"] = "https://www.baidu.com",
        };

    [Fact]
    public void Default_DefinesEveryCanonicalSettingExactlyOnce()
    {
        SettingsRegistry registry = SettingsRegistry.Default;

        Assert.Equal(ExpectedDefaults.Count, registry.Definitions.Count);
        Assert.Equal(
            ExpectedDefaults.Count,
            registry.Definitions.Select(static definition => definition.Key).Distinct().Count());

        foreach ((string key, string expectedDefault) in ExpectedDefaults)
        {
            Assert.True(registry.TryResolve(key, out SettingDefinition? definition, out SettingKeyResolution resolution));
            Assert.NotNull(definition);
            Assert.Equal(SettingKeyResolution.Canonical, resolution);
            Assert.Equal(key, definition.Key.Value);
            Assert.Equal(expectedDefault, definition.DefaultValue.CanonicalText);
        }
    }

    [Fact]
    public void Default_CoversLegacyDisplayFlagAsReadOnlyAlias()
    {
        SettingsRegistry registry = SettingsRegistry.Default;

        Assert.True(
            registry.TryResolve(
                "MainlandChinaDisplayEnabled",
                out SettingDefinition? definition,
                out SettingKeyResolution resolution));

        Assert.NotNull(definition);
        Assert.Equal("MainlandChinaFeatureMode", definition.Key.Value);
        Assert.Equal(SettingKeyResolution.Alias, resolution);
        Assert.Throws<KeyNotFoundException>(() => registry.Get("MainlandChinaDisplayEnabled"));
        Assert.DoesNotContain(
            registry.Definitions,
            candidate => candidate.Key.Value == "MainlandChinaDisplayEnabled");
    }

    [Fact]
    public void Default_CoversEveryWritableLegacySettingsProperty()
    {
        string[] legacyProperties = typeof(AppSettingsService)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(static property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] registeredKeys = SettingsRegistry.Default.Definitions
            .SelectMany(static definition =>
                definition.Aliases.Prepend(definition.Key).Select(static key => key.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(legacyProperties, registeredKeys);
    }

    [Fact]
    public void Default_ProvidesCompleteStableMetadata()
    {
        SettingsRegistry registry = SettingsRegistry.Default;

        Assert.All(
            registry.Definitions,
            definition =>
            {
                Assert.Equal(1, definition.SchemaVersion);
                Assert.True(Enum.IsDefined(definition.Category));
                Assert.True(Enum.IsDefined(definition.Authority));
                Assert.True(Enum.IsDefined(definition.ApplicationKind));
                Assert.True(Enum.IsDefined(definition.ApplicationTiming));
                Assert.Equal(SettingApplicationTiming.Live, definition.ApplicationTiming);
                Assert.True(definition.IncludeInDataPackage);
                Assert.StartsWith("Settings.", definition.LocalizationCategory, StringComparison.Ordinal);
                Assert.Equal(definition.ValueType, definition.DefaultValue.ValueType);
                Assert.Equal(definition.ValueType, definition.SafeFallback.ValueType);
            });

        Assert.Equal(
            [
                "LaunchAtStartupEnabled",
                "CurrentMode",
                "ActiveProfileId",
                "TransparentProxyEnabled",
                "MixedPort",
                "ConnectionSamplingEnabled",
                "ConnectionSamplingIntervalSeconds",
                "TriggersEnabled",
            ],
            registry.Definitions
                .Where(static definition => definition.Authority == SettingAuthority.ExternallyObserved)
                .Select(static definition => definition.Key.Value));

        Assert.DoesNotContain(
            registry.Definitions,
            static definition => definition.Authority == SettingAuthority.RestartBound);

        Assert.Equal(
            [
                "ConnectionTestUrl",
                "ConnectionTestProxyUrl1",
                "ConnectionTestProxyUrl2",
                "ConnectionTestDirectUrl",
            ],
            registry.Definitions
                .Where(static definition => definition.IsSensitive)
                .Select(static definition => definition.Key.Value));
    }

    [Fact]
    public void Default_DeclaresSafeFallbacksForExternallyObservedSettings()
    {
        SettingsRegistry registry = SettingsRegistry.Default;

        Assert.Equal("false", registry.Get("LaunchAtStartupEnabled").SafeFallback.CanonicalText);
        Assert.Equal("Disabled", registry.Get("CurrentMode").SafeFallback.CanonicalText);
        Assert.Equal("builtin-direct", registry.Get("ActiveProfileId").SafeFallback.CanonicalText);
        Assert.Equal("false", registry.Get("TransparentProxyEnabled").SafeFallback.CanonicalText);
        Assert.Equal("10000", registry.Get("MixedPort").SafeFallback.CanonicalText);
        Assert.Equal("false", registry.Get("ConnectionSamplingEnabled").SafeFallback.CanonicalText);
        Assert.Equal("30", registry.Get("ConnectionSamplingIntervalSeconds").SafeFallback.CanonicalText);
        Assert.Equal("false", registry.Get("TriggersEnabled").SafeFallback.CanonicalText);
    }

    [Theory]
    [MemberData(nameof(ValidNormalizationCases))]
    public void Normalize_ValidInput_ReturnsTypedCanonicalValue(
        string key,
        string input,
        string expectedCanonical)
    {
        SettingDefinition definition = SettingsRegistry.Default.Get(key);

        SettingNormalizationResult result = definition.Normalize(input);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Null(result.Error);
        Assert.Equal(expectedCanonical, result.Value.CanonicalText);
        Assert.Equal(definition.ValueType, result.Value.ValueType);
    }

    [Theory]
    [MemberData(nameof(InvalidNormalizationCases))]
    public void Normalize_InvalidInput_ReturnsStableFailureWithoutThrowing(
        string key,
        string input,
        SettingValueErrorKind expectedKind)
    {
        SettingDefinition definition = SettingsRegistry.Default.Get(key);

        SettingNormalizationResult result = definition.Normalize(input);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
        Assert.Equal(expectedKind, result.Error.Kind);
        Assert.StartsWith("settings.value.", result.Error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_NullInput_ReturnsMissingFailure()
    {
        SettingNormalizationResult result = SettingsRegistry.Default.Get("DisplayLanguage").Normalize(null);

        Assert.False(result.IsSuccess);
        Assert.Equal(SettingValueErrorKind.Missing, result.Error?.Kind);
    }

    [Fact]
    public void NormalizeValue_UndefinedOrWrongEnumValue_IsRejected()
    {
        SettingDefinition definition = SettingsRegistry.Default.Get("DisplayLanguage");

        SettingNormalizationResult undefined = definition.NormalizeValue((AppLanguage)999);
        SettingNormalizationResult wrongType = definition.NormalizeValue(999);

        Assert.False(undefined.IsSuccess);
        Assert.Equal(SettingValueErrorKind.UndefinedValue, undefined.Error?.Kind);
        Assert.False(wrongType.IsSuccess);
        Assert.Equal(SettingValueErrorKind.InvalidType, wrongType.Error?.Kind);
    }

    [Fact]
    public void NormalizeValue_DefinedButNonWritableEnumValues_AreRejected()
    {
        SettingNormalizationResult faultedMode = SettingsRegistry.Default
            .Get("CurrentMode")
            .NormalizeValue(ClashSharpMode.Faulted);
        SettingNormalizationResult legacyRegionalMode = SettingsRegistry.Default
            .Get("MainlandChinaFeatureMode")
            .NormalizeValue(MainlandChinaFeatureMode.AllIncludingUrlBlacklist);

        Assert.False(faultedMode.IsSuccess);
        Assert.Equal(SettingValueErrorKind.UndefinedValue, faultedMode.Error?.Kind);
        Assert.False(legacyRegionalMode.IsSuccess);
        Assert.Equal(SettingValueErrorKind.UndefinedValue, legacyRegionalMode.Error?.Kind);
    }

    [Fact]
    public void EnumDefinitions_ExposeOnlyCanonicalNamedOptionsAndRejectNumericText()
    {
        IReadOnlyDictionary<string, string[]> expectedOptions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["DisplayLanguage"] =
            [
                "AutoDetect",
                "SimplifiedChinese",
                "TraditionalChinese",
                "English",
                "Russian",
                "French",
                "German",
            ],
            ["AppThemeMode"] = ["FollowSystem", "Light", "Dark"],
            ["AppAccentColorMode"] = ["FollowSystem", "Custom"],
            ["CurrentMode"] = ["Disabled", "Standby", "RuleTakeover", "FullTakeover"],
            ["StartupBehaviorMode"] = ["LastSetting", "StartRuleProxy", "DisableProxy"],
            ["CloseBehaviorMode"] = ["ExitWithoutConfirmation", "ConfirmExit", "MinimizeToTray"],
            ["MainlandChinaFeatureMode"] =
            [
                "Disabled",
                "FlagReplacementOnly",
                "FlagReplacementAndTextCompletion",
                "FlagTextCompletionAndKeywordFilter",
            ],
            ["NotificationLevel"] = ["Default", "CriticalOnly", "More"],
        };

        SettingDefinition[] enumDefinitions = SettingsRegistry.Default.Definitions
            .Where(static definition => definition.ValueType.IsEnum)
            .ToArray();
        Assert.Equal(expectedOptions.Keys, enumDefinitions.Select(static definition => definition.Key.Value));

        foreach (SettingDefinition definition in enumDefinitions)
        {
            string[] expected = expectedOptions[definition.Key.Value];
            Assert.Equal(expected, definition.AllowedValues.Select(static value => value.CanonicalText));
            Assert.All(expected, name => Assert.True(definition.Normalize(name).IsSuccess));
            Assert.All(
                Enum.GetNames(definition.ValueType).Except(expected, StringComparer.Ordinal),
                name => Assert.False(definition.Normalize(name).IsSuccess));
            SettingNormalizationResult undefinedBoxed =
                definition.NormalizeValue(Enum.ToObject(definition.ValueType, 999));
            Assert.False(undefinedBoxed.IsSuccess);
            Assert.Equal(SettingValueErrorKind.UndefinedValue, undefinedBoxed.Error?.Kind);
            Assert.False(definition.Normalize("-2").IsSuccess);
            Assert.False(definition.Normalize("0").IsSuccess);
            Assert.False(definition.Normalize("999").IsSuccess);
        }
    }

    [Theory]
    [MemberData(nameof(ResetScopeCases))]
    public void GetResetDefinitions_ReturnsOnlyRegistryOwnedGroupMembers(
        SettingsResetScope scope,
        string[] expectedKeys)
    {
        IReadOnlyList<SettingDefinition> definitions = SettingsRegistry.Default.GetResetDefinitions(scope);

        Assert.Equal(expectedKeys, definitions.Select(static definition => definition.Key.Value));
    }

    [Fact]
    public void GetResetDefinitions_NoneOrUnknownScope_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SettingsRegistry.Default.GetResetDefinitions(SettingsResetScope.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SettingsRegistry.Default.GetResetDefinitions((SettingsResetScope)(1 << 20)));
    }

    [Fact]
    public void GetResetDefinitions_CombinedScope_ReturnsUnionInSchemaOrder()
    {
        IReadOnlyList<SettingDefinition> definitions = SettingsRegistry.Default.GetResetDefinitions(
            SettingsResetScope.Basic | SettingsResetScope.Startup);

        Assert.Equal(
            [
                "DisplayLanguage",
                "AppThemeMode",
                "AppAccentColorMode",
                "AppAccentColorValue",
                "LaunchAtStartupEnabled",
                "StartupConflictCheckEnabled",
                "StartupBehaviorMode",
                "ShowStartupGuideOnStartup",
                "CloseBehaviorMode",
            ],
            definitions.Select(static definition => definition.Key.Value));
    }

    [Fact]
    public void SettingValue_ProvidesExactTypedAccess()
    {
        SettingValue language = SettingsRegistry.Default.Get("DisplayLanguage").DefaultValue;
        SettingValue port = SettingsRegistry.Default.Get("MixedPort").DefaultValue;

        Assert.Equal(AppLanguage.AutoDetect, language.Get<AppLanguage>());
        Assert.Equal(10000, port.Get<int>());
        Assert.Throws<InvalidOperationException>(() => language.Get<int>());
    }

    [Fact]
    public void RegistryAndMetadataCollections_AreDefensivelyReadOnly()
    {
        List<SettingKey> aliases = [new SettingKey("LegacySynthetic")];
        SettingDefinition definition = CreateBooleanDefinition("Synthetic", aliases);
        List<SettingDefinition> source = [definition];
        SettingsRegistry registry = SettingsRegistry.Create(source);
        aliases.Clear();
        source.Clear();

        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<SettingDefinition>>(registry.Definitions).Add(definition));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<SettingKey>>(definition.Aliases).Add(new SettingKey("AnotherAlias")));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<SettingValue>>(definition.AllowedValues).Add(definition.DefaultValue));
        Assert.Single(registry.Definitions);
        Assert.Single(definition.Aliases);
    }

    [Theory]
    [InlineData(SettingAuthority.RestartBound, SettingApplicationTiming.Live)]
    [InlineData(SettingAuthority.Internal, SettingApplicationTiming.Restart)]
    [InlineData(SettingAuthority.ExternallyObserved, SettingApplicationTiming.Restart)]
    public void SettingDefinitionMetadata_RejectsContradictoryAuthorityAndTiming(
        SettingAuthority authority,
        SettingApplicationTiming applicationTiming)
    {
        Assert.Throws<ArgumentException>(() =>
            new SettingDefinitionMetadata(
                schemaVersion: 1,
                category: SettingCategory.General,
                resetScopes: SettingsResetScope.None,
                includeInDataPackage: false,
                authority,
                applicationKind: SettingApplicationKind.Internal,
                applicationTiming,
                localizationCategory: "Settings.Test",
                isSensitive: false));
    }

    [Fact]
    public void SettingDefinitionMetadata_AcceptsMatchedRestartAuthorityAndTiming()
    {
        SettingDefinitionMetadata metadata = new(
            schemaVersion: 1,
            category: SettingCategory.General,
            resetScopes: SettingsResetScope.None,
            includeInDataPackage: false,
            authority: SettingAuthority.RestartBound,
            applicationKind: SettingApplicationKind.Internal,
            applicationTiming: SettingApplicationTiming.Restart,
            localizationCategory: "Settings.Test",
            isSensitive: false);

        Assert.Equal(SettingAuthority.RestartBound, metadata.Authority);
        Assert.Equal(SettingApplicationTiming.Restart, metadata.ApplicationTiming);
    }

    [Fact]
    public void Create_RejectsDuplicateCanonicalAndAliasKeys()
    {
        SettingDefinition first = CreateBooleanDefinition("First");
        SettingDefinition aliasOwner = CreateBooleanDefinition("Second", [new SettingKey("LegacySecond")]);
        SettingDefinition aliasCollision = CreateBooleanDefinition("LegacySecond");

        Assert.Throws<ArgumentException>(() => SettingsRegistry.Create([first, first]));
        Assert.Throws<ArgumentException>(() => SettingsRegistry.Create([first, aliasOwner, aliasCollision]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Leading")]
    [InlineData("Trailing ")]
    [InlineData("Contains/Slash")]
    public void SettingKey_InvalidValue_Throws(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SettingKey(value!));
    }

    [Fact]
    public void MovedEnums_PreserveValuesAndAreOwnedByCore()
    {
        System.Reflection.Assembly coreAssembly = typeof(SettingsRegistry).Assembly;

        Assert.Equal(coreAssembly, typeof(AppLanguage).Assembly);
        Assert.Equal(coreAssembly, typeof(AppThemeMode).Assembly);
        Assert.Equal(coreAssembly, typeof(AppAccentColorMode).Assembly);
        Assert.Equal(coreAssembly, typeof(StartupBehaviorMode).Assembly);
        Assert.Equal(coreAssembly, typeof(CloseBehaviorMode).Assembly);
        Assert.Equal(coreAssembly, typeof(MainlandChinaFeatureMode).Assembly);
        Assert.Equal(coreAssembly, typeof(NotificationLevel).Assembly);
        Assert.Equal(coreAssembly, typeof(ClashDataPackageScope).Assembly);

        Assert.Equal([-1, 0, 1, 2, 3, 4, 5], NumericValues<AppLanguage>());
        Assert.Equal([0, 1, 2], NumericValues<AppThemeMode>());
        Assert.Equal([0, 1], NumericValues<AppAccentColorMode>());
        Assert.Equal([0, 1, 2], NumericValues<StartupBehaviorMode>());
        Assert.Equal([0, 1, 2], NumericValues<CloseBehaviorMode>());
        Assert.Equal([0, 1, 2, 3, 4], NumericValues<MainlandChinaFeatureMode>());
        Assert.Equal([0, 1, 2], NumericValues<NotificationLevel>());
        Assert.Equal([0, 1], NumericValues<ClashDataPackageScope>());
    }

    public static TheoryData<string, string, string> ValidNormalizationCases => new()
    {
        { "DisplayLanguage", "English", "English" },
        { "AppThemeMode", "Dark", "Dark" },
        { "LaunchAtStartupEnabled", "true", "true" },
        { "LaunchAtStartupEnabled", "false", "false" },
        { "MixedPort", "10000", "10000" },
        { "ConnectionSamplingIntervalSeconds", "3", "3" },
        { "ConnectionSamplingIntervalSeconds", "300", "300" },
        { "AppAccentColorValue", "0078d4", "#FF0078D4" },
        { "AppAccentColorValue", "#8042a5f5", "#8042A5F5" },
        { "ActiveProfileId", "profile-a_1.0", "profile-a_1.0" },
        { "TrayVisibleFeatureIds", "status, mode,status", "status,mode" },
        { "ConnectionTestUrl", "https://example.com/generate_204/", "https://example.com/generate_204" },
        { "ConnectionTestProxyUrl1", "example.com/", "https://example.com" },
        {
            "MasterHeroStatusLayout",
            "UploadRate, DownloadRate,CoreStatus,SystemProxy,TransparentProxy,CurrentNode,TotalTraffic,Availability",
            "UploadRate,DownloadRate,CoreStatus,SystemProxy,TransparentProxy,CurrentNode,TotalTraffic,Availability"
        },
        {
            "MasterHeroStatusLayout",
            "Latency,UploadRate,DownloadRate,TotalTraffic,ActiveConnections,CurrentMode,ActiveProfile,MihomoService",
            "Latency,UploadRate,DownloadRate,TotalTraffic,ActiveConnections,CurrentMode,ActiveProfile,MihomoService"
        },
        {
            "MasterInfoTileLayout",
            "Latency, core,latency,memory-usage",
            "latency,core,memory-usage"
        },
    };

    public static TheoryData<string, string, SettingValueErrorKind> InvalidNormalizationCases => new()
    {
        { "DisplayLanguage", "-2", SettingValueErrorKind.UndefinedValue },
        { "DisplayLanguage", "999", SettingValueErrorKind.UndefinedValue },
        { "DisplayLanguage", "0", SettingValueErrorKind.UndefinedValue },
        { "DisplayLanguage", "english", SettingValueErrorKind.UndefinedValue },
        { "LaunchAtStartupEnabled", "True", SettingValueErrorKind.InvalidFormat },
        { "LaunchAtStartupEnabled", "FALSE", SettingValueErrorKind.InvalidFormat },
        { "LaunchAtStartupEnabled", "1", SettingValueErrorKind.InvalidFormat },
        { "LaunchAtStartupEnabled", " true ", SettingValueErrorKind.InvalidFormat },
        { "MixedPort", "0", SettingValueErrorKind.OutOfRange },
        { "MixedPort", "65536", SettingValueErrorKind.OutOfRange },
        { "MixedPort", "010000", SettingValueErrorKind.InvalidFormat },
        { "MixedPort", "+10000", SettingValueErrorKind.InvalidFormat },
        { "MixedPort", "10000 ", SettingValueErrorKind.InvalidFormat },
        { "ConnectionSamplingIntervalSeconds", "2", SettingValueErrorKind.OutOfRange },
        { "ConnectionSamplingIntervalSeconds", "301", SettingValueErrorKind.OutOfRange },
        { "AppAccentColorValue", "#12345", SettingValueErrorKind.InvalidFormat },
        { "AppAccentColorValue", "#GG0078D4", SettingValueErrorKind.InvalidFormat },
        { "ActiveProfileId", " ", SettingValueErrorKind.UnsafeValue },
        { "ActiveProfileId", "../profile", SettingValueErrorKind.UnsafeValue },
        { "TrayVisibleFeatureIds", "status,unknown", SettingValueErrorKind.UndefinedValue },
        { "ConnectionTestUrl", "", SettingValueErrorKind.InvalidFormat },
        { "ConnectionTestUrl", "ftp://example.com", SettingValueErrorKind.InvalidFormat },
        { "ConnectionTestUrl", "https://user:password@example.com", SettingValueErrorKind.UnsafeValue },
        { "MasterHeroStatusLayout", "CoreStatus,Unknown", SettingValueErrorKind.UndefinedValue },
        { "MasterInfoTileLayout", "core,../unknown", SettingValueErrorKind.UnsafeValue },
        {
            "MasterHeroStatusLayout",
            "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability,Unknown",
            SettingValueErrorKind.UndefinedValue
        },
        {
            "MasterHeroStatusLayout",
            "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,Latency,UploadRate,DownloadRate,TotalTraffic,ActiveConnections",
            SettingValueErrorKind.OutOfRange
        },
    };

    public static TheoryData<SettingsResetScope, string[]> ResetScopeCases => new()
    {
        {
            SettingsResetScope.Basic,
            [
                "DisplayLanguage",
                "AppThemeMode",
                "AppAccentColorMode",
                "AppAccentColorValue",
                "CloseBehaviorMode",
            ]
        },
        {
            SettingsResetScope.Notifications,
            ["NotificationEnabled", "NotificationLevel"]
        },
        {
            SettingsResetScope.Startup,
            [
                "LaunchAtStartupEnabled",
                "StartupConflictCheckEnabled",
                "StartupBehaviorMode",
                "ShowStartupGuideOnStartup",
            ]
        },
        {
            SettingsResetScope.Triggers,
            ["TriggersEnabled", "TriggerNotificationsEnabled"]
        },
        {
            SettingsResetScope.Tray,
            ["TrayUseMonochromeInactiveIcon", "TrayVisibleFeatureIds"]
        },
        {
            SettingsResetScope.TransparentProxy,
            ["TransparentProxyEnabled"]
        },
        {
            SettingsResetScope.Proxy,
            [
                "TransparentProxyEnabled",
                "MixedPort",
                "ConnectionSamplingEnabled",
                "ConnectionSamplingIntervalSeconds",
                "ConnectionTestUrl",
                "ConnectionTestProxyUrl1",
                "ConnectionTestProxyUrl2",
                "ConnectionTestDirectUrl",
            ]
        },
        {
            SettingsResetScope.ConnectionTests,
            [
                "ConnectionTestUrl",
                "ConnectionTestProxyUrl1",
                "ConnectionTestProxyUrl2",
                "ConnectionTestDirectUrl",
            ]
        },
        {
            SettingsResetScope.WindowsNative,
            ["RestoreProxyOnExit", "CheckStaleProxyOnStartup"]
        },
        {
            SettingsResetScope.MainlandChina,
            ["MainlandChinaFeatureMode", "MainlandChinaUrlBlockingEnabled"]
        },
        {
            SettingsResetScope.MasterControl,
            ["MasterHeroStatusLayout", "MasterInfoTileLayout"]
        },
        {
            SettingsResetScope.All,
            ExpectedDefaults.Keys.ToArray()
        },
    };

    private static SettingDefinition CreateBooleanDefinition(
        string key,
        IEnumerable<SettingKey>? aliases = null)
    {
        SettingDefinitionMetadata metadata = new(
            schemaVersion: 1,
            category: SettingCategory.General,
            resetScopes: SettingsResetScope.None,
            includeInDataPackage: false,
            authority: SettingAuthority.Internal,
            applicationKind: SettingApplicationKind.Internal,
            applicationTiming: SettingApplicationTiming.Live,
            localizationCategory: "Settings.Test",
            isSensitive: false,
            aliases: aliases);

        return SettingDefinition.CreateBoolean(
            new SettingKey(key),
            defaultValue: false,
            safeFallback: false,
            metadata);
    }

    private static IEnumerable<int> NumericValues<TEnum>()
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()
            .Select(static value => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture))
            .Order();
    }
}
