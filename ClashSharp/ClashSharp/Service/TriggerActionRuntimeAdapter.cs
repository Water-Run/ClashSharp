using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model;
using ClashSharp.Model.Triggers;
using ClashSharp.Settings;
using TriggerActionKind = ClashSharp.Model.Triggers.TriggerActionKind;

namespace ClashSharp.Service;

/// <summary>Adapts current application services to idempotent durable trigger-action semantics.</summary>
internal sealed class TriggerActionRuntimeAdapter : ITriggerActionRuntime
{
    private readonly IRuntimeSettingsAuthority _settings;
    private readonly StartupLaunchService _startupLaunch;
    private readonly IConnectionSamplingRuntime _sampling;
    private readonly MihomoConnectionService _connections;
    private readonly INetworkStateObserver _networkObserver;
    private readonly MihomoServiceManager? _mihomoService;
    private readonly IIdempotentTriggerNotificationSink _notifications;
    private readonly ITriggerLifecycleHandoff _exitHandoff;

    public TriggerActionRuntimeAdapter(
        IRuntimeSettingsAuthority settings,
        StartupLaunchService startupLaunch,
        IConnectionSamplingRuntime sampling,
        MihomoConnectionService connections,
        INetworkStateObserver networkObserver,
        IIdempotentTriggerNotificationSink notifications,
        ITriggerLifecycleHandoff exitHandoff,
        MihomoServiceManager? mihomoService = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _startupLaunch = startupLaunch ?? throw new ArgumentNullException(nameof(startupLaunch));
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _networkObserver = networkObserver ?? throw new ArgumentNullException(nameof(networkObserver));
        _mihomoService = mihomoService;
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _exitHandoff = exitHandoff ?? throw new ArgumentNullException(nameof(exitHandoff));
    }

