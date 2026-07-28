using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Locks every canonical setting to its complete registry metadata contract.</summary>
public sealed class SettingsRegistryMetadataTests
{
    [Fact]
    public void Default_DefinitionsMatchCompleteExpectedMatrix()
    {
        Assert.Equal(ExpectedDefinitions.Count, SettingsRegistry.Default.Definitions.Count);

        for (int index = 0; index < ExpectedDefinitions.Count; index++)
        {
            ExpectedDefinition expected = ExpectedDefinitions[index];
            SettingDefinition actual = SettingsRegistry.Default.Definitions[index];

            Assert.Equal(expected.Key, actual.Key.Value);
            Assert.Equal(expected.ValueType, actual.ValueType);
            Assert.Equal(expected.DefaultValue, actual.DefaultValue.CanonicalText);
            Assert.Equal(expected.SafeFallback, actual.SafeFallback.CanonicalText);
            Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
            Assert.Equal(expected.Category, actual.Category);
            Assert.Equal(expected.ResetScopes, actual.ResetScopes);
            Assert.Equal(expected.IncludeInDataPackage, actual.IncludeInDataPackage);
            Assert.Equal(expected.Authority, actual.Authority);
            Assert.Equal(expected.ApplicationKind, actual.ApplicationKind);
            Assert.Equal(expected.ApplicationTiming, actual.ApplicationTiming);
            Assert.Equal(expected.LocalizationCategory, actual.LocalizationCategory);
            Assert.Equal(expected.IsSensitive, actual.IsSensitive);
            Assert.Equal(expected.Aliases, actual.Aliases.Select(static alias => alias.Value));
        }
    }

    private static readonly IReadOnlyList<ExpectedDefinition> ExpectedDefinitions =
    [
        Internal(
            "DisplayLanguage",
            typeof(AppLanguage),
            "AutoDetect",
            SettingCategory.Appearance,
            SettingsResetScope.Basic,
            SettingApplicationKind.Appearance,
            "Settings.Basic"),
        Internal(
            "AppThemeMode",
            typeof(AppThemeMode),
            "FollowSystem",
            SettingCategory.Appearance,
            SettingsResetScope.Basic,
            SettingApplicationKind.Appearance,
            "Settings.Basic"),
        Internal(
            "AppAccentColorMode",
            typeof(AppAccentColorMode),
            "FollowSystem",
            SettingCategory.Appearance,
            SettingsResetScope.Basic,
            SettingApplicationKind.Appearance,
            "Settings.Basic"),
        Internal(
            "AppAccentColorValue",
            typeof(string),
            "#FF0078D4",
            SettingCategory.Appearance,
            SettingsResetScope.Basic,
            SettingApplicationKind.Appearance,
            "Settings.Basic"),
        External(
            "LaunchAtStartupEnabled",
            typeof(bool),
            "false",
            "false",
            SettingCategory.Startup,
            SettingsResetScope.Startup,
            SettingApplicationKind.StartupTask,
            "Settings.Startup"),
        External(
            "CurrentMode",
            typeof(ClashSharpMode),
            "Disabled",
            "Disabled",
            SettingCategory.Network,
            SettingsResetScope.None,
            SettingApplicationKind.Network,
            "Settings.Proxy"),
        External(
            "ActiveProfileId",
            typeof(string),
            "builtin-direct",
            "builtin-direct",
            SettingCategory.Network,
            SettingsResetScope.None,
            SettingApplicationKind.Network,
            "Settings.Proxy"),
        External(
            "TransparentProxyEnabled",
            typeof(bool),
            "true",
            "false",
            SettingCategory.Network,
            SettingsResetScope.TransparentProxy | SettingsResetScope.Proxy,
            SettingApplicationKind.Network,
            "Settings.Proxy"),
        External(
            "MixedPort",
            typeof(int),
            "10000",
            "10000",
            SettingCategory.Network,
            SettingsResetScope.Proxy,
            SettingApplicationKind.Network,
            "Settings.Proxy"),
        External(
            "ConnectionSamplingEnabled",
            typeof(bool),
            "true",
            "false",
            SettingCategory.Sampling,
            SettingsResetScope.Proxy,
            SettingApplicationKind.Sampling,
            "Settings.Proxy"),
        External(
            "ConnectionSamplingIntervalSeconds",
            typeof(int),
            "30",
            "30",
            SettingCategory.Sampling,
            SettingsResetScope.Proxy,
            SettingApplicationKind.Sampling,
            "Settings.Proxy"),
        Internal(
            "RestoreProxyOnExit",
            typeof(bool),
            "true",
            SettingCategory.Network,
            SettingsResetScope.WindowsNative,
            SettingApplicationKind.Internal,
            "Settings.WindowsNative"),
        Internal(
            "CheckStaleProxyOnStartup",
            typeof(bool),
            "true",
            SettingCategory.Network,
            SettingsResetScope.WindowsNative,
            SettingApplicationKind.Internal,
            "Settings.WindowsNative"),
        Internal(
            "StartupConflictCheckEnabled",
            typeof(bool),
            "true",
            SettingCategory.Startup,
            SettingsResetScope.Startup,
            SettingApplicationKind.Internal,
            "Settings.Startup"),
        Internal(
            "StartupBehaviorMode",
            typeof(StartupBehaviorMode),
            "LastSetting",
            SettingCategory.Startup,
            SettingsResetScope.Startup,
            SettingApplicationKind.Internal,
            "Settings.Startup"),
        Internal(
            "ShowStartupGuideOnStartup",
            typeof(bool),
            "true",
            SettingCategory.Startup,
            SettingsResetScope.Startup,
            SettingApplicationKind.Internal,
            "Settings.Startup"),
        External(
            "TriggersEnabled",
            typeof(bool),
            "true",
            "false",
            SettingCategory.Triggers,
            SettingsResetScope.Triggers,
            SettingApplicationKind.Triggers,
            "Settings.Triggers"),
        Internal(
            "TriggerNotificationsEnabled",
            typeof(bool),
            "true",
            SettingCategory.Triggers,
            SettingsResetScope.Triggers,
            SettingApplicationKind.Triggers,
            "Settings.Triggers"),
        Internal(
            "CloseBehaviorMode",
            typeof(CloseBehaviorMode),
            "MinimizeToTray",
            SettingCategory.General,
            SettingsResetScope.Basic,
            SettingApplicationKind.Internal,
            "Settings.Basic"),
        Internal(
            "TrayUseMonochromeInactiveIcon",
            typeof(bool),
            "false",
            SettingCategory.Tray,
            SettingsResetScope.Tray,
            SettingApplicationKind.Appearance,
            "Settings.Tray"),
        Internal(
            "TrayVisibleFeatureIds",
            typeof(string),
            "status,mode,pages,transparent-proxy,settings,safe-exit",
            SettingCategory.Tray,
            SettingsResetScope.Tray,
            SettingApplicationKind.Appearance,
            "Settings.Tray"),
        Internal(
            "MainlandChinaFeatureMode",
            typeof(MainlandChinaFeatureMode),
            "FlagReplacementAndTextCompletion",
            SettingCategory.Regional,
            SettingsResetScope.MainlandChina,
            SettingApplicationKind.Appearance,
            "Settings.MainlandChina",
            aliases: ["MainlandChinaDisplayEnabled"]),
        Internal(
            "MainlandChinaUrlBlockingEnabled",
            typeof(bool),
            "false",
            SettingCategory.Regional,
            SettingsResetScope.MainlandChina,
            SettingApplicationKind.Appearance,
            "Settings.MainlandChina"),
        Internal(
            "NotificationEnabled",
            typeof(bool),
            "true",
            SettingCategory.Notifications,
            SettingsResetScope.Notifications,
            SettingApplicationKind.Internal,
            "Settings.Notifications"),
        Internal(
            "NotificationLevel",
            typeof(NotificationLevel),
            "Default",
            SettingCategory.Notifications,
            SettingsResetScope.Notifications,
            SettingApplicationKind.Internal,
            "Settings.Notifications"),
        Internal(
            "ConnectionTestUrl",
            typeof(string),
            "https://www.google.com/generate_204",
            SettingCategory.Network,
            SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
            SettingApplicationKind.Internal,
            "Settings.ConnectionTest",
            isSensitive: true),
        Internal(
            "MasterHeroStatusLayout",
            typeof(string),
            "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability",
            SettingCategory.Appearance,
            SettingsResetScope.MasterControl,
            SettingApplicationKind.Appearance,
            "Settings.MasterControl"),
        Internal(
            "MasterInfoTileLayout",
            typeof(string),
            "core,upload-rate,download-rate,active-connections,transparent-proxy,latency,active-profile,current-mode",
            SettingCategory.Appearance,
            SettingsResetScope.MasterControl,
            SettingApplicationKind.Appearance,
            "Settings.MasterControl"),
        Internal(
            "ConnectionTestProxyUrl1",
            typeof(string),
            "https://www.google.com",
            SettingCategory.Network,
            SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
            SettingApplicationKind.Internal,
            "Settings.ConnectionTest",
            isSensitive: true),
        Internal(
            "ConnectionTestProxyUrl2",
            typeof(string),
            "https://github.com",
            SettingCategory.Network,
            SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
            SettingApplicationKind.Internal,
            "Settings.ConnectionTest",
            isSensitive: true),
        Internal(
            "ConnectionTestDirectUrl",
            typeof(string),
            "https://www.baidu.com",
            SettingCategory.Network,
            SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
            SettingApplicationKind.Internal,
            "Settings.ConnectionTest",
            isSensitive: true),
    ];

