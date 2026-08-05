using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Ensures the mihomo runtime configuration matches the desired takeover mode.</summary>
internal interface INetworkTakeoverCoreConfiguration
{
    /// <summary>Applies one desired runtime generation through validation, promotion, readiness, and rollback.</summary>
    Task<RuntimeConfigurationTransactionResult> ApplyConfigurationAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        ICoreConfigurationRuntime runtime,
        CancellationToken cancellationToken);
}

/// <summary>Controls the owned mihomo core process.</summary>
internal interface INetworkTakeoverCore
{
    /// <summary>Gets whether the App-owned child is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Gets whether App child ownership can be classified safely.</summary>
    bool IsOwnershipKnown { get; }

    /// <summary>Restarts the core with <paramref name="configurationState"/>.</summary>
    void Restart(CoreConfigurationState configurationState);

    /// <summary>Stops the owned core process.</summary>
    void Stop();
}

/// <summary>Mutates Windows system proxy state.</summary>
internal interface INetworkTakeoverWindowsProxy
{
    /// <summary>Disables Windows system proxy.</summary>
    void DisableProxy();

    /// <summary>Enables Windows system proxy for <paramref name="proxyServer"/>.</summary>
    void EnableProxy(string proxyServer);
}

/// <summary>Reads the installed mihomo service state required by transparent proxy.</summary>
internal interface INetworkTakeoverMihomoService
{
    /// <summary>Returns the current mihomo service status.</summary>
    Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Restarts the installed service so it owns the current managed configuration.</summary>
    Task<MihomoServiceStatus> RestartAsync(
        long generation,
        string configurationHash,
        CancellationToken cancellationToken);

    /// <summary>Stops the installed service and confirms that it released core ownership.</summary>
    Task<MihomoServiceStatus> StopAsync(CancellationToken cancellationToken);
}

/// <summary>Builds loopback system proxy endpoints.</summary>
internal interface INetworkTakeoverProxyRecovery
{
    /// <summary>Builds the loopback proxy server string for <paramref name="mixedPort"/>.</summary>
    string BuildLoopbackProxyServer(int mixedPort);
}

/// <summary>Verifies the authenticated controller exposes one promoted runtime plan.</summary>
internal interface INetworkTakeoverReadiness
{
    /// <summary>Verifies the effective plan through the controller owned by the observed runtime.</summary>
    Task<bool> MatchesRuntimeConfigurationAsync(
        RuntimeConfigurationActivationPlan plan,
        long generation,
        string configurationHash,
        MihomoServiceStatus observedServiceStatus,
        CancellationToken cancellationToken);
}

