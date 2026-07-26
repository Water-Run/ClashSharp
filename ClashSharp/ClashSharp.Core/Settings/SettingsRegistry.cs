using System.Collections.ObjectModel;
using ClashSharp.Model;

namespace ClashSharp.Settings;

/// <summary>
/// Owns the single canonical collection of setting keys, defaults, validation, reset, package, and application metadata.
/// </summary>
public sealed partial class SettingsRegistry
{
    private const SettingsResetScope GroupResetScopes =
        SettingsResetScope.Basic
        | SettingsResetScope.Notifications
        | SettingsResetScope.Startup
        | SettingsResetScope.Triggers
        | SettingsResetScope.Tray
        | SettingsResetScope.TransparentProxy
        | SettingsResetScope.Proxy
        | SettingsResetScope.ConnectionTests
        | SettingsResetScope.WindowsNative
        | SettingsResetScope.MainlandChina
        | SettingsResetScope.MasterControl;

    private const string DefaultAccentColor = "#FF0078D4";
    private const string DefaultProfileId = "builtin-direct";
    private const string DefaultConnectionTestUrl = "https://www.google.com/generate_204";
    private const string DefaultConnectionTestProxyUrl1 = "https://www.google.com";
    private const string DefaultConnectionTestProxyUrl2 = "https://github.com";
    private const string DefaultConnectionTestDirectUrl = "https://www.baidu.com";

    private static readonly string[] TrayFeatureIds =
    [
        "status",
        "mode",
        "pages",
        "transparent-proxy",
        "settings",
        "safe-exit",
    ];

    private static readonly string[] DefaultHeroStatusItems =
    [
        "CoreStatus",
        "SystemProxy",
        "TransparentProxy",
        "CurrentNode",
        "UploadRate",
        "DownloadRate",
        "TotalTraffic",
        "Availability",
    ];

    private static readonly string[] HeroStatusItems =
    [
        "CoreStatus",
        "SystemProxy",
        "TransparentProxy",
        "CurrentNode",
        "Latency",
        "UploadRate",
        "DownloadRate",
        "TotalTraffic",
        "ActiveConnections",
        "CurrentMode",
        "ActiveProfile",
        "MihomoService",
        "StartupLaunch",
        "Availability",
    ];

    private readonly IReadOnlyDictionary<string, RegistryEntry> _entries;

    private SettingsRegistry(IEnumerable<SettingDefinition> definitions)
    {
        SettingDefinition[] snapshot = definitions.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("A settings registry must contain at least one definition.", nameof(definitions));
        }

        if (snapshot.Any(static definition => definition is null))
        {
            throw new ArgumentException("Settings definitions cannot contain null.", nameof(definitions));
        }

        Dictionary<string, RegistryEntry> entries = new(StringComparer.Ordinal);
        foreach (SettingDefinition definition in snapshot)
        {
            if (!entries.TryAdd(
                    definition.Key.Value,
                    new RegistryEntry(definition, SettingKeyResolution.Canonical)))
            {
                throw new ArgumentException(
                    $"Duplicate canonical setting key '{definition.Key.Value}'.",
                    nameof(definitions));
            }
        }

        foreach (SettingDefinition definition in snapshot)
        {
            foreach (SettingKey alias in definition.Aliases)
            {
                if (!entries.TryAdd(alias.Value, new RegistryEntry(definition, SettingKeyResolution.Alias)))
                {
                    throw new ArgumentException(
                        $"Setting alias '{alias.Value}' collides with another canonical key or alias.",
                        nameof(definitions));
                }
            }
        }

