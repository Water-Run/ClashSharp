using System.Collections.Frozen;
using ClashSharp.Settings;

namespace ClashSharp.Agent;

/// <summary>Classifies agent operations and setting writes without deriving authority from caller-provided tool names.</summary>
public static class McpAccessRules
{
    private static readonly FrozenSet<SettingKey> ConfigurationSettings = new[]
    {
        SettingsRegistry.Keys.DataUnitStyle, SettingsRegistry.Keys.RateUnit, SettingsRegistry.Keys.ClockFormat,
        SettingsRegistry.Keys.DisplayLanguage, SettingsRegistry.Keys.AppThemeMode, SettingsRegistry.Keys.AppAccentColorMode,
        SettingsRegistry.Keys.AppAccentColorValue, SettingsRegistry.Keys.ConnectionSamplingEnabled,
        SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds, SettingsRegistry.Keys.ShowStartupGuideOnStartup,
        SettingsRegistry.Keys.TriggerNotificationsEnabled, SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon,
        SettingsRegistry.Keys.TrayVisibleFeatureIds, SettingsRegistry.Keys.NotificationEnabled,
        SettingsRegistry.Keys.NotificationLevel, SettingsRegistry.Keys.MasterHeroStatusLayout,
        SettingsRegistry.Keys.MasterInfoTileLayout,
    }.ToFrozenSet();

    private static readonly FrozenSet<SettingKey> ConnectivitySettings = new[]
    {
        SettingsRegistry.Keys.CurrentMode, SettingsRegistry.Keys.ActiveProfileId, SettingsRegistry.Keys.TransparentProxyEnabled,
        SettingsRegistry.Keys.MixedPort, SettingsRegistry.Keys.LaunchAtStartupEnabled, SettingsRegistry.Keys.RestoreProxyOnExit,
        SettingsRegistry.Keys.CheckStaleProxyOnStartup, SettingsRegistry.Keys.StartupConflictCheckEnabled,
        SettingsRegistry.Keys.StartupBehaviorMode, SettingsRegistry.Keys.CloseBehaviorMode,
    }.ToFrozenSet();

    /// <summary>Gets every permission required by a product operation, including sensitive-read and connectivity combinations.</summary>
    /// <param name="operation">A known product operation class.</param>
    /// <returns>The complete required grant set.</returns>
    public static McpAccessScope RequiredScopes(McpOperation operation) => operation switch
    {
        McpOperation.ReadStatus => McpAccessScope.ReadStatus,
        McpOperation.ReadConfiguration => McpAccessScope.ReadConfiguration,
        McpOperation.ReadSensitiveConfiguration => McpAccessScope.ReadConfiguration | McpAccessScope.ReadSensitiveConfiguration,
        McpOperation.ModifyConfiguration => McpAccessScope.ModifyConfiguration,
        McpOperation.ModifyLiveConfiguration => McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState,
        McpOperation.ReadDiagnostics => McpAccessScope.ReadDiagnostics,
        McpOperation.ReadHistorySummary => McpAccessScope.ReadHistorySummary,
        McpOperation.ReadHistoryDetails => McpAccessScope.ReadHistoryDetails,
        McpOperation.ReadSensitiveHistory => McpAccessScope.ReadHistoryDetails | McpAccessScope.ReadSensitiveConfiguration,
        McpOperation.ChangeProxyState => McpAccessScope.ChangeProxyState,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    /// <summary>Classifies a canonical setting write; unclassified, aliased, trigger-engine and MCP-control keys are refused.</summary>
    /// <param name="key">An exact canonical setting key.</param>
    /// <returns>The complete permission requirement, or null when agent writes are not supported.</returns>
    public static McpAccessScope? RequiredSettingWriteScopes(SettingKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (ConfigurationSettings.Contains(key)) { return McpAccessScope.ModifyConfiguration; }
        if (ConnectivitySettings.Contains(key)) { return McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState; }
        return null;
    }
}