    private static ExpectedDefinition Internal(
        string key,
        Type valueType,
        string defaultValue,
        SettingCategory category,
        SettingsResetScope resetScopes,
        SettingApplicationKind applicationKind,
        string localizationCategory,
        bool isSensitive = false,
        string[]? aliases = null)
    {
        return new ExpectedDefinition(
            key,
            valueType,
            defaultValue,
            defaultValue,
            SchemaVersion: 1,
            category,
            resetScopes,
            IncludeInDataPackage: true,
            SettingAuthority.Internal,
            applicationKind,
            SettingApplicationTiming.Live,
            localizationCategory,
            isSensitive,
            aliases ?? []);
    }

    private static ExpectedDefinition External(
        string key,
        Type valueType,
        string defaultValue,
        string safeFallback,
        SettingCategory category,
        SettingsResetScope resetScopes,
        SettingApplicationKind applicationKind,
        string localizationCategory)
    {
        return new ExpectedDefinition(
            key,
            valueType,
            defaultValue,
            safeFallback,
            SchemaVersion: 1,
            category,
            resetScopes,
            IncludeInDataPackage: true,
            SettingAuthority.ExternallyObserved,
            applicationKind,
            SettingApplicationTiming.Live,
            localizationCategory,
            IsSensitive: false,
            Aliases: []);
    }

    private sealed record ExpectedDefinition(
        string Key,
        Type ValueType,
        string DefaultValue,
        string SafeFallback,
        int SchemaVersion,
        SettingCategory Category,
        SettingsResetScope ResetScopes,
        bool IncludeInDataPackage,
        SettingAuthority Authority,
        SettingApplicationKind ApplicationKind,
        SettingApplicationTiming ApplicationTiming,
        string LocalizationCategory,
        bool IsSensitive,
        IReadOnlyList<string> Aliases);
}
