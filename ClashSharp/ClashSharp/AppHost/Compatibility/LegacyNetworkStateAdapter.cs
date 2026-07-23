using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        ObservedNetworkState observed = Observe();
        if (!observed.Snapshot.IsKnown)
        {
            throw new InvalidOperationException("The current network baseline cannot be classified safely.");
        }

        bool useTransparentProxy = await ShouldUseTransparentProxyAsync(intent, cancellationToken).ConfigureAwait(false);
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
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
        string compensationData = LegacyNetworkPlanPersistence.Serialize(
            intent,
            observed.Snapshot,
            desired,
            baselineHash,
            desiredHash,
            observed.ProxyServer,
            desiredProxyServer,
            _settings.CurrentMode);
        return new NetworkPlan(
            intent,
            observed.Snapshot,
            desired,
            baselineHash,
            desiredHash,
            compensationData);
    }

    public Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Observe().Snapshot);
    }

    public Task<NetworkPlan> RestorePlanAsync(MutationJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LegacyNetworkPlanPersistence.PersistedNetworkPlan persisted = LegacyNetworkPlanPersistence.Restore(journal);
        string compensationData = journal.Steps.Single(
            static step => string.Equals(step.Name, "network-state", StringComparison.Ordinal)).CompensationData!;
        return Task.FromResult(persisted.ToPlan(compensationData));
    }

    public Task ValidateAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObservedNetworkState current = Observe();
        string currentAggregateHash = ComputeAggregateHash(
            current.Snapshot.StateHash,
            _settings.CurrentMode,
            _settings.TransparentProxyEnabled,
            _settings.MixedPort);
        if (!current.Snapshot.IsKnown
            || !string.Equals(current.Snapshot.StateHash, plan.Baseline.StateHash, StringComparison.Ordinal)
            || !string.Equals(currentAggregateHash, plan.BaselineHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The network baseline changed after planning.");
        }

        return Task.CompletedTask;
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

    public Task<NetworkStateSnapshot> ProbeAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Observe().Snapshot);
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
                await RunLegacyAsync(_core.Stop, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> ShouldUseTransparentProxyAsync(
        NetworkIntent intent,
        CancellationToken cancellationToken)
    {
        if (!intent.TransparentProxyEnabled
            || intent.Mode is not (ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover))
        {
            return false;
        }

        MihomoServiceStatus status = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        return status.IsInstalled && status.IsRunning;
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

    private ObservedNetworkState Observe()
    {
        WindowsProxyState proxy = _windowsProxy.GetCurrentState();
        bool coreRunning = _core.IsRunning;
        CoreConfigurationObservation configuration = ObserveConfiguration(coreRunning);
        NetworkStateSnapshot snapshot = CreateSnapshot(
            configuration.Mode,
            coreRunning,
            proxy.IsEnabled,
            coreRunning && configuration.TransparentProxyEnabled,
            configuration.MixedPort,
            proxy.ProxyServer,
            configuration.IsKnown);
        return new ObservedNetworkState(snapshot, proxy.ProxyServer);
    }

    private CoreConfigurationObservation ObserveConfiguration(bool coreRunning)
    {
        CoreConfigurationState state = _configuration.GetState();
        if (!coreRunning)
        {
            return new CoreConfigurationObservation(
                ClashSharpMode.Disabled,
                _settings.MixedPort,
                TransparentProxyEnabled: false,
                IsKnown: true);
        }

        if (!state.Exists)
        {
            return CoreConfigurationObservation.Unknown(_settings.MixedPort);
        }

        try
        {
            string[] lines = File.ReadAllLines(state.ConfigPath);
            string? modeValue = ReadTopLevelValue(lines, "mode");
            string? portValue = ReadTopLevelValue(lines, "mixed-port");
            ClashSharpMode mode = modeValue?.Trim().ToLowerInvariant() switch
            {
                "direct" => ClashSharpMode.Standby,
                "rule" => ClashSharpMode.RuleTakeover,
                "global" => ClashSharpMode.FullTakeover,
                _ => ClashSharpMode.Faulted,
            };
            bool validPort = int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out int mixedPort)
                && mixedPort is >= 1 and <= 65535;
            bool tunEnabled = HasEnabledTunSection(lines);
            return new CoreConfigurationObservation(
                mode,
                validPort ? mixedPort : _settings.MixedPort,
                tunEnabled,
                mode != ClashSharpMode.Faulted && validPort);
        }
        catch (IOException)
        {
            return CoreConfigurationObservation.Unknown(_settings.MixedPort);
        }
        catch (UnauthorizedAccessException)
        {
            return CoreConfigurationObservation.Unknown(_settings.MixedPort);
        }
    }

    private static string? ReadTopLevelValue(IEnumerable<string> lines, string key)
    {
        string prefix = key + ":";
        string? line = lines.FirstOrDefault(candidate =>
            candidate.Length == candidate.TrimStart().Length
            && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[prefix.Length..].Trim();
    }

    private static bool HasEnabledTunSection(IReadOnlyList<string> lines)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            if (!string.Equals(lines[index].Trim(), "tun:", StringComparison.OrdinalIgnoreCase)
                || lines[index].Length != lines[index].TrimStart().Length)
            {
                continue;
            }

            for (int child = index + 1; child < lines.Count && lines[child].Length != lines[child].TrimStart().Length; child++)
            {
                if (string.Equals(lines[child].Trim(), "enable: true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
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
