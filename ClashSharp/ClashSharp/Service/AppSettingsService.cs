/*
 * Application Settings Service
 * Provides persistent access to user-facing Clash# behavior switches and recovery policies
 *
 * @author: WaterRun
 * @file: Service/AppSettingsService.cs
 * @date: 2026-06-24
 */

using System;
using System.Collections.Generic;
using ClashSharp.Model;
using Windows.Storage;

namespace ClashSharp.Service;

/// <summary>Provides persistent storage for user-facing application settings and network behavior policies.</summary>
/// <remarks>
/// Invariants: Every setting has an explicit default value and can be read before any user modification.
/// Thread safety: Public members serialize access through a private lock.
/// Side effects: Writes setting changes to Windows local settings when available, otherwise to an in-memory fallback.
/// </remarks>
public sealed class AppSettingsService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="AppSettingsService"/> instance.</value>
    public static AppSettingsService Instance { get; } = new();

    /// <summary>Synchronization object guarding settings access for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Fallback settings map used when Windows application local settings are unavailable.</summary>
    private readonly Dictionary<string, object> _fallbackValues = [];

    /// <summary>Windows local settings container cached for this service lifetime when available.</summary>
    private readonly ApplicationDataContainer? _localSettings;

    /// <summary>Storage key for the selected display language.</summary>
    private const string KeyDisplayLanguage = "DisplayLanguage";

    /// <summary>Storage key for the selected display style.</summary>
    private const string KeyAppThemeMode = "AppThemeMode";

    /// <summary>Storage key for the selected application accent color behavior.</summary>
    private const string KeyAppAccentColorMode = "AppAccentColorMode";

    /// <summary>Storage key for the custom application accent color.</summary>
    private const string KeyAppAccentColorValue = "AppAccentColorValue";

    /// <summary>Storage key for launching Clash# when the user signs in.</summary>
    private const string KeyLaunchAtStartupEnabled = "LaunchAtStartupEnabled";

    /// <summary>Storage key for the currently selected master takeover mode.</summary>
    private const string KeyCurrentMode = "CurrentMode";

    /// <summary>Storage key for the currently active configuration profile.</summary>
    private const string KeyActiveProfileId = "ActiveProfileId";

    /// <summary>Storage key for the transparent proxy switch.</summary>
    private const string KeyTransparentProxyEnabled = "TransparentProxyEnabled";

    /// <summary>Storage key for the local mixed proxy port.</summary>
    private const string KeyMixedPort = "MixedPort";

    /// <summary>Storage key for background connection sampling.</summary>
    private const string KeyConnectionSamplingEnabled = "ConnectionSamplingEnabled";

    /// <summary>Storage key for background connection sampling interval in seconds.</summary>
    private const string KeyConnectionSamplingIntervalSeconds = "ConnectionSamplingIntervalSeconds";

    /// <summary>Storage key for restoring Windows proxy state when Clash# exits normally.</summary>
    private const string KeyRestoreProxyOnExit = "RestoreProxyOnExit";

    /// <summary>Storage key for detecting stale Windows proxy state during application startup.</summary>
    private const string KeyCheckStaleProxyOnStartup = "CheckStaleProxyOnStartup";

    /// <summary>Storage key for startup conflict detection.</summary>
    private const string KeyStartupConflictCheckEnabled = "StartupConflictCheckEnabled";

    /// <summary>Storage key for startup proxy behavior.</summary>
    private const string KeyStartupBehaviorMode = "StartupBehaviorMode";

    /// <summary>Storage key for showing the startup guide during application startup.</summary>
    private const string KeyShowStartupGuideOnStartup = "ShowStartupGuideOnStartup";

    /// <summary>Storage key for stale proxy recovery behavior after abnormal exits or restarts.</summary>
    private const string KeyProxyRecoveryMode = "ProxyRecoveryMode";

    /// <summary>Storage key for mainland China display text and flag replacement.</summary>
    private const string KeyMainlandChinaDisplayEnabled = "MainlandChinaDisplayEnabled";

    /// <summary>Storage key for mainland China feature mode.</summary>
    private const string KeyMainlandChinaFeatureMode = "MainlandChinaFeatureMode";

    /// <summary>Storage key for mainland China URL blocking.</summary>
    private const string KeyMainlandChinaUrlBlockingEnabled = "MainlandChinaUrlBlockingEnabled";

    /// <summary>Storage key for Windows system notification verbosity.</summary>
    private const string KeyNotificationLevel = "NotificationLevel";

    /// <summary>Storage key for proxy connection-test URL.</summary>
    private const string KeyConnectionTestUrl = "ConnectionTestUrl";

    private const string KeyConnectionTestProxyUrl1 = "ConnectionTestProxyUrl1";
    private const string KeyConnectionTestProxyUrl2 = "ConnectionTestProxyUrl2";
    private const string KeyConnectionTestDirectUrl = "ConnectionTestDirectUrl";

    /// <summary>Default proxy connection-test URL.</summary>
    private const string DefaultConnectionTestUrl = "https://www.google.com/generate_204";
    private const string DefaultConnectionTestProxyUrl1 = "https://www.google.com";
    private const string DefaultConnectionTestProxyUrl2 = "https://github.com";
    private const string DefaultConnectionTestDirectUrl = "https://www.baidu.com";

    /// <summary>Default custom accent color used as the picker seed.</summary>
    private const string DefaultAppAccentColorValue = "#FF0078D4";

    /// <summary>Settings keys owned by this service.</summary>
    private static readonly string[] KnownKeys =
    [
        KeyDisplayLanguage,
        KeyAppThemeMode,
        KeyAppAccentColorMode,
        KeyAppAccentColorValue,
        KeyLaunchAtStartupEnabled,
        KeyCurrentMode,
        KeyActiveProfileId,
        KeyTransparentProxyEnabled,
        KeyMixedPort,
        KeyConnectionSamplingEnabled,
        KeyConnectionSamplingIntervalSeconds,
        KeyRestoreProxyOnExit,
        KeyCheckStaleProxyOnStartup,
        KeyStartupConflictCheckEnabled,
        KeyStartupBehaviorMode,
        KeyShowStartupGuideOnStartup,
        KeyProxyRecoveryMode,
        KeyMainlandChinaDisplayEnabled,
        KeyMainlandChinaFeatureMode,
        KeyMainlandChinaUrlBlockingEnabled,
        KeyNotificationLevel,
        KeyConnectionTestUrl,
        KeyConnectionTestProxyUrl1,
        KeyConnectionTestProxyUrl2,
        KeyConnectionTestDirectUrl,
    ];

    /// <summary>Initializes the settings service and resolves the preferred storage container.</summary>
    private AppSettingsService()
    {
        _localSettings = TryResolveLocalSettings();
    }

    /// <summary>Gets or sets the user-selected display language.</summary>
    /// <value>Selected <see cref="AppLanguage"/> value; defaults to <see cref="AppLanguage.AutoDetect"/>.</value>
    public AppLanguage DisplayLanguage
    {
        get => GetEnum(KeyDisplayLanguage, AppLanguage.AutoDetect);
        set => SetEnum(KeyDisplayLanguage, value);
    }

    /// <summary>Gets or sets the selected application display style.</summary>
    /// <value>Selected <see cref="AppThemeMode"/> value; defaults to <see cref="AppThemeMode.FollowSystem"/>.</value>
    public AppThemeMode AppThemeMode
    {
        get => GetEnum(KeyAppThemeMode, AppThemeMode.FollowSystem);
        set => SetEnum(KeyAppThemeMode, value);
    }

    /// <summary>Gets or sets the selected application accent color behavior.</summary>
    /// <value>Selected <see cref="AppAccentColorMode"/> value; defaults to <see cref="AppAccentColorMode.FollowSystem"/>.</value>
    public AppAccentColorMode AppAccentColorMode
    {
        get => GetEnum(KeyAppAccentColorMode, AppAccentColorMode.FollowSystem);
        set => SetEnum(KeyAppAccentColorMode, value);
    }

    /// <summary>Gets or sets the custom application accent color in ARGB hex format.</summary>
    /// <value>Custom color as #AARRGGBB; defaults to Windows blue.</value>
    /// <exception cref="ArgumentException">Assigned value is not a 6- or 8-digit hex color.</exception>
    public string AppAccentColorValue
    {
        get => GetString(KeyAppAccentColorValue, DefaultAppAccentColorValue);
        set => SetString(KeyAppAccentColorValue, NormalizeAccentColorValue(value));
    }

    /// <summary>Gets or sets whether Clash# should launch when the user signs in.</summary>
    /// <value>True when launch-at-startup is requested; defaults to false.</value>
    public bool LaunchAtStartupEnabled
    {
        get => GetBoolean(KeyLaunchAtStartupEnabled, false);
        set => SetBoolean(KeyLaunchAtStartupEnabled, value);
    }

    /// <summary>Gets or sets the currently selected master takeover mode.</summary>
    /// <value>Selected <see cref="ClashSharpMode"/> value; defaults to <see cref="ClashSharpMode.Disabled"/>.</value>
    public ClashSharpMode CurrentMode
    {
        get => GetEnum(KeyCurrentMode, ClashSharpMode.Disabled);
        set => SetEnum(KeyCurrentMode, value);
    }

    /// <summary>Gets or sets the active configuration profile identifier.</summary>
    /// <value>Stable profile identifier; defaults to the built-in direct profile.</value>
    public string ActiveProfileId
    {
        get => GetString(KeyActiveProfileId, ProfileCatalogIds.BuiltInDirect);
        set => SetString(KeyActiveProfileId, value);
    }

    /// <summary>Gets or sets whether transparent proxy mode is enabled.</summary>
    /// <value>True when TUN-based transparent proxy should be used; defaults to true.</value>
    public bool TransparentProxyEnabled
    {
        get => GetBoolean(KeyTransparentProxyEnabled, true);
        set => SetBoolean(KeyTransparentProxyEnabled, value);
    }

    /// <summary>Gets or sets the local mixed HTTP and SOCKS proxy port.</summary>
    /// <value>TCP port in the inclusive range [1, 65535]; defaults to 10000.</value>
    /// <exception cref="ArgumentOutOfRangeException">Assigned value is outside the valid TCP port range.</exception>
    public int MixedPort
    {
        get => GetInt32(KeyMixedPort, 10000);
        set
        {
            if (value is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Port must be in the range [1, 65535].");
            }

            SetInt32(KeyMixedPort, value);
        }
    }

    /// <summary>Gets or sets whether active connections are periodically sampled into SQLite.</summary>
    /// <value>True when background connection sampling is enabled; defaults to true.</value>
    public bool ConnectionSamplingEnabled
    {
        get => GetBoolean(KeyConnectionSamplingEnabled, true);
        set => SetBoolean(KeyConnectionSamplingEnabled, value);
    }

    /// <summary>Gets or sets the background connection sampling interval in seconds.</summary>
    /// <value>Interval in the inclusive range [3, 300]; defaults to 30.</value>
    /// <exception cref="ArgumentOutOfRangeException">Assigned value is outside the valid interval range.</exception>
    public int ConnectionSamplingIntervalSeconds
    {
        get => GetInt32(KeyConnectionSamplingIntervalSeconds, 30);
        set
        {
            if (value is < 3 or > 300)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Sampling interval must be in the range [3, 300] seconds.");
            }

            SetInt32(KeyConnectionSamplingIntervalSeconds, value);
        }
    }

    /// <summary>Gets or sets whether Clash# restores Windows proxy state during normal exit.</summary>
    /// <value>True when proxy state is restored during normal exit; defaults to true.</value>
    public bool RestoreProxyOnExit
    {
        get => GetBoolean(KeyRestoreProxyOnExit, true);
        set => SetBoolean(KeyRestoreProxyOnExit, value);
    }

    /// <summary>Gets or sets whether Clash# checks stale Windows proxy state on startup.</summary>
    /// <value>True when startup stale-proxy checks are enabled; defaults to true.</value>
    public bool CheckStaleProxyOnStartup
    {
        get => GetBoolean(KeyCheckStaleProxyOnStartup, true);
        set => SetBoolean(KeyCheckStaleProxyOnStartup, value);
    }

    /// <summary>Gets or sets whether Clash# checks for startup conflicts before applying proxy mode.</summary>
    /// <value>True when startup conflict detection is enabled; defaults to true.</value>
    public bool StartupConflictCheckEnabled
    {
        get => GetBoolean(KeyStartupConflictCheckEnabled, true);
        set => SetBoolean(KeyStartupConflictCheckEnabled, value);
    }

    /// <summary>Gets or sets the proxy behavior applied when Clash# starts.</summary>
    /// <value>Selected <see cref="StartupBehaviorMode"/> value; defaults to <see cref="StartupBehaviorMode.LastSetting"/>.</value>
    public StartupBehaviorMode StartupBehaviorMode
    {
        get => GetEnum(KeyStartupBehaviorMode, StartupBehaviorMode.LastSetting);
        set => SetEnum(KeyStartupBehaviorMode, value);
    }

    /// <summary>Gets or sets whether Clash# should show the startup guide during application startup.</summary>
    /// <value>True when the startup guide should be shown; defaults to true.</value>
    public bool ShowStartupGuideOnStartup
    {
        get => GetBoolean(KeyShowStartupGuideOnStartup, true);
        set => SetBoolean(KeyShowStartupGuideOnStartup, value);
    }

    /// <summary>Gets or sets the recovery action applied to stale proxy state after abnormal exits.</summary>
    /// <value>Selected <see cref="ProxyRecoveryMode"/> value; defaults to <see cref="ProxyRecoveryMode.DisableProxy"/>.</value>
    public ProxyRecoveryMode ProxyRecoveryMode
    {
        get => GetEnum(KeyProxyRecoveryMode, ProxyRecoveryMode.DisableProxy);
        set => SetEnum(KeyProxyRecoveryMode, value);
    }

    /// <summary>Gets or sets the mainland China specific display feature level.</summary>
    /// <value>Selected feature level; defaults to flag replacement and text completion.</value>
    public MainlandChinaFeatureMode MainlandChinaFeatureMode
    {
        get
        {
            MainlandChinaFeatureMode defaultMode = GetBoolean(KeyMainlandChinaDisplayEnabled, true)
                ? MainlandChinaFeatureMode.FlagReplacementAndTextCompletion
                : MainlandChinaFeatureMode.Disabled;
            MainlandChinaFeatureMode mode = GetEnum(KeyMainlandChinaFeatureMode, defaultMode);
            return mode == MainlandChinaFeatureMode.AllIncludingUrlBlacklist
                ? MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter
                : mode;
        }

        set
        {
            MainlandChinaFeatureMode persistedMode = value == MainlandChinaFeatureMode.AllIncludingUrlBlacklist
                ? MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter
                : value;
            SetEnum(KeyMainlandChinaFeatureMode, persistedMode);
            SetBoolean(KeyMainlandChinaDisplayEnabled, persistedMode != MainlandChinaFeatureMode.Disabled);
        }
    }

    /// <summary>Gets or sets whether mainland China URL blocking is enabled.</summary>
    /// <value>True when CCP-unfriendly URLs are masked in UI text; defaults to false.</value>
    public bool MainlandChinaUrlBlockingEnabled
    {
        get
        {
            lock (_syncLock)
            {
                if (GetValue(KeyMainlandChinaFeatureMode) is int value
                    && value == (int)MainlandChinaFeatureMode.AllIncludingUrlBlacklist)
                {
                    return true;
                }
            }

            return GetBoolean(KeyMainlandChinaUrlBlockingEnabled, false);
        }

        set => SetBoolean(KeyMainlandChinaUrlBlockingEnabled, value);
    }

    /// <summary>Gets or sets the URL used for proxy connection tests.</summary>
    /// <value>Absolute HTTP/HTTPS URL; defaults to https://www.google.com/generate_204.</value>
    public string ConnectionTestUrl
    {
        get => GetString(KeyConnectionTestUrl, DefaultConnectionTestUrl);
        set => SetString(KeyConnectionTestUrl, NormalizeConnectionTestUrl(value));
    }

    public string ConnectionTestProxyUrl1
    {
        get => GetString(KeyConnectionTestProxyUrl1, DefaultConnectionTestProxyUrl1);
        set => SetString(KeyConnectionTestProxyUrl1, NormalizeConnectionTestUrl(value));
    }

    public string ConnectionTestProxyUrl2
    {
        get => GetString(KeyConnectionTestProxyUrl2, DefaultConnectionTestProxyUrl2);
        set => SetString(KeyConnectionTestProxyUrl2, NormalizeConnectionTestUrl(value));
    }

    public string ConnectionTestDirectUrl
    {
        get => GetString(KeyConnectionTestDirectUrl, DefaultConnectionTestDirectUrl);
        set => SetString(KeyConnectionTestDirectUrl, NormalizeConnectionTestUrl(value));
    }

    /// <summary>Gets or sets whether mainland China display replacement is enabled.</summary>
    /// <value>True when any mainland China feature mode is enabled; defaults to true.</value>
    public bool MainlandChinaDisplayEnabled
    {
        get => MainlandChinaFeatureMode != MainlandChinaFeatureMode.Disabled;
        set => MainlandChinaFeatureMode = value
            ? MainlandChinaFeatureMode.FlagReplacementAndTextCompletion
            : MainlandChinaFeatureMode.Disabled;
    }

    /// <summary>Gets or sets the Windows system notification verbosity.</summary>
    /// <value>Notification level; defaults to <see cref="NotificationLevel.Default"/>.</value>
    public NotificationLevel NotificationLevel
    {
        get => GetEnum(KeyNotificationLevel, NotificationLevel.Default);
        set => SetEnum(KeyNotificationLevel, value);
    }

    /// <summary>Removes all persisted settings owned by Clash#, restoring default values on subsequent reads.</summary>
    public void ResetAllSettings()
    {
        lock (_syncLock)
        {
            foreach (string key in KnownKeys)
            {
                RemoveValue(key);
            }
        }
    }

    /// <summary>Reads a boolean setting from storage or returns <paramref name="defaultValue"/>.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="defaultValue">Default value used when no valid stored value exists.</param>
    /// <returns>The stored boolean value or <paramref name="defaultValue"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private bool GetBoolean(string key, bool defaultValue)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_syncLock)
        {
            return GetValue(key) is bool value ? value : defaultValue;
        }
    }

    /// <summary>Writes a boolean setting to storage.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="value">Boolean value to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private void SetBoolean(string key, bool value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_syncLock)
        {
            SetValue(key, value);
        }
    }

    /// <summary>Reads a 32-bit integer setting from storage or returns <paramref name="defaultValue"/>.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="defaultValue">Default value used when no valid stored value exists.</param>
    /// <returns>The stored integer value or <paramref name="defaultValue"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private int GetInt32(string key, int defaultValue)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_syncLock)
        {
            return GetValue(key) is int value ? value : defaultValue;
        }
    }

    /// <summary>Writes a 32-bit integer setting to storage.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="value">Integer value to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private void SetInt32(string key, int value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_syncLock)
        {
            SetValue(key, value);
        }
    }

    /// <summary>Reads a string setting from storage or returns <paramref name="defaultValue"/>.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="defaultValue">Default value used when no valid stored value exists. Must not be null.</param>
    /// <returns>The stored string value or <paramref name="defaultValue"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="defaultValue"/> is null.</exception>
    private string GetString(string key, string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(defaultValue);

        lock (_syncLock)
        {
            return GetValue(key) is string value ? value : defaultValue;
        }
    }

    /// <summary>Writes a string setting to storage.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="value">String value to persist. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is null.</exception>
    private void SetString(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_syncLock)
        {
            SetValue(key, value);
        }
    }

    /// <summary>Reads an enum setting from storage or returns <paramref name="defaultValue"/>.</summary>
    /// <typeparam name="TEnum">Enum type represented by the stored setting.</typeparam>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="defaultValue">Default enum value used when no valid stored value exists.</param>
    /// <returns>The stored enum value or <paramref name="defaultValue"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private TEnum GetEnum<TEnum>(string key, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_syncLock)
        {
            object? rawValue = GetValue(key);
            if (rawValue is int intValue && Enum.IsDefined(typeof(TEnum), intValue))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), intValue);
            }

            return defaultValue;
        }
    }

    /// <summary>Writes an enum setting to storage as its integer representation.</summary>
    /// <typeparam name="TEnum">Enum type represented by the stored setting.</typeparam>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="value">Enum value to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private void SetEnum<TEnum>(string key, TEnum value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_syncLock)
        {
            SetValue(key, Convert.ToInt32(value));
        }
    }

    /// <summary>Reads a raw setting value from the preferred backing store.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <returns>The stored value when present; otherwise null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private object? GetValue(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_localSettings is not null && _localSettings.Values.TryGetValue(key, out object? localValue))
        {
            return localValue;
        }

        return _fallbackValues.TryGetValue(key, out object? fallbackValue) ? fallbackValue : null;
    }

    /// <summary>Writes a raw setting value to the preferred backing store.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    /// <param name="value">Value to persist. Must be supported by Windows local settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    private void SetValue(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_localSettings is not null)
        {
            _localSettings.Values[key] = value;
            return;
        }

        _fallbackValues[key] = value;
    }

    /// <summary>Removes a raw setting value from the preferred backing store.</summary>
    /// <param name="key">Storage key. Must not be null.</param>
    private void RemoveValue(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_localSettings is not null)
        {
            _localSettings.Values.Remove(key);
        }

        _fallbackValues.Remove(key);
    }

    /// <summary>Normalizes a user-entered HTTP/HTTPS connection test URL.</summary>
    /// <param name="value">User-entered URL. Must not be null.</param>
    /// <returns>Normalized absolute URL.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not an HTTP or HTTPS URL.</exception>
    private static string NormalizeConnectionTestUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string trimmedValue = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            return DefaultConnectionTestUrl;
        }

        if (!trimmedValue.Contains("://", StringComparison.Ordinal))
        {
            trimmedValue = $"https://{trimmedValue}";
        }

        if (!Uri.TryCreate(trimmedValue, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Connection test URL must be an absolute HTTP or HTTPS URL.", nameof(value));
        }

        return uri.ToString().TrimEnd('/');
    }

    /// <summary>Normalizes a user-selected accent color to #AARRGGBB.</summary>
    /// <param name="value">Hex color value. Must not be null.</param>
    /// <returns>Normalized ARGB hex color.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a 6- or 8-digit hex color.</exception>
    private static string NormalizeAccentColorValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string normalized = value.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 6)
        {
            normalized = $"FF{normalized}";
        }

        if (normalized.Length != 8 || !IsHexColorValue(normalized))
        {
            throw new ArgumentException("Accent color must be a 6- or 8-digit hexadecimal value.", nameof(value));
        }

        return $"#{normalized.ToUpperInvariant()}";
    }

    /// <summary>Returns whether <paramref name="value"/> contains only ASCII hexadecimal digits.</summary>
    /// <param name="value">Color text without leading #. Must not be null.</param>
    /// <returns>True when every character is hexadecimal.</returns>
    private static bool IsHexColorValue(string value)
    {
        foreach (char character in value)
        {
            bool isHexDigit = character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHexDigit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Attempts to resolve the Windows application local settings container.</summary>
    /// <returns>The local settings container when available; otherwise null.</returns>
    private static ApplicationDataContainer? TryResolveLocalSettings()
    {
        try
        {
            return ApplicationData.Current.LocalSettings;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
