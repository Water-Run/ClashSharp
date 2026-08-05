using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model;
using ClashSharp.Model.Triggers;
using TriggerActionKind = ClashSharp.Model.Triggers.TriggerActionKind;

namespace ClashSharp.Service;

/// <summary>Adapts current application services to idempotent durable trigger-action semantics.</summary>
internal sealed class TriggerActionRuntimeAdapter : ITriggerActionRuntime
{
    private readonly AppSettingsService _settings;
    private readonly StartupLaunchService _startupLaunch;
    private readonly ConnectionSamplingService _sampling;
    private readonly MihomoConnectionService _connections;
    private readonly NetworkStateCoordinator _network;
    private readonly INetworkStateObserver _networkObserver;
    private readonly MihomoServiceManager? _mihomoService;
    private readonly IIdempotentTriggerNotificationSink _notifications;
    private readonly ITriggerLifecycleHandoff _exitHandoff;

    public TriggerActionRuntimeAdapter(
        AppSettingsService settings,
        StartupLaunchService startupLaunch,
        ConnectionSamplingService sampling,
        MihomoConnectionService connections,
        NetworkStateCoordinator network,
        INetworkStateObserver networkObserver,
        IIdempotentTriggerNotificationSink notifications,
        ITriggerLifecycleHandoff exitHandoff,
        MihomoServiceManager? mihomoService = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _startupLaunch = startupLaunch ?? throw new ArgumentNullException(nameof(startupLaunch));
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _network = network ?? throw new ArgumentNullException(nameof(network));
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
                bool launchAtStartup = RequireBoolean(action);
                await _startupLaunch
                    .SetEnabledAsync(launchAtStartup, cancellationToken)
                    .ConfigureAwait(false);
                _settings.WriteAdmitted(
                    admissionLease,
                    editor => editor.LaunchAtStartupEnabled = launchAtStartup);
                return TriggerActionApplyResult.Applied();
            case TriggerActionKind.SetTransparentProxy:
                return await ApplyTransparentProxyAsync(
                    RequireBoolean(action),
                    admissionLease,
                    cancellationToken).ConfigureAwait(false);
            case TriggerActionKind.SetConnectionSampling:
                _settings.WriteAdmitted(
                    admissionLease,
                    editor => editor.ConnectionSamplingEnabled = RequireBoolean(action));
                await _sampling.RestartFromSettingsAsync(cancellationToken).ConfigureAwait(false);
                return TriggerActionApplyResult.Applied();
            case TriggerActionKind.SwitchProxyMode:
                return await ApplyNetworkModeAsync(
                    RequireMode(action),
                    admissionLease,
                    cancellationToken).ConfigureAwait(false);
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
        return _settings.LaunchAtStartupEnabled == desired && enabled == desired;
    }

    private bool ProbeConnectionSampling(bool desired)
    {
        return _settings.ConnectionSamplingEnabled == desired && _sampling.IsRunning == desired;
    }

    private async Task<bool?> ProbeTransparentProxyAsync(
        bool desired,
        CancellationToken cancellationToken)
    {
        if (_settings.TransparentProxyEnabled != desired)
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

        bool takeoverMode = _settings.CurrentMode is
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

            expectedTransparentProxy = serviceStatus.IsInstalled;
        }

        return observed.Mode == _settings.CurrentMode
            && observed.MixedPort == _settings.MixedPort
            && observed.TransparentProxyEnabled == expectedTransparentProxy
            && IsEffectiveModeState(observed);
    }

    private async Task<TriggerActionApplyResult> ApplyTransparentProxyAsync(
        bool transparentProxyEnabled,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        MutationResult<NetworkTransitionResult> result = await _network.ApplyAdmittedAsync(
            () => NetworkIntent.ChangeMode(
                _settings.CurrentMode,
                transparentProxyEnabled,
                _settings.MixedPort),
            admissionLease,
            cancellationToken).ConfigureAwait(false);
        return ClassifyNetworkMutationResult(result, cancellationToken);
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

        return _settings.CurrentMode == desiredMode
            && observed.Mode == desiredMode
            && observed.MixedPort == _settings.MixedPort
            && IsEffectiveModeState(observed);
    }

    private async Task<TriggerActionApplyResult> ApplyNetworkModeAsync(
        ClashSharpMode mode,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        MutationResult<NetworkTransitionResult> result = await _network.ApplyAdmittedAsync(
            () => NetworkIntent.ChangeMode(
                mode,
                _settings.TransparentProxyEnabled,
                _settings.MixedPort),
            admissionLease,
            cancellationToken).ConfigureAwait(false);
        return ClassifyNetworkMutationResult(result, cancellationToken);
    }

    private static TriggerActionApplyResult ClassifyNetworkMutationResult(
        MutationResult<NetworkTransitionResult> result,
        CancellationToken cancellationToken)
    {
        if (result.Outcome == MutationOutcome.Succeeded)
        {
            return TriggerActionApplyResult.Applied();
        }

        if (result.Outcome == MutationOutcome.Cancelled && cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        string diagnosticCode = result.ErrorCode ?? "trigger.action.network_transition_failed";
        return result.Outcome is MutationOutcome.RecoveryRequired or MutationOutcome.CommittedRecoveryRequired
            ? TriggerActionApplyResult.Uncertain(diagnosticCode)
            : TriggerActionApplyResult.Failed(diagnosticCode);
    }

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
