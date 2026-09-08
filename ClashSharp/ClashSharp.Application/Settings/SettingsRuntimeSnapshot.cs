using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Captures the durable settings consumed by immediate runtime participants.</summary>
/// <param name="DisplayLanguage">Requested display language.</param>
/// <param name="AppThemeMode">Requested application theme.</param>
/// <param name="AppAccentColorMode">Requested accent policy.</param>
/// <param name="AppAccentColorValue">Normalized custom accent value.</param>
/// <param name="LaunchAtStartupEnabled">Requested sign-in registration.</param>
/// <param name="ConnectionSamplingEnabled">Requested connection sampling state.</param>
/// <param name="ConnectionSamplingIntervalSeconds">Requested sampling interval.</param>
/// <param name="CurrentMode">Durable network takeover mode.</param>
/// <param name="ActiveProfileId">Durable active profile identity.</param>
/// <param name="TransparentProxyEnabled">Requested transparent proxy state.</param>
/// <param name="MixedPort">Requested mixed proxy port.</param>
public readonly record struct SettingsRuntimeSnapshot(
    AppLanguage DisplayLanguage,
    AppThemeMode AppThemeMode,
    AppAccentColorMode AppAccentColorMode,
    string AppAccentColorValue,
    bool LaunchAtStartupEnabled,
    bool ConnectionSamplingEnabled,
    int ConnectionSamplingIntervalSeconds,
    ClashSharpMode CurrentMode,
    string ActiveProfileId,
    bool TransparentProxyEnabled,
    int MixedPort);