        Definitions = Array.AsReadOnly(snapshot);
        _entries = new ReadOnlyDictionary<string, RegistryEntry>(entries);
    }

    /// <summary>Gets the process-wide immutable canonical registry.</summary>
    public static SettingsRegistry Default { get; } = Create(CreateDefaultDefinitions());

    /// <summary>Gets all canonical definitions in stable schema order.</summary>
    public IReadOnlyList<SettingDefinition> Definitions { get; }

    /// <summary>Creates an immutable registry and rejects canonical-key or alias collisions.</summary>
    /// <param name="definitions">Canonical setting definitions.</param>
    /// <returns>A validated immutable registry.</returns>
    public static SettingsRegistry Create(IEnumerable<SettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new SettingsRegistry(definitions);
    }

    /// <summary>Gets a canonical definition by its canonical key.</summary>
    /// <param name="key">Case-sensitive persisted key.</param>
    /// <returns>The resolved canonical definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="key"/> is not registered.</exception>
    public SettingDefinition Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _entries.TryGetValue(key, out RegistryEntry? entry)
            && entry.Resolution == SettingKeyResolution.Canonical
            ? entry.Definition
            : throw new KeyNotFoundException($"Setting key '{key}' is not registered.");
    }

    /// <summary>Attempts to resolve a canonical key or read-only legacy alias without throwing for external input.</summary>
    /// <param name="key">Case-sensitive persisted key.</param>
    /// <param name="definition">Resolved canonical definition, or null.</param>
    /// <param name="resolution">Whether the key was canonical, an alias, or unresolved.</param>
    /// <returns>True when the key resolved.</returns>
    public bool TryResolve(
        string? key,
        out SettingDefinition? definition,
        out SettingKeyResolution resolution)
    {
        if (key is not null && _entries.TryGetValue(key, out RegistryEntry? entry))
        {
            definition = entry.Definition;
            resolution = entry.Resolution;
            return true;
        }

        definition = null;
        resolution = SettingKeyResolution.None;
        return false;
    }

    /// <summary>Returns canonical definitions included in a reset scope in stable schema order.</summary>
    /// <param name="scope">Requested registry-derived reset scope.</param>
    /// <returns>A read-only definition snapshot.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scope"/> is none or undefined.</exception>
    public IReadOnlyList<SettingDefinition> GetResetDefinitions(SettingsResetScope scope)
    {
        if (scope == SettingsResetScope.None
            || (scope & ~(GroupResetScopes | SettingsResetScope.All)) != SettingsResetScope.None
            || scope != SettingsResetScope.All
            && (scope & SettingsResetScope.All) != SettingsResetScope.None)
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        SettingDefinition[] matches = Definitions
            .Where(definition => definition.IsInResetScope(scope))
            .ToArray();
        return Array.AsReadOnly(matches);
    }

    private static IEnumerable<SettingDefinition> CreateDefaultDefinitions()
    {
        yield return SettingDefinition.CreateEnum(
            Keys.DisplayLanguage,
            AppLanguage.AutoDetect,
            AppLanguage.AutoDetect,
            Metadata(SettingCategory.Appearance, SettingsResetScope.Basic, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.Basic"));
        yield return SettingDefinition.CreateEnum(
            Keys.AppThemeMode,
            AppThemeMode.FollowSystem,
            AppThemeMode.FollowSystem,
            Metadata(SettingCategory.Appearance, SettingsResetScope.Basic, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.Basic"));
        yield return SettingDefinition.CreateEnum(
            Keys.AppAccentColorMode,
            AppAccentColorMode.FollowSystem,
            AppAccentColorMode.FollowSystem,
            Metadata(SettingCategory.Appearance, SettingsResetScope.Basic, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.Basic"));
        yield return SettingDefinition.CreateString(
            Keys.AppAccentColorValue,
            DefaultAccentColor,
            DefaultAccentColor,
            NormalizeAccentColor,
            Metadata(SettingCategory.Appearance, SettingsResetScope.Basic, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.Basic"));
        yield return SettingDefinition.CreateBoolean(
            Keys.LaunchAtStartupEnabled,
            defaultValue: false,
            safeFallback: false,
            Metadata(SettingCategory.Startup, SettingsResetScope.Startup, SettingAuthority.ExternallyObserved, SettingApplicationKind.StartupTask, "Settings.Startup"));
        yield return SettingDefinition.CreateEnum(
            Keys.CurrentMode,
            ClashSharpMode.Disabled,
            ClashSharpMode.Disabled,
            Metadata(SettingCategory.Network, SettingsResetScope.None, SettingAuthority.ExternallyObserved, SettingApplicationKind.Network, "Settings.Proxy"),
            allowedValues:
            [
                ClashSharpMode.Disabled,
                ClashSharpMode.Standby,
                ClashSharpMode.RuleTakeover,
                ClashSharpMode.FullTakeover,
            ]);
        yield return SettingDefinition.CreateString(
            Keys.ActiveProfileId,
            DefaultProfileId,
            DefaultProfileId,
            NormalizeProfileId,
            Metadata(SettingCategory.Network, SettingsResetScope.None, SettingAuthority.ExternallyObserved, SettingApplicationKind.Network, "Settings.Proxy"));
        yield return SettingDefinition.CreateBoolean(
            Keys.TransparentProxyEnabled,
            defaultValue: true,
            safeFallback: false,
            Metadata(
                SettingCategory.Network,
                SettingsResetScope.TransparentProxy | SettingsResetScope.Proxy,
                SettingAuthority.ExternallyObserved,
                SettingApplicationKind.Network,
                "Settings.Proxy"));
        yield return SettingDefinition.CreateInteger(
            Keys.MixedPort,
            defaultValue: 10000,
            safeFallback: 10000,
            minimum: 1,
            maximum: 65535,
            Metadata(SettingCategory.Network, SettingsResetScope.Proxy, SettingAuthority.ExternallyObserved, SettingApplicationKind.Network, "Settings.Proxy"));
        yield return SettingDefinition.CreateBoolean(
            Keys.ConnectionSamplingEnabled,
            defaultValue: true,
            safeFallback: false,
            Metadata(SettingCategory.Sampling, SettingsResetScope.Proxy, SettingAuthority.ExternallyObserved, SettingApplicationKind.Sampling, "Settings.Proxy"));
        yield return SettingDefinition.CreateInteger(
            Keys.ConnectionSamplingIntervalSeconds,
            defaultValue: 30,
            safeFallback: 30,
            minimum: 3,
            maximum: 300,
            Metadata(SettingCategory.Sampling, SettingsResetScope.Proxy, SettingAuthority.ExternallyObserved, SettingApplicationKind.Sampling, "Settings.Proxy"));
        yield return SettingDefinition.CreateBoolean(
            Keys.RestoreProxyOnExit,
            defaultValue: true,
            safeFallback: true,
            Metadata(SettingCategory.Network, SettingsResetScope.WindowsNative, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.WindowsNative"));
        yield return SettingDefinition.CreateBoolean(
            Keys.CheckStaleProxyOnStartup,
            defaultValue: true,
            safeFallback: true,
            Metadata(SettingCategory.Network, SettingsResetScope.WindowsNative, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.WindowsNative"));
        yield return SettingDefinition.CreateBoolean(
            Keys.StartupConflictCheckEnabled,
            defaultValue: true,
            safeFallback: true,
            Metadata(SettingCategory.Startup, SettingsResetScope.Startup, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.Startup"));
        yield return SettingDefinition.CreateEnum(
            Keys.StartupBehaviorMode,
            StartupBehaviorMode.LastSetting,
            StartupBehaviorMode.LastSetting,
            Metadata(SettingCategory.Startup, SettingsResetScope.Startup, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.Startup"));
        yield return SettingDefinition.CreateBoolean(
            Keys.ShowStartupGuideOnStartup,
            defaultValue: true,
            safeFallback: true,
            Metadata(SettingCategory.Startup, SettingsResetScope.Startup, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.Startup"));
        yield return SettingDefinition.CreateBoolean(
            Keys.TriggersEnabled,
            defaultValue: true,
            safeFallback: false,
            Metadata(SettingCategory.Triggers, SettingsResetScope.Triggers, SettingAuthority.ExternallyObserved, SettingApplicationKind.Triggers, "Settings.Triggers"));
        yield return SettingDefinition.CreateBoolean(
            Keys.TriggerNotificationsEnabled,
            defaultValue: true,
            safeFallback: true,
            Metadata(SettingCategory.Triggers, SettingsResetScope.Triggers, SettingAuthority.Internal, SettingApplicationKind.Triggers, "Settings.Triggers"));
        yield return SettingDefinition.CreateEnum(
            Keys.CloseBehaviorMode,
            CloseBehaviorMode.MinimizeToTray,
            CloseBehaviorMode.MinimizeToTray,
            Metadata(SettingCategory.General, SettingsResetScope.Basic, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.Basic"));
        yield return SettingDefinition.CreateBoolean(
            Keys.TrayUseMonochromeInactiveIcon,
            defaultValue: false,
            safeFallback: false,
            Metadata(SettingCategory.Tray, SettingsResetScope.Tray, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.Tray"));
        yield return SettingDefinition.CreateString(
            Keys.TrayVisibleFeatureIds,
            string.Join(",", TrayFeatureIds),
            string.Join(",", TrayFeatureIds),
            NormalizeTrayFeatureIds,
            Metadata(SettingCategory.Tray, SettingsResetScope.Tray, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.Tray"));
        yield return SettingDefinition.CreateEnum(
            Keys.MainlandChinaFeatureMode,
            MainlandChinaFeatureMode.FlagReplacementAndTextCompletion,
            MainlandChinaFeatureMode.FlagReplacementAndTextCompletion,
            Metadata(
                SettingCategory.Regional,
                SettingsResetScope.MainlandChina,
                SettingAuthority.Internal,
                SettingApplicationKind.Appearance,
                "Settings.MainlandChina",
                aliases: [Keys.MainlandChinaDisplayEnabled]),
            allowedValues:
            [
                MainlandChinaFeatureMode.Disabled,
                MainlandChinaFeatureMode.FlagReplacementOnly,
                MainlandChinaFeatureMode.FlagReplacementAndTextCompletion,
                MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter,
            ]);
        yield return SettingDefinition.CreateBoolean(
            Keys.MainlandChinaUrlBlockingEnabled,
            defaultValue: false,
            safeFallback: false,
            Metadata(SettingCategory.Regional, SettingsResetScope.MainlandChina, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.MainlandChina"));
        yield return SettingDefinition.CreateBoolean(
            Keys.NotificationEnabled,
            defaultValue: true,
            safeFallback: true,
            Metadata(SettingCategory.Notifications, SettingsResetScope.Notifications, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.Notifications"));
        yield return SettingDefinition.CreateEnum(
            Keys.NotificationLevel,
            NotificationLevel.Default,
            NotificationLevel.Default,
            Metadata(SettingCategory.Notifications, SettingsResetScope.Notifications, SettingAuthority.Internal, SettingApplicationKind.Internal, "Settings.Notifications"));
        yield return SettingDefinition.CreateString(
            Keys.ConnectionTestUrl,
            DefaultConnectionTestUrl,
            DefaultConnectionTestUrl,
            NormalizeConnectionTestUri,
            Metadata(
                SettingCategory.Network,
                SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
                SettingAuthority.Internal,
                SettingApplicationKind.Internal,
                "Settings.ConnectionTest",
                isSensitive: true));
        yield return SettingDefinition.CreateString(
            Keys.MasterHeroStatusLayout,
            string.Join(",", DefaultHeroStatusItems),
            string.Join(",", DefaultHeroStatusItems),
            NormalizeHeroStatusLayout,
            Metadata(SettingCategory.Appearance, SettingsResetScope.MasterControl, SettingAuthority.Internal, SettingApplicationKind.Appearance, "Settings.MasterControl"));
        yield return SettingDefinition.CreateString(
            Keys.ConnectionTestProxyUrl1,
            DefaultConnectionTestProxyUrl1,
            DefaultConnectionTestProxyUrl1,
            NormalizeConnectionTestUri,
            Metadata(
                SettingCategory.Network,
                SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
                SettingAuthority.Internal,
                SettingApplicationKind.Internal,
                "Settings.ConnectionTest",
                isSensitive: true));
        yield return SettingDefinition.CreateString(
            Keys.ConnectionTestProxyUrl2,
            DefaultConnectionTestProxyUrl2,
            DefaultConnectionTestProxyUrl2,
            NormalizeConnectionTestUri,
            Metadata(
                SettingCategory.Network,
                SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
                SettingAuthority.Internal,
                SettingApplicationKind.Internal,
                "Settings.ConnectionTest",
                isSensitive: true));
        yield return SettingDefinition.CreateString(
            Keys.ConnectionTestDirectUrl,
            DefaultConnectionTestDirectUrl,
            DefaultConnectionTestDirectUrl,
            NormalizeConnectionTestUri,
            Metadata(
                SettingCategory.Network,
                SettingsResetScope.Proxy | SettingsResetScope.ConnectionTests,
                SettingAuthority.Internal,
                SettingApplicationKind.Internal,
                "Settings.ConnectionTest",
                isSensitive: true));
    }

    private static SettingDefinitionMetadata Metadata(
        SettingCategory category,
        SettingsResetScope resetScopes,
        SettingAuthority authority,
        SettingApplicationKind applicationKind,
        string localizationCategory,
        bool isSensitive = false,
        IEnumerable<SettingKey>? aliases = null)
    {
        return new SettingDefinitionMetadata(
            schemaVersion: 1,
            category,
            resetScopes,
            includeInDataPackage: true,
            authority,
            applicationKind,
            applicationTiming: SettingApplicationTiming.Live,
            localizationCategory,
            isSensitive,
            aliases);
    }

    private static StringNormalizationOutcome NormalizeAccentColor(string input)
    {
        string normalized = input.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 6)
        {
            normalized = $"FF{normalized}";
        }

        if (normalized.Length != 8 || normalized.Any(static character => !IsAsciiHexDigit(character)))
        {
            return Invalid(SettingValueErrorKind.InvalidFormat, "color.invalid_format");
        }

        return StringNormalizationOutcome.Succeeded($"#{normalized.ToUpperInvariant()}");
    }

    private static StringNormalizationOutcome NormalizeProfileId(string input)
    {
        if (input.Length is 0 or > 128
            || !IsAsciiLetterOrDigit(input[0])
            || input.Contains("..", StringComparison.Ordinal)
            || input.Any(static character =>
                !IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            return Invalid(SettingValueErrorKind.UnsafeValue, "profile_id.unsafe");
        }

        return StringNormalizationOutcome.Succeeded(input);
    }

    private static StringNormalizationOutcome NormalizeTrayFeatureIds(string input)
    {
        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string candidate in input.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? canonical = TrayFeatureIds.FirstOrDefault(
                id => StringComparer.OrdinalIgnoreCase.Equals(id, candidate));
            if (canonical is null)
            {
                return Invalid(SettingValueErrorKind.UndefinedValue, "tray_feature.undefined");
            }

            if (seen.Add(canonical))
            {
                normalized.Add(canonical);
            }
        }

        return normalized.Count == 0
            ? Invalid(SettingValueErrorKind.InvalidFormat, "tray_feature.empty")
            : StringNormalizationOutcome.Succeeded(string.Join(",", normalized));
    }

    private static StringNormalizationOutcome NormalizeConnectionTestUri(string input)
    {
        string normalized = input.Trim();
        if (normalized.Length is 0 or > 2048)
        {
            return Invalid(SettingValueErrorKind.InvalidFormat, "url.invalid_format");
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"https://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host))
        {
            return Invalid(SettingValueErrorKind.InvalidFormat, "url.invalid_format");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid(SettingValueErrorKind.UnsafeValue, "url.credentials_forbidden");
        }

        string canonical = uri.AbsoluteUri.TrimEnd('/');
        return StringNormalizationOutcome.Succeeded(canonical);
    }

    private static StringNormalizationOutcome NormalizeHeroStatusLayout(string input)
    {
        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string candidate in input.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? canonical = HeroStatusItems.FirstOrDefault(
                item => StringComparer.OrdinalIgnoreCase.Equals(item, candidate));
            if (canonical is null)
            {
                return Invalid(SettingValueErrorKind.UndefinedValue, "hero_status.undefined");
            }

            if (seen.Add(canonical))
            {
                normalized.Add(canonical);
            }
        }

        if (normalized.Count > DefaultHeroStatusItems.Length)
        {
            return Invalid(SettingValueErrorKind.OutOfRange, "hero_status.too_many");
        }

        if (normalized.Count == DefaultHeroStatusItems.Length)
        {
            return StringNormalizationOutcome.Succeeded(string.Join(",", normalized));
        }

        foreach (string candidate in DefaultHeroStatusItems.Concat(HeroStatusItems))
        {
            if (seen.Add(candidate))
            {
                normalized.Add(candidate);
            }

            if (normalized.Count == DefaultHeroStatusItems.Length)
            {
                return StringNormalizationOutcome.Succeeded(string.Join(",", normalized));
            }
        }

        return Invalid(SettingValueErrorKind.InvalidFormat, "hero_status.incomplete");
    }

    private static StringNormalizationOutcome Invalid(SettingValueErrorKind kind, string suffix) =>
        StringNormalizationOutcome.Failed(SettingDefinition.Error(kind, suffix));

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z'
        || character is >= 'a' and <= 'z'
        || character is >= '0' and <= '9';

    private static bool IsAsciiHexDigit(char character) =>
        character is >= '0' and <= '9'
        || character is >= 'A' and <= 'F'
        || character is >= 'a' and <= 'f';

    private sealed record RegistryEntry(
        SettingDefinition Definition,
        SettingKeyResolution Resolution);
}
