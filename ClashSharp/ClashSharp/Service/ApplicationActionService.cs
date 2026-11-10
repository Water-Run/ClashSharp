using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Diagnostics;
using ClashSharp.Model;
using ClashSharp.Settings;
using TriggerEventKind = global::ClashSharp.Model.Triggers.TriggerEventKind;

namespace ClashSharp.Service;

/// <summary>Default shared dispatcher for non-picker application actions.</summary>
internal sealed class ApplicationActionService : IApplicationActionDispatcher
{
    private static ApplicationActionService? _instance;

    public static ApplicationActionService Instance => Volatile.Read(ref _instance)
        ?? throw new InvalidOperationException("Application actions are unavailable before primary host startup.");

    private readonly IRuntimeSettingsAuthority _settings;
    private readonly IControllerCredentialProvider _controllerCredentials;
    private readonly MutationAdmissionBarrier _admissionBarrier;
    private readonly NetworkMaintenanceCoordinator _networkMaintenance;
    private readonly INetworkStateObserver _networkObserver;
    private readonly IConnectionSamplingRuntime _sampling;
    private readonly MihomoConnectionService _connections;
    private readonly IApplicationNotificationSink _notifications;
    private readonly OperationNotificationRunner _operationNotifications;
    private readonly ITriggerRuntimeEventPublisher _triggerEvents;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;
    private readonly ApplicationLifecycleService _lifecycle;

    private readonly RuntimeLifecycleCoordinator _shutdown;
    private readonly StartupLaunchService _startupLaunch;
    private readonly IApplicationDataClearOperationFactory? _dataClearOperations;

    private readonly ProxyModeAnnouncer _modeAnnouncer = new();

    internal ApplicationActionService(
        IRuntimeSettingsAuthority settings,
        MutationAdmissionBarrier admissionBarrier,
        NetworkStateCoordinator network,
        INetworkStateObserver networkObserver,
        IConnectionSamplingRuntime sampling,
        MihomoConnectionService connections,
        IApplicationNotificationSink notifications,
        ITriggerRuntimeEventPublisher triggerEvents,
        Action<string, string, string, string?> appendLog,
        Func<string, string> getString,
        ApplicationLifecycleService lifecycle,
        RuntimeLifecycleCoordinator shutdown,
        StartupLaunchService startupLaunch,
        IControllerCredentialProvider controllerCredentials,
        bool installAsPrimaryInstance = true,
        IApplicationDataClearOperationFactory? dataClearOperations = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _controllerCredentials = controllerCredentials ?? throw new ArgumentNullException(nameof(controllerCredentials));
        _admissionBarrier = admissionBarrier ?? throw new ArgumentNullException(nameof(admissionBarrier));
        ArgumentNullException.ThrowIfNull(network);
        _networkMaintenance = new(network, admissionBarrier);
        _networkObserver = networkObserver ?? throw new ArgumentNullException(nameof(networkObserver));
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _operationNotifications = new(notifications as IUserOperationNotifications);
        _triggerEvents = triggerEvents ?? throw new ArgumentNullException(nameof(triggerEvents));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _startupLaunch = startupLaunch ?? throw new ArgumentNullException(nameof(startupLaunch));
        _dataClearOperations = dataClearOperations;
        if (installAsPrimaryInstance && dataClearOperations is null) { throw new ArgumentNullException(nameof(dataClearOperations)); }
        if (installAsPrimaryInstance && Interlocked.CompareExchange(ref _instance, this, null) is not null)
        {
            throw new InvalidOperationException("The primary application action service is already configured.");
        }
    }

