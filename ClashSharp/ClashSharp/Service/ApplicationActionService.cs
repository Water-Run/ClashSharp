using System;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Diagnostics;
using ClashSharp.Model;
using TriggerEventKind = global::ClashSharp.Model.Triggers.TriggerEventKind;

namespace ClashSharp.Service;

/// <summary>Default shared dispatcher for non-picker application actions.</summary>
internal sealed class ApplicationActionService : IApplicationActionDispatcher
{
    private static ApplicationActionService? _instance;

    public static ApplicationActionService Instance => Volatile.Read(ref _instance)
        ?? throw new InvalidOperationException("Application actions are unavailable before primary host startup.");

    private readonly AppSettingsService _settings;
    private readonly MutationAdmissionBarrier _admissionBarrier;
    private readonly NetworkStateCoordinator _network;
    private readonly ConnectionSamplingService _sampling;
    private readonly MihomoConnectionService _connections;
    private readonly IApplicationNotificationSink _notifications;
    private readonly ITriggerRuntimeEventPublisher _triggerEvents;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;
    private readonly ApplicationLifecycleService _lifecycle;

    private readonly IApplicationShutdownCoordinator _shutdown;
    private readonly StartupLaunchService _startupLaunch;

    internal ApplicationActionService(
        AppSettingsService settings,
        MutationAdmissionBarrier admissionBarrier,
        NetworkStateCoordinator network,
        ConnectionSamplingService sampling,
        MihomoConnectionService connections,
        IApplicationNotificationSink notifications,
        ITriggerRuntimeEventPublisher triggerEvents,
        Action<string, string, string, string?> appendLog,
        Func<string, string> getString,
        ApplicationLifecycleService lifecycle,
        IApplicationShutdownCoordinator shutdown,
        StartupLaunchService startupLaunch)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _admissionBarrier = admissionBarrier ?? throw new ArgumentNullException(nameof(admissionBarrier));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _triggerEvents = triggerEvents ?? throw new ArgumentNullException(nameof(triggerEvents));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _startupLaunch = startupLaunch ?? throw new ArgumentNullException(nameof(startupLaunch));
        if (Interlocked.CompareExchange(ref _instance, this, null) is not null)
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
                await PublishProxyModeAppliedAsync(result.Mode, cancellationToken).ConfigureAwait(false);
                break;
            case ApplicationActionKind.CloseConnections:
                await _connections.CloseAllConnectionsAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Applies and verifies a mode through the sole durable network mutation coordinator.</summary>
    public async Task<NetworkTakeoverResult> ApplyNetworkModeAsync(
        ClashSharpMode mode,
        CancellationToken cancellationToken)
    {
        return await ApplyNetworkIntentAsync(
                () => NetworkIntent.ChangeMode(
                    mode,
                    _settings.TransparentProxyEnabled,
                    _settings.MixedPort),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies TUN and mixed-port preferences through the same verified runtime transaction as a mode change.
    /// </summary>
    internal async Task<NetworkTakeoverResult> ApplyNetworkSettingsAsync(
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        return await ApplyNetworkIntentAsync(
                () => NetworkIntent.ChangeMode(
                    GetSupportedCurrentMode(),
                    transparentProxyEnabled,
                    mixedPort),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Closes ordinary mutation admission and returns the sole settings-destructive lease.</summary>
    internal ValueTask<MutationAdmissionLease> BeginSettingsDestructiveMutationAsync(
        CancellationToken cancellationToken)
    {
        // Materialize the App-owned controller credential before closing ordinary settings
        // admission. The Installer-owned service credential is never stored in App settings.
        _ = _settings.MihomoControllerSecret;
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

    /// <summary>Applies network settings through an already-owned destructive admission lease.</summary>
    internal async Task<NetworkTakeoverResult> ApplyNetworkSettingsAdmittedAsync(
        bool transparentProxyEnabled,
        int mixedPort,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        NetworkIntent? appliedIntent = null;
        MutationResult<NetworkTransitionResult> mutation = await _network
            .ApplyAdmittedAsync(
                () => appliedIntent = NetworkIntent.ChangeMode(
                    GetSupportedCurrentMode(),
                    transparentProxyEnabled,
                    mixedPort),
                admissionLease,
                cancellationToken)
            .ConfigureAwait(false);
        return CreateNetworkTakeoverResult(
            mutation,
            appliedIntent
                ?? throw new InvalidOperationException("The admitted network mutation did not compose an intent."));
    }

    /// <summary>Changes only the requested TUN preference, reading mode and port under mutation ownership.</summary>
    internal Task<NetworkTakeoverResult> ApplyTransparentProxyAsync(
        bool transparentProxyEnabled,
        CancellationToken cancellationToken)
    {
        return ApplyNetworkIntentAsync(
            () => NetworkIntent.ChangeMode(
                GetSupportedCurrentMode(),
                transparentProxyEnabled,
                _settings.MixedPort),
            cancellationToken);
    }

    private async Task<NetworkTakeoverResult> ApplyNetworkIntentAsync(
        Func<NetworkIntent> intentFactory,
        CancellationToken cancellationToken)
    {
        NetworkIntent? appliedIntent = null;
        MutationResult<NetworkTransitionResult> mutation = await _network
            .ApplyAsync(
                () => appliedIntent = intentFactory(),
                cancellationToken)
            .ConfigureAwait(false);
        return CreateNetworkTakeoverResult(
            mutation,
            appliedIntent
                ?? throw new InvalidOperationException("The network mutation did not compose an intent."));
    }

    private NetworkTakeoverResult CreateNetworkTakeoverResult(
        MutationResult<NetworkTransitionResult> mutation,
        NetworkIntent intent)
    {
        if (mutation.Outcome != MutationOutcome.Succeeded || mutation.Value is null)
        {
            throw new NetworkTransitionFailedException(mutation.Outcome, mutation.ErrorCode);
        }

        NetworkTransitionResult state = mutation.Value;
        bool tunRequested = intent.TransparentProxyEnabled
            && intent.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
        MihomoCoreOwner requestedOwner = intent.Mode == ClashSharpMode.Disabled
            ? MihomoCoreOwner.None
            : tunRequested
                ? MihomoCoreOwner.Service
                : MihomoCoreOwner.App;
        return new NetworkTakeoverResult(
            state.Mode,
            state.CoreRunning,
            state.SystemProxyEnabled,
            state.TransparentProxyEnabled,
            GetNetworkResultMessage(state, tunRequested),
            requestedOwner,
            tunRequested);
    }

    private async Task ApplyLaunchAtStartupAsync(
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease admissionLease = await _admissionBarrier
            .AcquireOrdinaryAsync(cancellationToken)
            .ConfigureAwait(false);
        await ApplyLaunchAtStartupCoreAsync(
            isEnabled,
            admissionLease,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyLaunchAtStartupCoreAsync(
        bool isEnabled,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        bool baseline = _settings.LaunchAtStartupEnabled;
        try
        {
            await _startupLaunch.SetEnabledAsync(isEnabled, cancellationToken).ConfigureAwait(false);
            _settings.WriteAdmitted(
                admissionLease,
                editor => editor.LaunchAtStartupEnabled = isEnabled);
        }
        catch (Exception applyFailure) when (!ExceptionGraphClassifier.IsProcessFatal(applyFailure))
        {
            try
            {
                await _startupLaunch.SetEnabledAsync(baseline, CancellationToken.None).ConfigureAwait(false);
                _settings.WriteAdmitted(
                    admissionLease,
                    editor => editor.LaunchAtStartupEnabled = baseline);
            }
            catch (Exception compensationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(compensationFailure))
            {
                throw new AggregateException(
                    "Launch-at-startup failed and its previous state could not be restored.",
                    applyFailure,
                    compensationFailure);
            }

            ExceptionDispatchInfo.Capture(applyFailure).Throw();
            throw;
        }
    }

    private async Task ApplyConnectionSamplingAsync(
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease admissionLease = await _admissionBarrier
            .AcquireOrdinaryAsync(cancellationToken)
            .ConfigureAwait(false);
        bool baseline = _settings.ConnectionSamplingEnabled;
        _settings.WriteAdmitted(
            admissionLease,
            editor => editor.ConnectionSamplingEnabled = isEnabled);
        try
        {
            await _sampling.RestartFromSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception applyFailure) when (!ExceptionGraphClassifier.IsProcessFatal(applyFailure))
        {
            _settings.WriteAdmitted(
                admissionLease,
                editor => editor.ConnectionSamplingEnabled = baseline);
            try
            {
                await _sampling.RestartFromSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception compensationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(compensationFailure))
            {
                throw new AggregateException(
                    "Connection sampling failed and its previous state could not be restored.",
                    applyFailure,
                    compensationFailure);
            }

            ExceptionDispatchInfo.Capture(applyFailure).Throw();
            throw;
        }
    }

    /// <summary>Disables an explicitly confirmed conflicting Windows proxy through durable mutation.</summary>
    public async Task DisableWindowsProxyAsync(CancellationToken cancellationToken)
    {
        MutationResult<NetworkTransitionResult> mutation = await _network
            .ApplyAsync(
                () => NetworkIntent.DisableConflictingProxy(
                    GetSupportedCurrentMode(),
                    _settings.TransparentProxyEnabled,
                    _settings.MixedPort),
                cancellationToken)
            .ConfigureAwait(false);
        if (mutation.Outcome != MutationOutcome.Succeeded)
        {
            throw new NetworkTransitionFailedException(mutation.Outcome, mutation.ErrorCode);
        }
    }

    public Task PublishProxyModeAppliedAsync(ClashSharpMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _notifications.NotifyProxyModeChanged(mode);
        if (mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover)
        {
            _triggerEvents.Publish(new TriggerRuntimeEvent(TriggerEventKind.ProxyStarted));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Drains all runtime producers and mutation admission before deleting LocalData, then requests
    /// a process restart so repositories and generation-bound services are never reused after deletion.
    /// </summary>
    internal async Task ClearAllDataAndRestartAsync(CancellationToken cancellationToken)
    {
        await _shutdown.StopAsync(cancellationToken).ConfigureAwait(false);
        Exception? clearFailure = null;
        try
        {
            // Shutdown has crossed its terminal commit point. Finish the destructive operation
            // without caller cancellation so the current process cannot continue half-cleared.
            AppDataMaintenanceService.Instance.ClearDataAfterRuntimeShutdown(
                CancellationToken.None,
                useTerminalSettingsAdmission: true);
        }
        catch (Exception exception)
        {
            clearFailure = exception;
        }
        Exception? restartFailure = null;
        try
        {
            if (!_lifecycle.RequestRestart("clear-all-data"))
            {
                restartFailure = new InvalidOperationException(
                    "Application restart could not be requested after clearing local data.");
            }
        }
        catch (Exception exception)
        {
            restartFailure = exception;
        }

        if (clearFailure is not null && restartFailure is not null)
        {
            throw new AggregateException(clearFailure, restartFailure);
        }

        if (restartFailure is not null)
        {
            ExceptionDispatchInfo.Capture(restartFailure).Throw();
        }

        if (clearFailure is not null)
        {
            ExceptionDispatchInfo.Capture(clearFailure).Throw();
        }
    }

    private static bool ParseBoolean(string value)
    {
        return bool.TryParse(value, out bool parsed) && parsed;
    }

    private string GetNetworkResultMessage(NetworkTransitionResult state, bool tunRequested)
    {
        string key = state.Mode switch
        {
            ClashSharpMode.Disabled => "NetworkTakeover.Disabled",
            ClashSharpMode.Standby => "NetworkTakeover.Standby",
            ClashSharpMode.FullTakeover when state.TransparentProxyEnabled => "NetworkTakeover.TransparentProxy.Full",
            ClashSharpMode.RuleTakeover when state.TransparentProxyEnabled => "NetworkTakeover.TransparentProxy.Rule",
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
        ClashSharpMode mode = _settings.CurrentMode;
        return Enum.IsDefined(mode) && mode != ClashSharpMode.Faulted
            ? mode
            : ClashSharpMode.Disabled;
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