    public async Task<TriggerActionProbeResult> ProbeAsync(
        TriggerOutboxAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (action.DesiredEffect.Kind == TriggerActionKind.ExitApplication)
            {
                return await _exitHandoff.ProbeAsync(action, cancellationToken).ConfigureAwait(false);
            }

            bool? desired = action.DesiredEffect.Kind switch
            {
                TriggerActionKind.CloseConnections =>
                    (await _connections.GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false)).Count == 0,
                TriggerActionKind.SetLaunchAtStartup => await ProbeStartupLaunchAsync(
                    RequireBoolean(action),
                    cancellationToken).ConfigureAwait(false),
                TriggerActionKind.SetTransparentProxy =>
                    await ProbeTransparentProxyAsync(
                        RequireBoolean(action),
                        cancellationToken).ConfigureAwait(false),
                TriggerActionKind.SetConnectionSampling => ProbeConnectionSampling(RequireBoolean(action)),
                TriggerActionKind.SwitchProxyMode => await ProbeNetworkModeAsync(
                    RequireMode(action),
                    cancellationToken).ConfigureAwait(false),
                TriggerActionKind.SendNotification => await _notifications
                    .IsTriggerNotificationDeliveredAsync(action.IdempotencyKey, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidDataException("The durable outbox contains an unsupported trigger action."),
            };
            return desired switch
            {
                true => TriggerActionProbeResult.Desired(),
                false => TriggerActionProbeResult.NotDesired(),
                null => TriggerActionProbeResult.Unknown("trigger.action.probe_unavailable"),
            };
        }
        catch (Exception exception) when (IsExpectedProbeFailure(exception))
        {
            return TriggerActionProbeResult.Unknown("trigger.action.probe_unavailable");
        }
    }

    public async Task<TriggerActionApplyResult> ApplyAsync(
        TriggerOutboxAction action,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        switch (action.DesiredEffect.Kind)
        {
            case TriggerActionKind.CloseConnections:
                await _connections.CloseAllConnectionsAsync(cancellationToken).ConfigureAwait(false);
                return TriggerActionApplyResult.Applied();
            case TriggerActionKind.SetLaunchAtStartup:
                return await ApplySettingAsync(SettingsRegistry.Keys.LaunchAtStartupEnabled,
                    RequireBoolean(action), admissionLease, cancellationToken).ConfigureAwait(false);
            case TriggerActionKind.SetTransparentProxy:
                return await ApplySettingAsync(SettingsRegistry.Keys.TransparentProxyEnabled,
                    RequireBoolean(action), admissionLease, cancellationToken).ConfigureAwait(false);
            case TriggerActionKind.SetConnectionSampling:
                return await ApplySettingAsync(SettingsRegistry.Keys.ConnectionSamplingEnabled,
                    RequireBoolean(action), admissionLease, cancellationToken).ConfigureAwait(false);
            case TriggerActionKind.SwitchProxyMode:
                return await ApplySettingAsync(SettingsRegistry.Keys.CurrentMode,
                    RequireMode(action), admissionLease, cancellationToken).ConfigureAwait(false);
            case TriggerActionKind.ExitApplication:
                return await _exitHandoff.HandOffAsync(action, cancellationToken).ConfigureAwait(false);
            case TriggerActionKind.SendNotification:
                await _notifications.DeliverTriggerNotificationAsync(
                    action.IdempotencyKey,
                    RequireNotificationMessage(action),
                    cancellationToken).ConfigureAwait(false);
                return TriggerActionApplyResult.Applied();
            default:
                throw new InvalidDataException("The durable outbox contains an unsupported trigger action.");
        }
    }

    private async Task<bool?> ProbeStartupLaunchAsync(bool desired, CancellationToken cancellationToken)
    {
        StartupLaunchTaskState? state = await _startupLaunch
            .TryGetStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        bool enabled = state == StartupLaunchTaskState.Enabled;
        return ReadDesired<bool>(SettingsRegistry.Keys.LaunchAtStartupEnabled) == desired && enabled == desired;
    }

    private bool ProbeConnectionSampling(bool desired)
    {
        return ReadDesired<bool>(SettingsRegistry.Keys.ConnectionSamplingEnabled) == desired && _sampling.IsRunning == desired;
    }

    private async Task<bool?> ProbeTransparentProxyAsync(
        bool desired,
        CancellationToken cancellationToken)
    {
        SettingsEnvelope preferences = _settings.CaptureSnapshot().Envelope;
        if (preferences.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>() != desired)
        {
            return false;
        }

        NetworkStateSnapshot observed = await _networkObserver
            .ObserveAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!observed.IsKnown)
        {
            return null;
        }

        ClashSharpMode mode = preferences.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>();
        bool takeoverMode = mode is
            ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
        bool expectedTransparentProxy = false;
        if (desired && takeoverMode)
        {
            if (_mihomoService is null)
            {
                return null;
            }

            MihomoServiceStatus serviceStatus = await _mihomoService
                .GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!serviceStatus.IsKnown)
            {
                return null;
            }

            if (!serviceStatus.IsInstalled) { return false; }
            expectedTransparentProxy = true;
        }

        return observed.Mode == mode
            && observed.MixedPort == preferences.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>()
            && observed.TransparentProxyEnabled == expectedTransparentProxy
            && IsEffectiveModeState(observed);
    }

    private async Task<bool?> ProbeNetworkModeAsync(
        ClashSharpMode desiredMode,
        CancellationToken cancellationToken)
    {
        NetworkStateSnapshot observed = await _networkObserver
            .ObserveAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!observed.IsKnown)
        {
            return null;
        }

        SettingsEnvelope preferences = _settings.CaptureSnapshot().Envelope;
        return preferences.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>() == desiredMode
            && observed.Mode == desiredMode
            && observed.MixedPort == preferences.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>()
            && IsEffectiveModeState(observed);
    }

    private async Task<TriggerActionApplyResult> ApplySettingAsync<T>(
        SettingKey key, T value, MutationAdmissionLease admissionLease, CancellationToken cancellationToken) where T : notnull
    {
        SettingDefinition definition = SettingsRegistry.Default.Get(key.Value);
        SettingNormalizationResult normalized = definition.NormalizeValue(value);
        if (!normalized.IsSuccess) { throw new InvalidDataException("The trigger setting is invalid."); }
        SettingsAuthorityResult result = await _settings.ApplyRuntimeChangesAdmittedAsync(
            [new(key, normalized.Value!)], Guid.NewGuid(), admissionLease, cancellationToken).ConfigureAwait(false);
        return ClassifySettingsCommandResult(result, definition.ApplicationKind);
    }

    private static TriggerActionApplyResult ClassifySettingsCommandResult(SettingsAuthorityResult result, SettingApplicationKind kind)
    {
        if (result.IsSucceeded) { return TriggerActionApplyResult.Applied(); }
        string diagnosticCode = result.Code ?? "trigger.action.settings_failed";
        bool unresolved = result.Status is SettingsAuthorityStatus.ApplicationFailed or SettingsAuthorityStatus.PersistenceFailed
            || result.Envelope?.PendingApplications.Any(batch => batch.ApplicationKind == kind
                && batch.State is SettingsApplicationBatchState.Running or SettingsApplicationBatchState.Failed) == true;
        return unresolved
            ? TriggerActionApplyResult.Uncertain(diagnosticCode)
            : TriggerActionApplyResult.Failed(diagnosticCode);
    }

    private T ReadDesired<T>(SettingKey key) where T : notnull =>
        _settings.CaptureSnapshot().Envelope.Desired[key].Value.Get<T>();

    private static bool IsEffectiveModeState(NetworkStateSnapshot state)
    {
        return state.Mode switch
        {
            ClashSharpMode.Disabled =>
                !state.CoreRunning && !state.SystemProxyEnabled && !state.TransparentProxyEnabled,
            ClashSharpMode.Standby =>
                state.CoreRunning && !state.SystemProxyEnabled && !state.TransparentProxyEnabled,
            ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover =>
                state.CoreRunning && state.SystemProxyEnabled != state.TransparentProxyEnabled,
            _ => false,
        };
    }

    private static bool RequireBoolean(TriggerOutboxAction action)
    {
        return action.DesiredEffect.Parameters is BooleanActionParameters parameters
            ? parameters.Value
            : throw new InvalidDataException("The durable Boolean trigger action has invalid parameters.");
    }

    private static ClashSharpMode RequireMode(TriggerOutboxAction action)
    {
        return action.DesiredEffect.Parameters is ProxyModeActionParameters parameters
            && Enum.IsDefined(parameters.Mode)
            && parameters.Mode != ClashSharpMode.Faulted
                ? parameters.Mode
                : throw new InvalidDataException("The durable proxy-mode trigger action has invalid parameters.");
    }

    private static string RequireNotificationMessage(TriggerOutboxAction action)
    {
        return action.DesiredEffect.Parameters is NotificationActionParameters parameters
            && !string.IsNullOrWhiteSpace(parameters.Message)
                ? parameters.Message.Trim()
                : throw new InvalidDataException("The durable notification trigger action has invalid parameters.");
    }

    private static bool IsExpectedProbeFailure(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            HttpRequestException or
            JsonException or
            COMException;
    }
}
