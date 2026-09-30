using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

internal sealed partial class SettingsViewModel
{
    private readonly ISettingsRuntimeGroupReset? _runtimeGroupReset;
    private readonly Action _requireSettingsRestart;
    private SettingsRuntimeGroupResetResult? _pendingRuntimeGroupReset;

    public bool CanRetrySettingsReset => _pendingRuntimeGroupReset is not null;
    public string RetrySettingsResetText => _getString("Settings.Reset.Retry");

    private void ClearRuntimeGroupResetRetry()
    {
        _pendingRuntimeGroupReset = null;
        OnPropertyChanged(nameof(CanRetrySettingsReset));
    }

    /// <summary>Retries only the failed applications whose identities were presented to the user.</summary>
    public async Task RetryRuntimeSettingsResetAsync(CancellationToken cancellationToken)
    {
        await _resetSettingsGate.WaitAsync(cancellationToken);
        try
        {
            await WaitForOutstandingRuntimeSettingsAsync(cancellationToken);
            SettingsRuntimeGroupResetResult previous = _pendingRuntimeGroupReset
                ?? throw new InvalidOperationException("There is no pending settings reset to retry.");
            SettingsRuntimeGroupResetResult result = await _runtimeGroupReset!.RetryRuntimeGroupAsync(previous, cancellationToken);
            PublishRuntimeGroupReset(result);
        }
        finally { _resetSettingsGate.Release(); }
    }

    private void PublishRuntimeGroupReset(SettingsRuntimeGroupResetResult result)
    {
        _pendingRuntimeGroupReset = result.CanRetry ? result : null;
        SettingsAuthorityResult outcome = result.Outcome;
        if (outcome.Envelope is { } envelope)
        {
            if (outcome.IsSucceeded)
            {
                T Value<T>(SettingKey key) where T : notnull => envelope.Desired[key].Value.Get<T>();
                MarkExternalSettingsApplied(new SettingsRuntimeSnapshot(
                    Value<AppLanguage>(SettingsRegistry.Keys.DisplayLanguage), Value<AppThemeMode>(SettingsRegistry.Keys.AppThemeMode),
                    Value<AppAccentColorMode>(SettingsRegistry.Keys.AppAccentColorMode), Value<string>(SettingsRegistry.Keys.AppAccentColorValue),
                    Value<bool>(SettingsRegistry.Keys.LaunchAtStartupEnabled), Value<bool>(SettingsRegistry.Keys.ConnectionSamplingEnabled),
                    Value<int>(SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds), Value<ClashSharpMode>(SettingsRegistry.Keys.CurrentMode),
                    Value<string>(SettingsRegistry.Keys.ActiveProfileId), Value<bool>(SettingsRegistry.Keys.TransparentProxyEnabled),
                    Value<int>(SettingsRegistry.Keys.MixedPort)), result.Scope);
            }
            if (result.Scope == SettingsResetScope.Startup) { ReloadStartupSettingsAfterReset(envelope); }
            else { ReloadNetworkSettingsAfterReset(result.Scope == SettingsResetScope.Proxy, envelope); }
        }
        OnPropertyChanged(nameof(CanRetrySettingsReset));
        OnPropertyChanged(nameof(RetrySettingsResetText));
        if (result.RequiresRestart)
        {
            _dataReplacementRestartPending = true;
            _requireSettingsRestart();
            OnPropertyChanged(nameof(HasRestartRequiredSettings));
        }
        if (outcome.Status == SettingsAuthorityStatus.DeferredToRestart)
        {
            OperationErrorText = string.Empty;
            return;
        }
        OperationErrorText = outcome.IsSucceeded ? string.Empty : _getString(outcome.Code == "settings.reset.stale_retry"
            ? "Settings.Reset.Stale" : result.CanRetry ? "Settings.Reset.Unapplied" : "Application.UnexpectedError");
        if (!outcome.IsSucceeded) { throw new SettingsRuntimeGroupResetException(outcome); }
    }

    private static T ReadResetValue<T>(SettingsEnvelope? envelope, SettingKey key, Func<T> readLegacy) where T : notnull =>
        envelope is null ? readLegacy() : envelope.Desired[key].Value.Get<T>();
}