/// <summary>Applies Clash# master takeover modes to the local core process and Windows system proxy.</summary>
/// <remarks>
/// Invariants: Disabled and standby modes leave Windows system proxy disabled; rule and full takeover modes prefer TUN when enabled and otherwise enable system proxy.
/// Thread safety: Public mode application is serialized through an asynchronous gate.
/// Side effects: Starts or stops mihomo and mutates Windows system proxy state through injected dependencies.
/// </remarks>
public sealed partial class NetworkTakeoverService : ICoreConfigurationRuntime
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Synchronization object guarding runtime mode transitions for this service lifetime.</summary>
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    private readonly INetworkTakeoverCoreConfiguration _configuration;

    private readonly INetworkTakeoverCore _core;

    private readonly INetworkTakeoverWindowsProxy _windowsProxy;

    private readonly INetworkTakeoverMihomoService _mihomoService;

    private readonly INetworkTakeoverProxyRecovery _proxyRecovery;

    private readonly INetworkTakeoverReadiness _readiness;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes a new network takeover service instance.</summary>
    internal NetworkTakeoverService(
        INetworkTakeoverCoreConfiguration configuration,
        INetworkTakeoverCore core,
        INetworkTakeoverWindowsProxy windowsProxy,
        INetworkTakeoverMihomoService mihomoService,
        INetworkTakeoverProxyRecovery proxyRecovery,
        INetworkTakeoverReadiness readiness,
        Func<string, string> getString)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _mihomoService = mihomoService ?? throw new ArgumentNullException(nameof(mihomoService));
        _proxyRecovery = proxyRecovery ?? throw new ArgumentNullException(nameof(proxyRecovery));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Applies one immutable planned mode without rereading mutable TUN or port settings.</summary>
    internal Task<NetworkTakeoverResult> ApplyModeAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        return ApplyRequestedModeAsync(
            mode,
            transparentProxyEnabled,
            mixedPort,
            cancellationToken);
    }

    private async Task<NetworkTakeoverResult> ApplyRequestedModeAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        ValidateModeAndPort(mode, mixedPort);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mode == ClashSharpMode.Disabled)
            {
                await ApplyRuntimeTransactionAsync(
                    mode,
                    effectiveTunEnabled: false,
                    mixedPort,
                    cancellationToken).ConfigureAwait(false);
                return BuildAppliedResult(mode, tunRequested: false, effectiveTunEnabled: false);
            }

            bool tunRequested = transparentProxyEnabled
                && mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
            if (!tunRequested)
            {
                await ApplyRuntimeTransactionAsync(
                    mode,
                    effectiveTunEnabled: false,
                    mixedPort,
                    cancellationToken).ConfigureAwait(false);
                return BuildAppliedResult(mode, tunRequested: false, effectiveTunEnabled: false);
            }

            MihomoServiceStatus serviceStatus = await _mihomoService
                .GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!serviceStatus.IsKnown)
            {
                throw CreateServiceDiagnosticFailure(
                    serviceStatus,
                    RuntimeFailureDiagnostics.ServiceUnavailable,
                    "Mihomo service ownership cannot be planned because SCM status is unknown.");
            }

            if (!serviceStatus.IsInstalled)
            {
                await ApplyRuntimeTransactionAsync(
                    mode,
                    effectiveTunEnabled: false,
                    mixedPort,
                    cancellationToken).ConfigureAwait(false);
                return BuildAppliedResult(mode, tunRequested: true, effectiveTunEnabled: false);
            }

            RuntimeConfigurationTransactionResult tunResult = await _configuration
                .ApplyConfigurationAsync(
                    mode,
                    transparentProxyEnabled: true,
                    mixedPort,
                    this,
                    cancellationToken)
                .ConfigureAwait(false);
            if (tunResult.IsApplied)
            {
                return BuildAppliedResult(mode, tunRequested: true, effectiveTunEnabled: true);
            }

            if (tunResult.Outcome != RuntimeConfigurationTransactionOutcome.RolledBack)
            {
                throw CreateRuntimeTransactionFailure(tunResult);
            }

            // Service activation failed but the previous generation was restored.
            // A separate non-TUN transaction keeps bytes, manifest, and owner aligned.
            await ApplyRuntimeTransactionAsync(
                mode,
                effectiveTunEnabled: false,
                mixedPort,
                cancellationToken).ConfigureAwait(false);
            return BuildAppliedResult(mode, tunRequested: true, effectiveTunEnabled: false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task<NetworkTakeoverResult> ApplyPreparedConfigurationCoreAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CoreConfigurationState preparedConfiguration,
        long generation,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return mode switch
        {
            ClashSharpMode.Disabled => await ApplyDisabledModeAsync(cancellationToken).ConfigureAwait(false),
            ClashSharpMode.Standby => await ApplyStandbyModeAsync(
                mixedPort,
                preparedConfiguration,
                cancellationToken).ConfigureAwait(false),
            ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover => await ApplyTakeoverModeAsync(
                mode,
                transparentProxyEnabled,
                mixedPort,
                preparedConfiguration,
                generation,
                configurationHash,
                cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Clash# runtime mode."),
        };
    }

    private async Task ApplyRuntimeTransactionAsync(
        ClashSharpMode mode,
        bool effectiveTunEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        RuntimeConfigurationTransactionResult result = await _configuration
            .ApplyConfigurationAsync(
                mode,
                effectiveTunEnabled,
                mixedPort,
                this,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsApplied)
        {
            throw CreateRuntimeTransactionFailure(result);
        }
    }

    private NetworkTakeoverResult BuildAppliedResult(
        ClashSharpMode mode,
        bool tunRequested,
        bool effectiveTunEnabled)
    {
        string message = mode switch
        {
            ClashSharpMode.Disabled => GetString("NetworkTakeover.Disabled"),
            ClashSharpMode.Standby => GetString("NetworkTakeover.Standby"),
            ClashSharpMode.FullTakeover when effectiveTunEnabled => BuildTransparentProxyMessage(mode),
            ClashSharpMode.RuleTakeover when effectiveTunEnabled => BuildTransparentProxyMessage(mode),
            ClashSharpMode.FullTakeover when tunRequested => BuildTransparentProxyServiceMissingMessage(mode),
            ClashSharpMode.RuleTakeover when tunRequested => BuildTransparentProxyServiceMissingMessage(mode),
            ClashSharpMode.FullTakeover => BuildSystemProxyMessage(mode),
            ClashSharpMode.RuleTakeover => BuildSystemProxyMessage(mode),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported applied runtime mode."),
        };
        bool coreRunning = mode != ClashSharpMode.Disabled;
        bool systemProxyEnabled = mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover
            && !effectiveTunEnabled;
        MihomoCoreOwner requestedOwner = !coreRunning
            ? MihomoCoreOwner.None
            : tunRequested
                ? MihomoCoreOwner.Service
                : MihomoCoreOwner.App;
        return new NetworkTakeoverResult(
            mode,
            coreRunning,
            systemProxyEnabled,
            effectiveTunEnabled,
            message,
            requestedOwner,
            tunRequested);
    }

    private static InvalidOperationException CreateRuntimeTransactionFailure(
        RuntimeConfigurationTransactionResult result)
    {
        Exception? cause = result.RollbackFailure is not null && result.Failure is not null
            ? new AggregateException(result.RollbackFailure, result.Failure)
            : result.RollbackFailure ?? result.Failure;
        return new InvalidOperationException(
            $"Runtime configuration transaction ended with '{result.Outcome}'.",
            cause);
    }

    private static void ValidateModeAndPort(ClashSharpMode mode, int mixedPort)
    {
        if (!Enum.IsDefined(mode) || mode == ClashSharpMode.Faulted)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Clash# runtime mode.");
        }

        if (mixedPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort), "Port must be in the range [1, 65535].");
        }
    }

    /// <summary>Applies disabled mode by stopping the core and disabling Windows proxy.</summary>
    /// <returns>The resulting disabled-mode takeover state.</returns>
    private async Task<NetworkTakeoverResult> ApplyDisabledModeAsync(CancellationToken cancellationToken)
    {
        // Release owned WinINet state before stopping its listener. If SCM
        // observation is inconclusive, no second mihomo owner can be started.
        _windowsProxy.DisableProxy();
        // transition fails with no possibility of starting a second mihomo owner.
        _core.Stop();
        await EnsureServiceStoppedAsync(cancellationToken).ConfigureAwait(false);
        return new NetworkTakeoverResult(ClashSharpMode.Disabled, false, false, false, GetString("NetworkTakeover.Disabled"));
    }

    /// <summary>Applies standby mode by starting the core and disabling Windows proxy.</summary>
    /// <returns>The resulting standby-mode takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private async Task<NetworkTakeoverResult> ApplyStandbyModeAsync(
        int mixedPort,
        CoreConfigurationState preparedConfiguration,
        CancellationToken cancellationToken)
    {
        _windowsProxy.DisableProxy();
        await TransitionToAppOwnedCoreAsync(
            ClashSharpMode.Standby,
            mixedPort,
            preparedConfiguration,
            cancellationToken).ConfigureAwait(false);
        return new NetworkTakeoverResult(
            ClashSharpMode.Standby,
            true,
            false,
            false,
            GetString("NetworkTakeover.Standby"),
            MihomoCoreOwner.App,
            TunRequested: false);
    }

    /// <summary>Applies a takeover mode through TUN when enabled, otherwise through Windows system proxy.</summary>
    /// <param name="mode">Takeover mode that should route traffic through mihomo.</param>
    /// <returns>The resulting takeover state.</returns>
    /// <exception cref="FileNotFoundException">Required core files are missing.</exception>
    /// <exception cref="InvalidOperationException">Core startup or Windows proxy registry access fails.</exception>
    /// <exception cref="Win32Exception">Windows rejects the proxy change notification.</exception>
    private async Task<NetworkTakeoverResult> ApplyTakeoverModeAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CoreConfigurationState preparedConfiguration,
        long generation,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        if (!transparentProxyEnabled)
        {
            return await ApplySystemProxyTakeoverModeAsync(
                mode,
                mixedPort,
                BuildSystemProxyMessage(mode),
                tunRequested: false,
                preparedConfiguration,
                cancellationToken).ConfigureAwait(false);
        }

        MihomoServiceStatus serviceStatus = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!serviceStatus.IsKnown)
        {
            throw CreateServiceDiagnosticFailure(
                serviceStatus,
                RuntimeFailureDiagnostics.ServiceUnavailable,
                "Mihomo service ownership cannot be changed because SCM status is unknown.");
        }

        if (!serviceStatus.IsInstalled)
        {
            throw CreateServiceDiagnosticFailure(
                serviceStatus,
                MihomoServiceIpcEndpoint.AssociationMissingCode,
                "The promoted TUN generation cannot be activated because the mihomo service is not installed.");
        }

        // App and service are mutually exclusive owners. Release the App-owned
        // listener before asking SCM to acquire the already-promoted generation.
        _windowsProxy.DisableProxy();
        _core.Stop();
        MihomoServiceStatus restartedStatus = await _mihomoService
            .RestartAsync(generation, configurationHash, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!restartedStatus.IsKnown
            || !restartedStatus.IsReady
            || restartedStatus.ServiceSessionId is not Guid serviceSessionId
            || serviceSessionId == Guid.Empty
            || restartedStatus.ActiveGeneration != generation
            || !StringComparer.Ordinal.Equals(
                restartedStatus.ActiveConfigurationHash,
                configurationHash))
        {
            // A failed service acquisition is compensated to a confirmed stopped
            // service before the App-owned system-proxy fallback may start.
            await EnsureServiceStoppedAsync(cancellationToken).ConfigureAwait(false);
            throw CreateServiceDiagnosticFailure(
                restartedStatus,
                RuntimeFailureDiagnostics.ControllerUnavailable,
                "The promoted TUN generation could not acquire service runtime ownership.");
        }

        return new NetworkTakeoverResult(
            mode,
            true,
            false,
            true,
            BuildTransparentProxyMessage(mode),
            MihomoCoreOwner.Service,
            TunRequested: true);
    }

    /// <summary>Applies a takeover mode by starting the core and enabling Windows proxy.</summary>
    /// <param name="mode">Takeover mode that should enable Windows proxy.</param>
    /// <param name="message">Human-readable outcome message. Must not be null.</param>
    /// <returns>The resulting takeover state.</returns>
    private async Task<NetworkTakeoverResult> ApplySystemProxyTakeoverModeAsync(
        ClashSharpMode mode,
        int mixedPort,
        string message,
        bool tunRequested,
        CoreConfigurationState preparedConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A port/profile restart must not leave WinINet pointing at the old
        // App listener. Commit re-enables it only after controller readiness.
        _windowsProxy.DisableProxy();
        await TransitionToAppOwnedCoreAsync(
            mode,
            mixedPort,
            preparedConfiguration,
            cancellationToken).ConfigureAwait(false);
        return new NetworkTakeoverResult(
            mode,
            true,
            true,
            false,
            message,
            tunRequested ? MihomoCoreOwner.Service : MihomoCoreOwner.App,
            tunRequested);
    }

    /// <summary>Hands core ownership from the Windows service to the App child.</summary>
    private async Task TransitionToAppOwnedCoreAsync(
        ClashSharpMode mode,
        int mixedPort,
        CoreConfigurationState preparedConfiguration,
        CancellationToken cancellationToken)
    {
        // Stopping the App child first also repairs a legacy double-owner state.
        _core.Stop();
        await EnsureServiceStoppedAsync(cancellationToken).ConfigureAwait(false);
        _core.Restart(preparedConfiguration);
    }

    /// <summary>Stops the service-owned child and fails closed without authenticated release proof.</summary>
    private async Task EnsureServiceStoppedAsync(CancellationToken cancellationToken)
    {
        MihomoServiceStatus status = await _mihomoService
            .StopAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!status.HasReleasedChildOwnership)
        {
            throw CreateServiceDiagnosticFailure(
                status,
                RuntimeFailureDiagnostics.ServiceUnavailable,
                "Mihomo service ownership could not be released safely.");
        }
    }

    private static StableRuntimeDiagnosticException CreateServiceDiagnosticFailure(
        MihomoServiceStatus status,
        string fallbackCode,
        string message)
    {
        string code = new[]
        {
            status.CleanupFailureCode,
            status.IpcFailureCode,
            status.ProvisioningFailureCode,
        }.FirstOrDefault(RuntimeFailureDiagnostics.IsStableCode) ?? fallbackCode;
        return new StableRuntimeDiagnosticException(code, message);
    }

    async Task ICoreConfigurationRuntime.ApplyAsync(
        CoreConfigurationState configuration,
        long generation,
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken)
    {
        string configurationHash = await ComputeConfigurationHashAsync(
            configuration.ConfigPath,
            cancellationToken).ConfigureAwait(false);
        NetworkTakeoverResult result = await ApplyPreparedConfigurationCoreAsync(
            plan.Mode,
            plan.TunEnabled,
            plan.MixedPort,
            configuration,
            generation,
            configurationHash,
            cancellationToken).ConfigureAwait(false);
        MihomoCoreOwner expectedOwner = plan.Mode == ClashSharpMode.Disabled
            ? MihomoCoreOwner.None
            : plan.TunEnabled
                ? MihomoCoreOwner.Service
                : MihomoCoreOwner.App;
        if (result.EffectiveOwner != expectedOwner || result.TunEffective != plan.TunEnabled)
        {
            throw new StableRuntimeDiagnosticException(
                RuntimeFailureDiagnostics.ControllerUnavailable,
                "The promoted runtime generation did not acquire its planned owner.");
        }
    }

    async Task<bool> ICoreConfigurationRuntime.WaitUntilReadyAsync(
        long generation,
        string configurationHash,
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        if (!MihomoServiceIpcProtocol.IsCanonicalSha256(configurationHash))
        {
            throw new ArgumentException(
                "The runtime configuration hash must be a lowercase SHA-256 value.",
                nameof(configurationHash));
        }

        if (plan.Mode == ClashSharpMode.Disabled)
        {
            MihomoServiceStatus disabledServiceStatus = await _mihomoService
                .GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            return !_core.IsRunning
                && _core.IsOwnershipKnown
                && disabledServiceStatus.HasReleasedChildOwnership;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ReadinessTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                MihomoServiceStatus serviceStatus = await _mihomoService
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                bool ownerMatches = plan.TunEnabled
                    ? !_core.IsRunning
                        && _core.IsOwnershipKnown
                        && serviceStatus.IsKnown
                        && serviceStatus.IsReady
                        && serviceStatus.ServiceSessionId is Guid serviceSessionId
                        && serviceSessionId != Guid.Empty
                        && serviceStatus.ActiveGeneration == generation
                        && StringComparer.Ordinal.Equals(
                            serviceStatus.ActiveConfigurationHash,
                            configurationHash)
                    : _core.IsRunning
                        && _core.IsOwnershipKnown
                        && serviceStatus.HasReleasedChildOwnership;
                if (ownerMatches
                    && await _readiness
                        .MatchesRuntimeConfigurationAsync(
                            plan,
                            generation,
                            configurationHash,
                            serviceStatus,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // HttpClient can time out an individual readiness probe without
                // cancelling the transition's bounded readiness window.
            }
            catch (Exception exception) when (exception is
                HttpRequestException or
                JsonException or
                IOException or
                InvalidOperationException)
            {
            }

            await Task.Delay(ReadinessPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<string> ComputeConfigurationHashAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    Task ICoreConfigurationRuntime.CommitAsync(
        long generation,
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover
            && !plan.TunEnabled)
        {
            _windowsProxy.EnableProxy(_proxyRecovery.BuildLoopbackProxyServer(plan.MixedPort));
        }
        else
        {
            _windowsProxy.DisableProxy();
        }

        return Task.CompletedTask;
    }

    async Task ICoreConfigurationRuntime.DeactivateAsync(CancellationToken cancellationToken)
    {
        _windowsProxy.DisableProxy();
        _core.Stop();
        await EnsureServiceStoppedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds user-facing message for system-proxy takeover.</summary>
    private string BuildSystemProxyMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? GetString("NetworkTakeover.SystemProxy.Full")
            : GetString("NetworkTakeover.SystemProxy.Rule");
    }

    /// <summary>Builds user-facing message for transparent-proxy takeover.</summary>
    private string BuildTransparentProxyMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? GetString("NetworkTakeover.TransparentProxy.Full")
            : GetString("NetworkTakeover.TransparentProxy.Rule");
    }

    /// <summary>Builds user-facing message when transparent proxy is unavailable because the service is missing.</summary>
    private string BuildTransparentProxyServiceMissingMessage(ClashSharpMode mode)
    {
        return mode == ClashSharpMode.FullTakeover
            ? GetString("NetworkTakeover.TransparentProxyServiceMissing.Full")
            : GetString("NetworkTakeover.TransparentProxyServiceMissing.Rule");
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
