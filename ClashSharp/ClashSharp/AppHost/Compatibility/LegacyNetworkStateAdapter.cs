using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>
/// Adapts the existing synchronous network services to the staged durable network contract.
/// This is the sole compatibility boundary allowed to call the legacy takeover implementation.
/// </summary>
internal sealed class LegacyNetworkStateAdapter : INetworkStateAdapter, INetworkStateObserver
{
    private readonly AppSettingsService _settings;
    private readonly NetworkTakeoverService _takeover;
    private readonly WindowsProxyService _windowsProxy;
    private readonly MihomoCoreService _core;
    private readonly CoreConfigurationService _configuration;
    private readonly MihomoServiceManager _mihomoService;
    private readonly ProxyRecoveryService _proxyRecovery;

    public LegacyNetworkStateAdapter(
        AppSettingsService settings,
        NetworkTakeoverService takeover,
        WindowsProxyService windowsProxy,
        MihomoCoreService core,
        CoreConfigurationService configuration,
        MihomoServiceManager mihomoService,
        ProxyRecoveryService proxyRecovery)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
        _windowsProxy = windowsProxy ?? throw new ArgumentNullException(nameof(windowsProxy));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _mihomoService = mihomoService ?? throw new ArgumentNullException(nameof(mihomoService));
        _proxyRecovery = proxyRecovery ?? throw new ArgumentNullException(nameof(proxyRecovery));
    }

    public async Task<NetworkPlan> PlanAsync(NetworkIntent intent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MihomoServiceStatus serviceStatus = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        ObservedNetworkState observed = Observe(serviceStatus);
        if (!observed.Snapshot.IsKnown)
        {
            throw CreateServiceObservationFailure(
                serviceStatus,
                "The current network baseline cannot be classified safely.");
        }

        bool useTransparentProxy = ShouldUseTransparentProxy(intent, serviceStatus);
        NetworkStateSnapshot desired = BuildDesired(intent, observed, useTransparentProxy);
        string desiredProxyServer = DetermineDesiredProxyServer(intent, observed, desired);
        string baselineHash = ComputeAggregateHash(
            observed.Snapshot.StateHash,
            _settings.CurrentMode,
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
        string desiredHash = ComputeAggregateHash(
            desired.StateHash,
            intent.Kind == NetworkIntentKind.ModeTransition ? intent.Mode : _settings.CurrentMode,
            intent.Kind == NetworkIntentKind.ModeTransition
                ? intent.TransparentProxyEnabled
                : _settings.TransparentProxyEnabled,
            intent.Kind == NetworkIntentKind.ModeTransition
                ? intent.MixedPort
                : _settings.MixedPort);
        string compensationData = LegacyNetworkPlanPersistence.Serialize(
            intent,
            observed.Snapshot,
            desired,
            baselineHash,
            desiredHash,
            observed.ProxyServer,
            desiredProxyServer,
            _settings.CurrentMode,
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
        return new NetworkPlan(
            intent,
            observed.Snapshot,
            desired,
            baselineHash,
            desiredHash,
            compensationData);
    }

    public async Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MihomoServiceStatus serviceStatus = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        return Observe(serviceStatus).Snapshot;
    }

    public Task<NetworkPlan> RestorePlanAsync(MutationJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LegacyNetworkPlanPersistence.PersistedNetworkPlan persisted = LegacyNetworkPlanPersistence.Restore(journal);
        string compensationData = journal.Steps.Single(
            static step => string.Equals(step.Name, "network-state", StringComparison.Ordinal)).CompensationData!;
        return Task.FromResult(persisted.ToPlan(compensationData));
    }

    public async Task ValidateAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MihomoServiceStatus serviceStatus = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        ObservedNetworkState current = Observe(serviceStatus);
        string currentAggregateHash = ComputeAggregateHash(
            current.Snapshot.StateHash,
            _settings.CurrentMode,
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
        if (!current.Snapshot.IsKnown)
        {
            throw CreateServiceObservationFailure(
                serviceStatus,
                "The current network baseline cannot be classified safely.");
        }

        if (!string.Equals(current.Snapshot.StateHash, plan.Baseline.StateHash, StringComparison.Ordinal)
            || !string.Equals(currentAggregateHash, plan.BaselineHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The network baseline changed after planning.");
        }

    }

    private static StableRuntimeDiagnosticException CreateServiceObservationFailure(
        MihomoServiceStatus status,
        string message)
    {
        string code = new[]
        {
            status.CleanupFailureCode,
            status.IpcFailureCode,
            status.ProvisioningFailureCode,
        }.FirstOrDefault(RuntimeFailureDiagnostics.IsStableCode)
            ?? RuntimeFailureDiagnostics.ServiceUnavailable;
        return new StableRuntimeDiagnosticException(code, message);
    }

    public Task StageAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        switch (plan.Intent.Kind)
        {
            case NetworkIntentKind.ModeTransition:
            case NetworkIntentKind.Shutdown:
                await _takeover.ApplyModeAsync(
                    plan.Intent.Mode,
                    plan.Intent.TransparentProxyEnabled,
                    plan.Intent.MixedPort,
                    cancellationToken).ConfigureAwait(false);
                break;
            case NetworkIntentKind.StartupProxyRecovery:
            case NetworkIntentKind.ProxyConflictRepair:
                await RunLegacyAsync(() => ApplyProxyRecovery(plan), cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(plan), plan.Intent.Kind, "Unsupported network intent.");
        }
    }

    public async Task<NetworkStateSnapshot> ProbeAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MihomoServiceStatus serviceStatus = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        return Observe(serviceStatus).Snapshot;
    }

    public async Task CompensateAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        LegacyNetworkPlanPersistence.PersistedNetworkPlan persisted =
            LegacyNetworkPlanPersistence.Deserialize(plan.CompensationData);
        if (plan.Intent.Kind is not (NetworkIntentKind.StartupProxyRecovery
            or NetworkIntentKind.ProxyConflictRepair))
        {
            if (persisted.Baseline.CoreRunning)
            {
                await _takeover.ApplyModeAsync(
                    persisted.Baseline.Mode,
                    persisted.Baseline.TransparentProxyEnabled,
                    persisted.Baseline.MixedPort,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _takeover.ApplyModeAsync(
                    ClashSharpMode.Disabled,
                    transparentProxyEnabled: false,
                    persisted.Baseline.MixedPort,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await RunLegacyAsync(
            () => RestoreProxy(persisted.Baseline.SystemProxyEnabled, persisted.BaselineProxyServer),
            cancellationToken).ConfigureAwait(false);
    }

    public Task ActivateAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task CleanupAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ApplyProxyRecovery(NetworkPlan plan)
    {
        switch (plan.Intent.Kind)
        {
            case NetworkIntentKind.StartupProxyRecovery:
            case NetworkIntentKind.ProxyConflictRepair:
                if (plan.Desired.SystemProxyEnabled)
                {
                    string proxyServer = LegacyNetworkPlanPersistence.Deserialize(plan.CompensationData).DesiredProxyServer;
                    _windowsProxy.EnableProxy(proxyServer);
                }
                else
                {
                    _windowsProxy.DisableProxy();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(plan), plan.Intent.Kind, "Unsupported network intent.");
        }
    }

    private NetworkStateSnapshot BuildDesired(
        NetworkIntent intent,
        ObservedNetworkState observed,
        bool useTransparentProxy)
    {
        if (intent.Kind is NetworkIntentKind.StartupProxyRecovery
            or NetworkIntentKind.ProxyConflictRepair)
        {
            bool shouldDisableProxy = intent.Kind == NetworkIntentKind.ProxyConflictRepair
                || (_settings.CheckStaleProxyOnStartup
                    && _proxyRecovery.IsStaleClashProxy(
                        new WindowsProxyState(observed.Snapshot.SystemProxyEnabled, observed.ProxyServer),
                        intent.MixedPort));
            bool proxyEnabled = observed.Snapshot.SystemProxyEnabled && !shouldDisableProxy;
            return CreateSnapshot(
                observed.Snapshot.Mode,
                observed.Snapshot.CoreRunning,
                proxyEnabled,
                observed.Snapshot.TransparentProxyEnabled,
                observed.Snapshot.MixedPort,
                observed.ProxyServer,
                isKnown: true);
        }

        bool coreRunning = intent.Mode != ClashSharpMode.Disabled;
        bool transparentProxy = useTransparentProxy;
        bool systemProxy = intent.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover
            && !transparentProxy;
        int effectivePort = intent.MixedPort;
        string proxyServer = systemProxy
            ? _proxyRecovery.BuildLoopbackProxyServer(intent.MixedPort)
            : observed.ProxyServer;
        return CreateSnapshot(
            intent.Mode,
            coreRunning,
            systemProxy,
            transparentProxy,
            effectivePort,
            proxyServer,
            isKnown: true);
    }

    private static bool ShouldUseTransparentProxy(
        NetworkIntent intent,
        MihomoServiceStatus serviceStatus)
    {
        if (!intent.TransparentProxyEnabled
            || intent.Mode is not (ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover))
        {
            return false;
        }

        // Installed-but-stopped is available: NetworkTakeoverService starts it as
        // part of the mutually exclusive App-to-service ownership handoff.
        return serviceStatus.IsKnown && serviceStatus.IsInstalled;
    }

    private static string DetermineDesiredProxyServer(
        NetworkIntent intent,
        ObservedNetworkState observed,
        NetworkStateSnapshot desired)
    {
        if (intent.Kind is NetworkIntentKind.StartupProxyRecovery or NetworkIntentKind.ProxyConflictRepair
            || !desired.SystemProxyEnabled)
        {
            return observed.ProxyServer;
        }

        return $"127.0.0.1:{intent.MixedPort.ToString(CultureInfo.InvariantCulture)}";
    }

    private ObservedNetworkState Observe(MihomoServiceStatus serviceStatus)
    {
        WindowsProxyState proxy = _windowsProxy.GetCurrentState();
        bool appCoreRunning = _core.IsRunning;
        bool appCoreOwnershipKnown = !_core.HasOwnershipFault;
        bool serviceCoreRunning = serviceStatus.IsKnown
            && serviceStatus.IsInstalled
            && serviceStatus.IsRunning;
        bool coreRunning = appCoreRunning || serviceCoreRunning;
        CoreConfigurationObservation configuration = ObserveConfiguration(coreRunning);
        bool singleOwner = !(appCoreRunning && serviceCoreRunning);
        bool ownerMatchesConfiguration = !coreRunning
            || (appCoreRunning && !configuration.TransparentProxyEnabled)
            || (serviceCoreRunning
                && configuration.TransparentProxyEnabled
                && configuration.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover);
        bool isKnown = serviceStatus.IsKnown
            && appCoreOwnershipKnown
            && configuration.IsKnown
            && singleOwner
            && ownerMatchesConfiguration;
        NetworkStateSnapshot snapshot = CreateSnapshot(
            isKnown ? configuration.Mode : ClashSharpMode.Faulted,
            coreRunning,
            proxy.IsEnabled,
            isKnown && serviceCoreRunning && configuration.TransparentProxyEnabled,
            configuration.MixedPort,
            proxy.ProxyServer,
            isKnown);
        return new ObservedNetworkState(snapshot, proxy.ProxyServer);
    }

    private CoreConfigurationObservation ObserveConfiguration(bool coreRunning)
    {
        RuntimeConfigurationIntegrityObservation integrity =
            _configuration.ObserveRuntimeConfigurationIntegrity();
        if (!integrity.IsKnown)
        {
            return CoreConfigurationObservation.Unknown(_settings.MixedPort);
        }

        RuntimeConfigurationActivationPlan? plan = integrity.AppliedPlan;
        if (plan is null)
        {
            return coreRunning
                ? CoreConfigurationObservation.Unknown(_settings.MixedPort)
                : new CoreConfigurationObservation(
                    ClashSharpMode.Disabled,
                    _settings.MixedPort,
                    TransparentProxyEnabled: false,
                    IsKnown: true);
        }

        bool planRequiresOwner = plan.Mode != ClashSharpMode.Disabled;
        if (planRequiresOwner != coreRunning
            || !StringComparer.Ordinal.Equals(plan.ProfileId, _settings.ActiveProfileId))
        {
            return CoreConfigurationObservation.Unknown(_settings.MixedPort);
        }

        return new CoreConfigurationObservation(
            plan.Mode,
            plan.MixedPort,
            plan.TunEnabled,
            IsKnown: true);
    }

    private static NetworkStateSnapshot CreateSnapshot(
        ClashSharpMode mode,
        bool coreRunning,
        bool systemProxyEnabled,
        bool transparentProxyEnabled,
        int mixedPort,
        string proxyServer,
        bool isKnown)
    {
        string canonical = string.Join(
            "|",
            ((int)mode).ToString(CultureInfo.InvariantCulture),
            coreRunning ? "1" : "0",
            systemProxyEnabled ? "1" : "0",
            transparentProxyEnabled ? "1" : "0",
            mixedPort.ToString(CultureInfo.InvariantCulture),
            proxyServer.Trim());
        return new NetworkStateSnapshot(
            mode,
            coreRunning,
            systemProxyEnabled,
            transparentProxyEnabled,
            mixedPort,
            ComputeHash(canonical),
            isKnown);
    }

    private static string ComputeAggregateHash(
        string externalHash,
        ClashSharpMode durableMode,
        bool transparentProxyEnabled,
        int mixedPort)
    {
        return ComputeHash(string.Join(
            "|",
            externalHash,
            ((int)durableMode).ToString(CultureInfo.InvariantCulture),
            transparentProxyEnabled ? "1" : "0",
            mixedPort.ToString(CultureInfo.InvariantCulture)));
    }

    private static string ComputeHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private void RestoreProxy(bool enabled, string proxyServer)
    {
        if (enabled)
        {
            _windowsProxy.EnableProxy(proxyServer);
        }
        else
        {
            _windowsProxy.DisableProxy();
        }
    }

    private static Task RunLegacyAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(action, cancellationToken);
    }

    private sealed record ObservedNetworkState(NetworkStateSnapshot Snapshot, string ProxyServer);

    private sealed record CoreConfigurationObservation(
        ClashSharpMode Mode,
        int MixedPort,
        bool TransparentProxyEnabled,
        bool IsKnown)
    {
        public static CoreConfigurationObservation Unknown(int mixedPort)
        {
            return new CoreConfigurationObservation(
                ClashSharpMode.Faulted,
                mixedPort,
                TransparentProxyEnabled: false,
                IsKnown: false);
        }
    }
}
