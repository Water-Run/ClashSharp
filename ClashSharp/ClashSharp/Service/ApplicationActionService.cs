using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Default shared dispatcher for non-picker application actions.</summary>
internal sealed class ApplicationActionService : IApplicationActionDispatcher
{
    private static ApplicationActionService? _instance;

    public static ApplicationActionService Instance => Volatile.Read(ref _instance)
        ?? throw new InvalidOperationException("Application actions are unavailable before primary host startup.");

    private readonly AppSettingsService _settings;
    private readonly NetworkStateCoordinator _network;
    private readonly MihomoConnectionService _connections;
    private readonly IApplicationNotificationSink _notifications;
    private readonly ITriggerRuntimeEventPublisher _triggerEvents;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;
    private readonly ApplicationLifecycleService _lifecycle;

    internal ApplicationActionService(
        AppSettingsService settings,
        NetworkStateCoordinator network,
        MihomoConnectionService connections,
        IApplicationNotificationSink notifications,
        ITriggerRuntimeEventPublisher triggerEvents,
        Action<string, string, string, string?> appendLog,
        Func<string, string> getString,
        ApplicationLifecycleService lifecycle)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _triggerEvents = triggerEvents ?? throw new ArgumentNullException(nameof(triggerEvents));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
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
                _settings.LaunchAtStartupEnabled = launchAtStartup;
                await StartupLaunchService.Instance.SetEnabledAsync(launchAtStartup).ConfigureAwait(false);
                break;
            case ApplicationActionKind.SetTransparentProxy:
                _settings.TransparentProxyEnabled = ParseBoolean(value);
                break;
            case ApplicationActionKind.SetConnectionSampling:
                _settings.ConnectionSamplingEnabled = ParseBoolean(value);
                ConnectionSamplingService.Instance.RestartFromSettings();
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
        NetworkIntent intent = NetworkIntent.ChangeMode(
            mode,
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
        MutationResult<NetworkTransitionResult> mutation = await _network
            .ApplyAsync(intent, cancellationToken)
            .ConfigureAwait(false);
        if (mutation.Outcome != MutationOutcome.Succeeded || mutation.Value is null)
        {
            throw new NetworkTransitionFailedException(mutation.Outcome, mutation.ErrorCode);
        }

        NetworkTransitionResult state = mutation.Value;
        return new NetworkTakeoverResult(
            state.Mode,
            state.CoreRunning,
            state.SystemProxyEnabled,
            state.TransparentProxyEnabled,
            GetNetworkResultMessage(state));
    }

    /// <summary>Disables an explicitly confirmed conflicting Windows proxy through durable mutation.</summary>
    public async Task DisableWindowsProxyAsync(CancellationToken cancellationToken)
    {
        NetworkIntent intent = NetworkIntent.DisableConflictingProxy(
            GetSupportedCurrentMode(),
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
        MutationResult<NetworkTransitionResult> mutation = await _network
            .ApplyAsync(intent, cancellationToken)
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

    private static bool ParseBoolean(string value)
    {
        return bool.TryParse(value, out bool parsed) && parsed;
    }

    private string GetNetworkResultMessage(NetworkTransitionResult state)
    {
        string key = state.Mode switch
        {
            ClashSharpMode.Disabled => "NetworkTakeover.Disabled",
            ClashSharpMode.Standby => "NetworkTakeover.Standby",
            ClashSharpMode.FullTakeover when state.TransparentProxyEnabled => "NetworkTakeover.TransparentProxy.Full",
            ClashSharpMode.RuleTakeover when state.TransparentProxyEnabled => "NetworkTakeover.TransparentProxy.Rule",
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
internal sealed class NetworkTransitionFailedException : InvalidOperationException
{
    public NetworkTransitionFailedException(MutationOutcome outcome, string? errorCode)
        : base($"Network transition failed with outcome '{outcome}' and code '{errorCode}'.")
    {
        Outcome = outcome;
        ErrorCode = errorCode;
    }

    public MutationOutcome Outcome { get; }

    public string? ErrorCode { get; }
}