    public async Task DispatchAsync(ApplicationActionKind kind, string value, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ApplicationActionKind.SetLaunchAtStartup:
                bool launchAtStartup = ParseBoolean(value);
                await ApplyLaunchAtStartupAsync(launchAtStartup, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ApplicationActionKind.SetTransparentProxy:
                await ApplyTransparentProxyAsync(
                        ParseBoolean(value),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ApplicationActionKind.SetConnectionSampling:
                await ApplyConnectionSamplingAsync(ParseBoolean(value), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ApplicationActionKind.SwitchProxyMode:
                ClashSharpMode mode = Enum.TryParse(value, out ClashSharpMode parsedMode)
                    && Enum.IsDefined(parsedMode)
                    && parsedMode != ClashSharpMode.Faulted
                        ? parsedMode
                        : GetSupportedCurrentMode();
                NetworkTakeoverResult result = await ApplyNetworkModeAsync(mode, cancellationToken).ConfigureAwait(false);
                await PublishProxyModeAppliedAsync(result.Mode, CancellationToken.None).ConfigureAwait(false);
                break;
            case ApplicationActionKind.CloseConnections:
                await _operationNotifications.ExecuteAsync(() => _connections.CloseAllConnectionsAsync(cancellationToken),
                    "Nav.Connections", "Connections.Status.ClosedAll", "Application.UnexpectedError", cancellationToken).ConfigureAwait(false);
                break;
            case ApplicationActionKind.SendNotification:
                _notifications.NotifyCustom(value);
                break;
            case ApplicationActionKind.ExitApplication:
                _lifecycle.RequestExit("application-action");
                break;
            case ApplicationActionKind.ExportConfiguration:
            case ApplicationActionKind.ImportConfiguration:
                _appendLog(
                    "Info",
                    "ApplicationAction",
                    string.Format(CultureInfo.CurrentCulture, _getString("ApplicationAction.UiPickerRequired.Format"), kind),
                    value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported application action.");
        }
    }

    /// <summary>Applies and independently observes a mode through the settings authority.</summary>
    public async Task<NetworkTakeoverResult> ApplyNetworkModeAsync(
        ClashSharpMode mode,
        CancellationToken cancellationToken)
    {
        return await ApplyNetworkChangesAsync([Change(SettingsRegistry.Keys.CurrentMode, mode)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies TUN and mixed-port preferences through the same verified runtime transaction as a mode change.
    /// </summary>
    internal async Task<NetworkTakeoverResult> ApplyNetworkSettingsAsync(
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        return await ApplyNetworkChangesAsync(
            [Change(SettingsRegistry.Keys.TransparentProxyEnabled, transparentProxyEnabled), Change(SettingsRegistry.Keys.MixedPort, mixedPort)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes ordinary mutation admission and returns the sole settings-destructive lease.</summary>
    internal ValueTask<MutationAdmissionLease> BeginSettingsDestructiveMutationAsync(
        CancellationToken cancellationToken)
    {
        // Require the independently verified startup credential before destructive recovery
        // can generate runtime configuration under exclusive admission. This is a pure read.
        _ = _controllerCredentials.GetSecret();
        return _admissionBarrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            cancellationToken);
    }

    /// <summary>Applies startup registration while a settings-destructive lease is already active.</summary>
    internal Task ApplyLaunchAtStartupAdmittedAsync(
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        // The destructive coordinator already owns the desired settings generation.
        // This participant changes only the external StartupTask registration.
        return _startupLaunch.SetEnabledAsync(isEnabled, cancellationToken);
    }

    /// <summary>Restarts sampling while a settings-destructive lease is already active.</summary>
    internal Task RestartConnectionSamplingAdmittedAsync(CancellationToken cancellationToken)
    {
        return _sampling.RestartFromSettingsAsync(cancellationToken);
    }

    /// <summary>Changes only the requested TUN preference, reading mode and port under mutation ownership.</summary>
    internal Task<NetworkTakeoverResult> ApplyTransparentProxyAsync(
        bool transparentProxyEnabled,
        CancellationToken cancellationToken)
    {
        return ApplyNetworkChangesAsync([Change(SettingsRegistry.Keys.TransparentProxyEnabled, transparentProxyEnabled)], cancellationToken);
    }

    private async Task<NetworkTakeoverResult> ApplyNetworkChangesAsync(
        IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken)
    {
        try { return await ApplyNetworkChangesCoreAsync(changes, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)
            && !ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken))
        {
            string? diagnosticCode = RuntimeDiagnosticCode.Extract(failure);
            TryNotify(() => _notifications.NotifyProxyFailure(diagnosticCode));
            OperationNotificationRunner.MarkFailureReported(failure);
            throw;
        }
    }

    private static readonly SettingKey[] NetworkApplicationKeys =
    [
        SettingsRegistry.Keys.CurrentMode, SettingsRegistry.Keys.ActiveProfileId,
        SettingsRegistry.Keys.TransparentProxyEnabled, SettingsRegistry.Keys.MixedPort,
    ];

    /// <summary>
    /// Runs one explicit network command and resolves a failed network attempt left by an earlier command.
    /// </summary>
    /// <remarks>
    /// The settings layer never replaces a failed attempt silently, and that contract is unchanged. A failed
    /// network attempt (for example a profile the transparent proxy service cannot host) would otherwise make
    /// every later network action fail, including switching to system proxy as the failure message advises,
    /// until the user found the retry link on the Settings page. The new command is itself the user's request
    /// to try again, so each failed network attempt is retried under a fresh identity before the command is run
    /// once more. A genuine new failure is returned as it is; it is never retried in a loop.
    /// </remarks>
    private async Task<SettingsAuthorityResult> ApplyNetworkCommandAsync(
        IReadOnlyList<SettingValueChange> changes, MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        SettingsAuthorityResult command = await _settings.ApplyRuntimeChangesAdmittedAsync(
            changes, Guid.NewGuid(), lease, cancellationToken).ConfigureAwait(false);
        if (command.Envelope is null || !IsBlockedByFailedNetworkAttempt(command))
        {
            return command;
        }

        foreach (SettingsApplicationBatch failed in command.Envelope.PendingApplications
                     .Where(IsFailedNetworkBatch).ToArray())
        {
            SettingsAuthorityResult retried = await _settings.RetryAdmittedAsync(
                failed.BatchId, failed.AttemptId, Guid.NewGuid(), lease, cancellationToken).ConfigureAwait(false);
            if (!retried.IsSucceeded)
            {
                return retried;
            }
        }

        return await _settings.ApplyRuntimeChangesAdmittedAsync(
            changes, Guid.NewGuid(), lease, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsFailedNetworkBatch(SettingsApplicationBatch batch) =>
        batch.State == SettingsApplicationBatchState.Failed && batch.ApplicationKind == SettingApplicationKind.Network;

    private static bool IsBlockedByFailedNetworkAttempt(SettingsAuthorityResult command)
    {
        SettingsEnvelope envelope = command.Envelope!;
        if (!envelope.PendingApplications.Any(IsFailedNetworkBatch))
        {
            return false;
        }

        // Either the retained attempt refused to start again, or the command itself took effect but a key of
        // the retained attempt could not be verified, which the caller would report as superseded.
        return command.Status == SettingsAuthorityStatus.Rejected && command.Code == "settings.application.state"
            || command.IsSucceeded && NetworkApplicationKeys.Any(key => envelope.Applied[key].Kind != SettingAppliedStateKind.Verified);
    }

    private async Task<NetworkTakeoverResult> ApplyNetworkChangesCoreAsync(
        IReadOnlyList<SettingValueChange> changes,
        CancellationToken cancellationToken)
    {
        using MutationAdmissionLease lease = await _admissionBarrier.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
        SettingsEnvelope before = _settings.CaptureSnapshot().Envelope;
        SettingsAuthorityResult command = await ApplyNetworkCommandAsync(changes, lease, cancellationToken).ConfigureAwait(false);
        SettingsEnvelope committed = RequireApplied(command);
        // Keep mutation admission until the final independent read, including after a page cancels.
        // A directory transition must drain this complete operation before retiring its runtime.
        NetworkStateSnapshot state = await _networkObserver.ObserveAsync(CancellationToken.None).ConfigureAwait(false);
        SettingsEnvelope current = _settings.CaptureSnapshot().Envelope;
        SettingKey[] networkKeys = [SettingsRegistry.Keys.CurrentMode, SettingsRegistry.Keys.ActiveProfileId,
            SettingsRegistry.Keys.TransparentProxyEnabled, SettingsRegistry.Keys.MixedPort];
        if (networkKeys.Any(key => committed.Applied[key].Kind != SettingAppliedStateKind.Verified
            || current.Applied[key].Kind != SettingAppliedStateKind.Verified
            || !current.Applied[key].Value!.Equals(committed.Applied[key].Value))
            || changes.Any(change => !committed.Applied[change.Key].Value!.Equals(change.Value)
                || !current.Desired[change.Key].Value.Equals(change.Value)))
        {
            throw new SettingsActionFailedException(SettingsAuthorityStatus.ApplicationFailed, "settings.network_result_superseded");
        }
        ClashSharpMode mode = committed.Applied[SettingsRegistry.Keys.CurrentMode].Value!.Get<ClashSharpMode>();
        bool tunRequested = committed.Applied[SettingsRegistry.Keys.TransparentProxyEnabled].Value!.Get<bool>()
            && mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
        int port = committed.Applied[SettingsRegistry.Keys.MixedPort].Value!.Get<int>();
        bool systemProxyRequired = !tunRequested && mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
        if (!state.IsKnown || state.Mode != mode || state.MixedPort != port
            || state.CoreRunning != (mode != ClashSharpMode.Disabled) || state.TransparentProxyEnabled != tunRequested
            || systemProxyRequired && !state.SystemProxyEnabled)
        {
            throw new SettingsActionFailedException(SettingsAuthorityStatus.ApplicationFailed, "settings.network_result_unverified");
        }
        MihomoCoreOwner requestedOwner = mode == ClashSharpMode.Disabled
            ? MihomoCoreOwner.None
            : tunRequested
                ? MihomoCoreOwner.Service
                : MihomoCoreOwner.App;
        if (changes.Any(change => change.Key == SettingsRegistry.Keys.TransparentProxyEnabled
            && !before.Desired[change.Key].Value.Equals(change.Value)))
        {
            TryNotify(() => _notifications.NotifyTransparentProxyChanged(
                committed.Applied[SettingsRegistry.Keys.TransparentProxyEnabled].Value!.Get<bool>(), state.TransparentProxyEnabled));
            OperationNotificationRunner.MarkSuccessReported();
        }
        else if (changes.Any(change => change.Key == SettingsRegistry.Keys.MixedPort
            && !before.Desired[change.Key].Value.Equals(change.Value)))
        {
            TryNotify(_notifications.NotifyNetworkSettingsChanged);
            OperationNotificationRunner.MarkSuccessReported();
        }
        return new NetworkTakeoverResult(
            state.Mode,
            state.CoreRunning,
            state.SystemProxyEnabled,
            state.TransparentProxyEnabled,
            GetNetworkResultMessage(state.Mode, state.TransparentProxyEnabled, tunRequested),
            requestedOwner,
            tunRequested);
    }

    private Task ApplyLaunchAtStartupAsync(
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        return ApplySettingsAsync([Change(SettingsRegistry.Keys.LaunchAtStartupEnabled, isEnabled)], cancellationToken);
    }

    private static void TryNotify(Action publish)
    {
        try { publish(); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            // Presentation transport cannot replace an independently verified transaction outcome.
        }
    }

    /// <summary>Applies the settings page's complete sampling choice through the shared coordinator.</summary>
    internal Task ApplyConnectionSamplingSettingsAsync(
        bool isEnabled,
        int intervalSeconds,
        CancellationToken cancellationToken) =>
        ApplySettingsAsync([Change(SettingsRegistry.Keys.ConnectionSamplingEnabled, isEnabled),
            Change(SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds, intervalSeconds)], cancellationToken);

    private Task ApplyConnectionSamplingAsync(
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        return ApplySettingsAsync([Change(SettingsRegistry.Keys.ConnectionSamplingEnabled, isEnabled)], cancellationToken);
    }

    /// <summary>Disables an explicitly confirmed conflicting Windows proxy through durable mutation.</summary>
    public async Task DisableWindowsProxyAsync(CancellationToken cancellationToken)
    {
        MutationResult<NetworkTransitionResult> mutation = await _networkMaintenance
            .ApplyAsync(
                () =>
                {
                    SettingsEnvelope preferences = _settings.CaptureSnapshot().Envelope;
                    return NetworkIntent.DisableConflictingProxy(
                        preferences.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>(),
                        preferences.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>(),
                        preferences.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (mutation.Outcome != MutationOutcome.Succeeded)
        {
            throw new NetworkTransitionFailedException(mutation.Outcome, mutation.ErrorCode);
        }
    }

    /// <summary>
    /// Announces a verified mode: a notification only when the mode changed, and the proxy-start trigger event only
    /// when traffic takeover begins. Re-selecting the active mode repairs the runtime silently.
    /// </summary>
    public Task PublishProxyModeAppliedAsync(ClashSharpMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (bool modeChanged, bool proxyStarted) = _modeAnnouncer.Observe(mode);
        // The operation itself succeeded either way; its generic success feedback must not be repeated.
        OperationNotificationRunner.MarkSuccessReported();
        if (modeChanged) { TryNotify(() => _notifications.NotifyProxyModeChanged(mode)); }
        if (proxyStarted) { _triggerEvents.Publish(new TriggerRuntimeEvent(TriggerEventKind.ProxyStarted)); }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Hands data removal to the outer lifetime before any destructive work starts.
    /// </summary>
    internal Task ClearAllDataAndRestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        IApplicationLifetimeMaintenance maintenance = (_dataClearOperations
            ?? throw new InvalidOperationException("Data removal is unavailable without a host-owned operation factory.")).Create();
        if (!_lifecycle.TryRequest(ApplicationLifetimeRequest.Restart("clear-all-data", maintenance)))
        {
            throw new InvalidOperationException("Another application exit or restart is already in progress.");
        }

        return Task.CompletedTask;
    }

    private static bool ParseBoolean(string value)
    {
        return bool.TryParse(value, out bool parsed) && parsed;
    }

    private string GetNetworkResultMessage(ClashSharpMode mode, bool tunEffective, bool tunRequested)
    {
        string key = mode switch
        {
            ClashSharpMode.Disabled => "NetworkTakeover.Disabled",
            ClashSharpMode.Standby => "NetworkTakeover.Standby",
            ClashSharpMode.FullTakeover when tunEffective => "NetworkTakeover.TransparentProxy.Full",
            ClashSharpMode.RuleTakeover when tunEffective => "NetworkTakeover.TransparentProxy.Rule",
            ClashSharpMode.FullTakeover when tunRequested => "NetworkTakeover.TransparentProxyServiceMissing.Full",
            ClashSharpMode.RuleTakeover when tunRequested => "NetworkTakeover.TransparentProxyServiceMissing.Rule",
            ClashSharpMode.FullTakeover => "NetworkTakeover.SystemProxy.Full",
            ClashSharpMode.RuleTakeover => "NetworkTakeover.SystemProxy.Rule",
            _ => throw new InvalidOperationException("The verified network transition returned an unsupported mode."),
        };
        return _getString(key);
    }

    private ClashSharpMode GetSupportedCurrentMode()
    {
        ClashSharpMode mode = _settings.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>();
        return Enum.IsDefined(mode) && mode != ClashSharpMode.Faulted
            ? mode
            : ClashSharpMode.Disabled;
    }

    private Task ApplySettingsAsync(IReadOnlyList<SettingValueChange> changes, CancellationToken cancellationToken) =>
        _operationNotifications.ExecuteAsync(async () =>
        {
            SettingsEnvelope before = _settings.CaptureSnapshot().Envelope;
            _ = RequireApplied(await _settings.ApplyRuntimeChangesAsync(changes, Guid.NewGuid(), cancellationToken).ConfigureAwait(false));
            return changes.Any(change => !before.Desired[change.Key].Value.Equals(change.Value)) ? true : (bool?)null;
        }, "Nav.Settings", "Notification.Settings.Saved", "Application.UnexpectedError", cancellationToken);

    private static SettingsEnvelope RequireApplied(SettingsAuthorityResult result) => result.IsSucceeded && result.Envelope is not null
        ? result.Envelope : throw new SettingsActionFailedException(result.Status, result.Code ?? "settings.action.failed");

    private static SettingValueChange Change<T>(SettingKey key, T value) where T : notnull
    {
        SettingNormalizationResult normalized = SettingsRegistry.Default.Get(key.Value).NormalizeValue(value);
        return normalized.IsSuccess ? new(key, normalized.Value!) : throw new ArgumentException(normalized.Error!.Code, nameof(value));
    }
}

/// <summary>Preserves the durable mutation classification for UI and startup policy decisions.</summary>
internal sealed class NetworkTransitionFailedException : InvalidOperationException,
    IStableDiagnosticCodeProvider
{
    public NetworkTransitionFailedException(MutationOutcome outcome, string? errorCode)
        : base($"Network transition failed with outcome '{outcome}' and code '{errorCode}'.")
    {
        Outcome = outcome;
        ErrorCode = errorCode;
    }

    public MutationOutcome Outcome { get; }

    public string? ErrorCode { get; }

    public string DiagnosticCode => ErrorCode ?? string.Empty;
}
