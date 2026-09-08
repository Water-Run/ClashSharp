using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

internal sealed partial class SettingsViewModel
{
    /// <summary>Projects page callbacks and an already-owned runtime scope into application ports.</summary>
    private sealed class ResetOperation(
        SettingsViewModel owner,
        ISettingsDestructiveRuntimeScope runtimeMutation) : ISettingsResetOperation
    {
        public SettingsRuntimeSnapshot CaptureSnapshot() => new(
            owner._settings.DisplayLanguage,
            owner._settings.AppThemeMode,
            owner._settings.AppAccentColorMode,
            owner._settings.AppAccentColorValue,
            owner._settings.LaunchAtStartupEnabled,
            owner._settings.ConnectionSamplingEnabled,
            owner._settings.ConnectionSamplingIntervalSeconds,
            owner._settings.CurrentMode,
            owner._settings.ActiveProfileId,
            owner._settings.TransparentProxyEnabled,
            owner._settings.MixedPort);

        public IRetainedSettingsResetReceipt BeginReset(SettingsResetScope scope, bool transparentProxyEnabled) => scope switch
        {
            SettingsResetScope.All => runtimeMutation.BeginResetSettings(),
            SettingsResetScope.Startup => runtimeMutation.BeginResetStartupSettings(),
            SettingsResetScope.Proxy or SettingsResetScope.TransparentProxy =>
                runtimeMutation.BeginResetNetworkSettings(scope, transparentProxyEnabled),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

        public void RestoreDurableSnapshot(SettingsRuntimeSnapshot snapshot) =>
            runtimeMutation.RestoreDurableSettings(new SettingsExternalDurableSnapshot(
                snapshot.DisplayLanguage,
                snapshot.AppThemeMode,
                snapshot.AppAccentColorMode,
                snapshot.AppAccentColorValue,
                snapshot.LaunchAtStartupEnabled,
                snapshot.ConnectionSamplingEnabled,
                snapshot.ConnectionSamplingIntervalSeconds,
                snapshot.CurrentMode,
                snapshot.ActiveProfileId,
                snapshot.TransparentProxyEnabled,
                snapshot.MixedPort));

        public void ApplyLanguage(AppLanguage language) => owner._applyLanguage(language);

        public void ApplyTheme(AppThemeMode theme) => owner._applyTheme(theme);

        public void ApplyAccentColor(AppAccentColorMode mode, string value) => owner._applyAccentColor(mode, value);

        public Task ApplyLaunchAtStartupAsync(bool enabled, CancellationToken cancellationToken) =>
            runtimeMutation.ApplyLaunchAtStartupAsync(enabled, cancellationToken);

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken) =>
            runtimeMutation.RestartConnectionSamplingAsync(cancellationToken);

        public async Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken)
        {
            await owner._networkSettingsGate.WaitAsync(cancellationToken);
            try
            {
                await runtimeMutation.ApplyNetworkSettingsAsync(transparentProxyEnabled, mixedPort, cancellationToken);
            }
            finally
            {
                owner._networkSettingsGate.Release();
            }
        }

        public void ReportApplied(SettingsRuntimeSnapshot snapshot, SettingsResetScope scope, bool operationFailed)
        {
            owner.MarkExternalSettingsApplied(snapshot, scope);
            owner.IsResetRecoveryRequired = false;
            owner.OperationErrorText = operationFailed ? owner._getString("Application.UnexpectedError") : string.Empty;
        }
    }
}
