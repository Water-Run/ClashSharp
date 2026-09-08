using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Supplies admitted runtime participants and their single durable settings authority.</summary>
public interface ISettingsRuntimeParticipants
{
    /// <summary>Reads the authoritative participant-facing settings without modifying storage.</summary>
    SettingsRuntimeSnapshot CaptureSnapshot();

    /// <summary>Applies the selected display language.</summary>
    void ApplyLanguage(AppLanguage language);

    /// <summary>Applies the selected application theme.</summary>
    void ApplyTheme(AppThemeMode theme);

    /// <summary>Applies the selected accent policy and value together.</summary>
    void ApplyAccentColor(AppAccentColorMode mode, string value);

    /// <summary>Applies sign-in registration without reacquiring ordinary admission.</summary>
    Task ApplyLaunchAtStartupAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Restarts sampling from the authoritative settings.</summary>
    Task RestartConnectionSamplingAsync(CancellationToken cancellationToken);

    /// <summary>Applies and verifies network settings inside the admitted transaction.</summary>
    Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken);
}
