using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Supplies already-admitted storage, runtime participants, and applied-state observation.</summary>
/// <remarks>The caller owns admission and serialization until the coordinator and receipt finish.</remarks>
public interface ISettingsResetOperation
{
    /// <summary>Reads the currently authoritative settings without changing their storage.</summary>
    SettingsRuntimeSnapshot CaptureSnapshot();

    /// <summary>Resets exactly one supported scope and transfers its retained receipt to the coordinator.</summary>
    IRetainedSettingsResetReceipt BeginReset(SettingsResetScope scope, bool transparentProxyEnabled);

    /// <summary>Restores participant-facing values for the legacy full-reset adapter.</summary>
    void RestoreDurableSnapshot(SettingsRuntimeSnapshot snapshot);

    /// <summary>Applies the selected display language.</summary>
    void ApplyLanguage(AppLanguage language);

    /// <summary>Applies the selected application theme.</summary>
    void ApplyTheme(AppThemeMode theme);

    /// <summary>Applies the selected accent policy and value together.</summary>
    void ApplyAccentColor(AppAccentColorMode mode, string value);

    /// <summary>Applies sign-in registration without reacquiring ordinary admission.</summary>
    Task ApplyLaunchAtStartupAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Restarts sampling from the current authoritative settings.</summary>
    Task RestartConnectionSamplingAsync(CancellationToken cancellationToken);

    /// <summary>Applies and verifies network settings inside the admitted runtime transaction.</summary>
    Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken);

    /// <summary>Publishes a verified snapshot before retained-decision cleanup is attempted.</summary>
    void ReportApplied(SettingsRuntimeSnapshot snapshot, SettingsResetScope scope, bool operationFailed);
}
