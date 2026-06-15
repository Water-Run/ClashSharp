/*
 * Application Settings Service
 * Provides persistent access to user-facing Clash# behavior switches and recovery policies
 *
 * @author: WaterRun
 * @file: Service/AppSettingsService.cs
 * @date: 2026-06-15
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

    /// <summary>Storage key for the currently selected master takeover mode.</summary>
    private const string KeyCurrentMode = "CurrentMode";

    /// <summary>Storage key for the transparent proxy switch.</summary>
    private const string KeyTransparentProxyEnabled = "TransparentProxyEnabled";

    /// <summary>Storage key for the local mixed proxy port.</summary>
    private const string KeyMixedPort = "MixedPort";

    /// <summary>Storage key for the automatic fallback from TUN to system proxy.</summary>
    private const string KeyFallbackToSystemProxyWhenTunFails = "FallbackToSystemProxyWhenTunFails";

    /// <summary>Storage key for restoring Windows proxy state when Clash# exits normally.</summary>
    private const string KeyRestoreProxyOnExit = "RestoreProxyOnExit";

    /// <summary>Storage key for detecting stale Windows proxy state during application startup.</summary>
    private const string KeyCheckStaleProxyOnStartup = "CheckStaleProxyOnStartup";

    /// <summary>Storage key for stale proxy recovery behavior after abnormal exits or restarts.</summary>
    private const string KeyProxyRecoveryMode = "ProxyRecoveryMode";

    /// <summary>Storage key for mainland China display text and flag replacement.</summary>
    private const string KeyMainlandChinaDisplayEnabled = "MainlandChinaDisplayEnabled";

    /// <summary>Initializes the settings service and resolves the preferred storage container.</summary>
    private AppSettingsService()
    {
        _localSettings = TryResolveLocalSettings();
    }

    /// <summary>Gets or sets the user-selected display language.</summary>
    /// <value>Selected <see cref="AppLanguage"/> value; defaults to <see cref="AppLanguage.SimplifiedChinese"/>.</value>
    public AppLanguage DisplayLanguage
    {
        get => GetEnum(KeyDisplayLanguage, AppLanguage.SimplifiedChinese);
        set => SetEnum(KeyDisplayLanguage, value);
    }

    /// <summary>Gets or sets the currently selected master takeover mode.</summary>
    /// <value>Selected <see cref="ClashSharpMode"/> value; defaults to <see cref="ClashSharpMode.Disabled"/>.</value>
    public ClashSharpMode CurrentMode
    {
        get => GetEnum(KeyCurrentMode, ClashSharpMode.Disabled);
        set => SetEnum(KeyCurrentMode, value);
    }

    /// <summary>Gets or sets whether transparent proxy mode is enabled.</summary>
    /// <value>True when TUN-based transparent proxy should be used; defaults to false.</value>
    public bool TransparentProxyEnabled
    {
        get => GetBoolean(KeyTransparentProxyEnabled, false);
        set => SetBoolean(KeyTransparentProxyEnabled, value);
    }

    /// <summary>Gets or sets the local mixed HTTP and SOCKS proxy port.</summary>
    /// <value>TCP port in the inclusive range [1, 65535]; defaults to 7890.</value>
    /// <exception cref="ArgumentOutOfRangeException">Assigned value is outside the valid TCP port range.</exception>
    public int MixedPort
    {
        get => GetInt32(KeyMixedPort, 7890);
        set
        {
            if (value is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Port must be in the range [1, 65535].");
            }

            SetInt32(KeyMixedPort, value);
        }
    }

    /// <summary>Gets or sets whether TUN startup failures automatically fall back to system proxy mode.</summary>
    /// <value>True when fallback is enabled; defaults to true.</value>
    public bool FallbackToSystemProxyWhenTunFails
    {
        get => GetBoolean(KeyFallbackToSystemProxyWhenTunFails, true);
        set => SetBoolean(KeyFallbackToSystemProxyWhenTunFails, value);
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

    /// <summary>Gets or sets the recovery action applied to stale proxy state after abnormal exits.</summary>
    /// <value>Selected <see cref="ProxyRecoveryMode"/> value; defaults to <see cref="ProxyRecoveryMode.DisableProxy"/>.</value>
    public ProxyRecoveryMode ProxyRecoveryMode
    {
        get => GetEnum(KeyProxyRecoveryMode, ProxyRecoveryMode.DisableProxy);
        set => SetEnum(KeyProxyRecoveryMode, value);
    }

    /// <summary>Gets or sets whether mainland China display replacement is enabled.</summary>
    /// <value>True when regional text and flag display replacement is enabled; defaults to true.</value>
    public bool MainlandChinaDisplayEnabled
    {
        get => GetBoolean(KeyMainlandChinaDisplayEnabled, true);
        set => SetBoolean(KeyMainlandChinaDisplayEnabled, value);
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
