using System.Security.Cryptography;
using ClashSharp.Diagnostics;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for network takeover mode application.</summary>
public sealed class NetworkTakeoverServiceTests
{
    /// <summary>Verifies TUN hands core ownership from the App child to the installed service.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTransparentProxyEnabledAndServiceInstalled_UsesTransparentProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, true, "Installed"));
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.FullTakeover,
            true,
            19090,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.FullTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.False(result.SystemProxyEnabled);
        Assert.True(result.TransparentProxyEnabled);
        Assert.Equal(MihomoCoreOwner.Service, result.RequestedOwner);
        Assert.Equal(MihomoCoreOwner.Service, result.EffectiveOwner);
        Assert.True(result.TunRequested);
        Assert.True(result.TunEffective);
        Assert.Equal("transparent full", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.FullTakeover, true, 19090)], configuration.Requests);
        Assert.Empty(core.RestartedStates);
        Assert.True(core.Stopped);
        Assert.Equal(1, serviceStatus.RestartCount);
        Assert.Equal(1, serviceStatus.LastRestartGeneration);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configuration.State.ConfigPath)))
                .ToLowerInvariant(),
            serviceStatus.LastRestartConfigurationHash);
        Assert.Equal(0, serviceStatus.StopCount);
        Assert.Equal(2, windowsProxy.DisableCount);
        Assert.Empty(windowsProxy.EnabledServers);
    }

    /// <summary>Verifies an installed stopped service is acquired instead of being treated as unavailable.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTransparentProxyServiceInstalledButStopped_StartsServiceOwner()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, false, "Stopped"));
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.FullTakeover,
            true,
            10002,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.FullTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.False(result.SystemProxyEnabled);
        Assert.True(result.TransparentProxyEnabled);
        Assert.Equal("transparent full", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.FullTakeover, true, 10002)], configuration.Requests);
        Assert.Empty(core.RestartedStates);
        Assert.True(core.Stopped);
        Assert.Equal(1, serviceStatus.RestartCount);
        Assert.Empty(windowsProxy.EnabledServers);
        Assert.Equal(2, windowsProxy.DisableCount);
    }

    /// <summary>Verifies missing transparent proxy service falls back to system proxy.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTransparentProxyEnabledButServiceMissing_FallsBackToSystemProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(false, false, "Missing"));
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            true,
            10001,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.RuleTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal(MihomoCoreOwner.Service, result.RequestedOwner);
        Assert.Equal(MihomoCoreOwner.App, result.EffectiveOwner);
        Assert.True(result.TunRequested);
        Assert.False(result.TunEffective);
        Assert.Equal("missing rule", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.RuleTakeover, false, 10001)], configuration.Requests);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.True(core.Stopped);
        Assert.Equal(1, serviceStatus.StopCount);
        Assert.Equal(["127.0.0.1:10001"], windowsProxy.EnabledServers);
        Assert.Equal(1, windowsProxy.DisableCount);
    }

    /// <summary>Verifies disabled mode stops mihomo and disables Windows system proxy through dependencies.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenDisabled_StopsCoreAndDisablesSystemProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, true, "Installed"));
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.Disabled,
            false,
            7890,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.Disabled, result.Mode);
        Assert.False(result.CoreRunning);
        Assert.False(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal(MihomoCoreOwner.None, result.RequestedOwner);
        Assert.Equal(MihomoCoreOwner.None, result.EffectiveOwner);
        Assert.Equal("disabled", result.Message);
        Assert.True(core.Stopped);
        Assert.Empty(core.RestartedStates);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.Disabled, false, 7890)], configuration.Requests);
        Assert.Equal(1, serviceStatus.StopCount);
        Assert.Equal(2, windowsProxy.DisableCount);
        Assert.Empty(windowsProxy.EnabledServers);
    }

    /// <summary>Verifies journaled parameters win over settings that changed after planning.</summary>
    [Fact]
    public async Task ApplyModeAsync_WithExplicitPlan_UsesFrozenTunAndPortValues()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            windowsProxy: windowsProxy);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 12000,
            CancellationToken.None);

        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.RuleTakeover, false, 12000)], configuration.Requests);
        Assert.Equal(["127.0.0.1:12000"], windowsProxy.EnabledServers);
    }

    [Fact]
    public async Task ApplyModeAsync_WhenLeavingTun_StopsServiceBeforeStartingAppOwner()
    {
        List<string> operations = [];
        FakeNetworkTakeoverCoreConfiguration configuration = new(operations);
        FakeNetworkTakeoverCore core = new(operations);
        FakeNetworkTakeoverMihomoService serviceStatus = new(
            new MihomoServiceStatus(true, true, "running"),
            operations);
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            serviceStatus: serviceStatus);

        await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 12000,
            CancellationToken.None);

        Assert.Equal(
            ["configuration.app", "app.stop", "service.stop", "app.restart", "service.query"],
            operations);
    }

    /// <summary>An authenticated stopped child releases TUN ownership without stopping the host.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenLeavingTun_AcceptsIdleInstallerManagedHost()
    {
        MihomoServiceStatus idleHost = new(true, false, "idle")
        {
            IsScmRunning = true,
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            ServiceSessionId = Guid.NewGuid(),
            ServiceVersion = "test",
            ChildState = MihomoServiceChildState.Stopped,
        };
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(
            new MihomoServiceStatus(true, true, "running"))
        {
            StopResult = idleHost,
        };
        NetworkTakeoverService service = CreateService(
            core: core,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 12000,
            CancellationToken.None);

        Assert.Equal(MihomoCoreOwner.App, result.EffectiveOwner);
        Assert.True(core.IsRunning);
        Assert.Equal(1, serviceStatus.StopCount);
    }

    /// <summary>A running host without an authenticated stopped snapshot is not release proof.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenIdleHostCannotBeAuthenticated_FailsClosed()
    {
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(
            new MihomoServiceStatus(true, true, "running"))
        {
            StopResult = new MihomoServiceStatus(true, false, "IPC unavailable")
            {
                IsScmRunning = true,
                IpcFailureCode = "service.ipc.timeout",
            },
        };
        NetworkTakeoverService service = CreateService(
            core: core,
            serviceStatus: serviceStatus);

        InvalidOperationException failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => service.ApplyModeAsync(
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                mixedPort: 12000,
                CancellationToken.None));

        Assert.Equal("service.ipc.timeout", RuntimeDiagnosticCode.Extract(failure));
        Assert.Empty(core.RestartedStates);
    }

    [Fact]
    public async Task ApplyModeAsync_SystemProxy_ReleasesOldListenerThenEnablesOnlyAfterReadiness()
    {
        List<string> operations = [];
        NetworkTakeoverService service = CreateService(
            configuration: new FakeNetworkTakeoverCoreConfiguration(operations),
            core: new FakeNetworkTakeoverCore(operations),
            windowsProxy: new FakeNetworkTakeoverWindowsProxy(operations),
            serviceStatus: new FakeNetworkTakeoverMihomoService(
                new MihomoServiceStatus(true, false, "stopped"),
                operations),
            readiness: new FakeNetworkTakeoverReadiness(operations));

        await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 12000,
            CancellationToken.None);

        Assert.Equal(
            [
                "configuration.app",
                "proxy.disable",
                "app.stop",
                "service.stop",
                "app.restart",
                "service.query",
                "controller.ready",
                "proxy.enable",
            ],
            operations);
    }

    [Fact]
    public async Task ApplyModeAsync_WhenEnteringTun_StopsAppBeforeStartingServiceOwner()
    {
        List<string> operations = [];
        FakeNetworkTakeoverCoreConfiguration configuration = new(operations);
        FakeNetworkTakeoverCore core = new(operations);
        FakeNetworkTakeoverMihomoService serviceStatus = new(
            new MihomoServiceStatus(true, false, "stopped"),
            operations);
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            serviceStatus: serviceStatus);

        await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            mixedPort: 12000,
            CancellationToken.None);

        Assert.Equal(
            [
                "service.query",
                "configuration.tun",
                "service.query",
                "app.stop",
                "service.restart",
                "service.query",
            ],
            operations);
    }

    [Fact]
    public async Task ApplyModeAsync_CancelledAfterServiceQuery_DoesNotMutateNetworkState()
    {
        using CancellationTokenSource cancellation = new();
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: new CancellingNetworkTakeoverMihomoService(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            mixedPort: 12000,
            cancellation.Token));

        Assert.Empty(configuration.Requests);
        Assert.Empty(core.RestartedStates);
        Assert.False(core.Stopped);
        Assert.Empty(windowsProxy.EnabledServers);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    [Fact]
    public async Task ApplyModeAsync_WhenServiceStatusIsUnknown_FailsBeforeChangingEitherOwner()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(
            MihomoServiceStatus.Unknown("unknown") with
            {
                IpcFailureCode = "service.ipc.timeout",
            });
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        InvalidOperationException failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => service.ApplyModeAsync(
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: true,
                mixedPort: 12000,
                CancellationToken.None));

        Assert.Equal("service.ipc.timeout", RuntimeDiagnosticCode.Extract(failure));
        Assert.False(core.Stopped);
        Assert.Empty(core.RestartedStates);
        Assert.Empty(configuration.Requests);
        Assert.Equal(0, serviceStatus.RestartCount);
        Assert.Equal(0, serviceStatus.StopCount);
        Assert.Empty(windowsProxy.EnabledServers);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    [Fact]
    public async Task ApplyModeAsync_WhenRollbackFailsGenerically_PreservesActivationDiagnostic()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        RuntimeConfigurationActivationPlan plan = new(
            ClashSharpMode.Standby,
            TunEnabled: false,
            MixedPort: 12000,
            ProfileId: "profile-a");
        string hash = new('a', 64);
        configuration.ResultOverride = new RuntimeConfigurationTransactionResult(
            RuntimeConfigurationTransactionOutcome.RollbackFailed,
            new RuntimeConfigurationGenerationState(
                DesiredGeneration: 1,
                DesiredContentHash: hash,
                DesiredPlan: plan,
                AppliedGeneration: null,
                AppliedContentHash: null,
                AppliedPlan: null),
            configuration.State,
            Failure: new TestRuntimeDiagnosticException("geo.assets_missing"),
            RollbackFailure: new InvalidOperationException("generic rollback failure"));
        NetworkTakeoverService service = CreateService(configuration: configuration);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyModeAsync(
                ClashSharpMode.Standby,
                transparentProxyEnabled: false,
                mixedPort: 12000,
                CancellationToken.None));

        Assert.Equal("geo.assets_missing", RuntimeDiagnosticCode.Extract(failure));
    }

    [Fact]
    public async Task ApplyModeAsync_WhenServiceCannotAcquireOwnership_CompensatesToAppSystemProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, true, "running"))
        {
            RestartResult = new MihomoServiceStatus(true, false, "stopped"),
        };
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            mixedPort: 12000,
            CancellationToken.None);

        Assert.True(result.CoreRunning);
        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal("missing rule", result.Message);
        Assert.True(core.Stopped);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.Equal(
            [
                new ConfigurationRequest(ClashSharpMode.RuleTakeover, true, 12000),
                new ConfigurationRequest(ClashSharpMode.RuleTakeover, false, 12000),
            ],
            configuration.Requests);
        Assert.Equal(1, serviceStatus.RestartCount);
        Assert.Equal(2, serviceStatus.StopCount);
        Assert.Equal(["127.0.0.1:12000"], windowsProxy.EnabledServers);
    }

    [Fact]
    public async Task ApplyModeAsync_WhenOwnershipReleaseCannotBeConfirmed_DoesNotStartAppCore()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, true, "running"))
        {
            RestartResult = MihomoServiceStatus.Unknown("unknown"),
            StopResult = MihomoServiceStatus.Unknown("unknown"),
        };
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyModeAsync(
            ClashSharpMode.FullTakeover,
            transparentProxyEnabled: true,
            mixedPort: 12000,
            CancellationToken.None));

        Assert.True(core.Stopped);
        Assert.Empty(core.RestartedStates);
        Assert.Equal(
            [
                new ConfigurationRequest(ClashSharpMode.FullTakeover, true, 12000),
                new ConfigurationRequest(ClashSharpMode.FullTakeover, false, 12000),
            ],
            configuration.Requests);
        Assert.Equal(1, serviceStatus.RestartCount);
        Assert.Equal(2, serviceStatus.StopCount);
        Assert.Empty(windowsProxy.EnabledServers);
        Assert.Equal(2, windowsProxy.DisableCount);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("hash")]
    public async Task WaitUntilReadyAsync_ServiceOwner_BindsProbeOnlyAfterExactRuntimeStatus(
        string mismatchedField)
    {
        string configurationHash = new('b', 64);
        Guid serviceSessionId = Guid.NewGuid();
        MihomoServiceStatus expectedStatus = new(true, true, "ready")
        {
            IsScmRunning = true,
            ServiceSessionId = serviceSessionId,
            ActiveGeneration = 7,
            ActiveConfigurationHash = configurationHash,
        };
        MihomoServiceStatus staleStatus = mismatchedField switch
        {
            "session" => expectedStatus with { ServiceSessionId = null },
            "generation" => expectedStatus with { ActiveGeneration = 6 },
            "hash" => expectedStatus with { ActiveConfigurationHash = new string('c', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatchedField)),
        };
        FakeNetworkTakeoverMihomoService serviceStatus = new(staleStatus);
        serviceStatus.StatusResults.Enqueue(staleStatus);
        serviceStatus.StatusResults.Enqueue(expectedStatus);
        FakeNetworkTakeoverReadiness readiness = new();
        NetworkTakeoverService service = CreateService(
            serviceStatus: serviceStatus,
            readiness: readiness);
        ICoreConfigurationRuntime runtime = service;
        RuntimeConfigurationActivationPlan plan = new(
            ClashSharpMode.RuleTakeover,
            TunEnabled: true,
            MixedPort: 7890,
            ProfileId: "profile-a");

        bool result = await runtime.WaitUntilReadyAsync(
            7,
            configurationHash,
            plan,
            CancellationToken.None);

        Assert.True(result);
        ReadinessRequest request = Assert.Single(readiness.Requests);
        Assert.Equal(7, request.Generation);
        Assert.Equal(configurationHash, request.ConfigurationHash);
        Assert.Equal(serviceSessionId, request.ObservedServiceStatus.ServiceSessionId);
        Assert.Equal(7, request.ObservedServiceStatus.ActiveGeneration);
        Assert.Equal(configurationHash, request.ObservedServiceStatus.ActiveConfigurationHash);
    }

    private static NetworkTakeoverService CreateService(
        FakeNetworkTakeoverCoreConfiguration? configuration = null,
        FakeNetworkTakeoverCore? core = null,
        FakeNetworkTakeoverWindowsProxy? windowsProxy = null,
        INetworkTakeoverMihomoService? serviceStatus = null,
        FakeNetworkTakeoverProxyRecovery? proxyRecovery = null,
        INetworkTakeoverReadiness? readiness = null)
    {
        return new NetworkTakeoverService(
            configuration ?? new FakeNetworkTakeoverCoreConfiguration(),
            core ?? new FakeNetworkTakeoverCore(),
            windowsProxy ?? new FakeNetworkTakeoverWindowsProxy(),
            serviceStatus ?? new FakeNetworkTakeoverMihomoService(new MihomoServiceStatus(true, true, "Installed")),
            proxyRecovery ?? new FakeNetworkTakeoverProxyRecovery(),
            readiness ?? new FakeNetworkTakeoverReadiness(),
            key => key switch
            {
                "NetworkTakeover.Disabled" => "disabled",
                "NetworkTakeover.Standby" => "standby",
                "NetworkTakeover.StartupRecovered" => "startup recovered",
                "NetworkTakeover.SystemProxy.Full" => "system full",
                "NetworkTakeover.SystemProxy.Rule" => "system rule",
                "NetworkTakeover.TransparentProxy.Full" => "transparent full",
                "NetworkTakeover.TransparentProxy.Rule" => "transparent rule",
                "NetworkTakeover.TransparentProxyServiceMissing.Full" => "missing full",
                "NetworkTakeover.TransparentProxyServiceMissing.Rule" => "missing rule",
                _ => key,
            });
    }

    private sealed class FakeNetworkTakeoverCoreConfiguration(List<string>? operations = null)
        : INetworkTakeoverCoreConfiguration
    {
        public CoreConfigurationState State { get; } = new(
            AppContext.BaseDirectory,
            typeof(NetworkTakeoverServiceTests).Assembly.Location,
            true);

        public List<ConfigurationRequest> Requests { get; } = [];

        public RuntimeConfigurationTransactionResult? ResultOverride { get; set; }

        private CoreConfigurationState PrepareConfiguration(
            ClashSharpMode mode,
            bool transparentProxyEnabled,
            int mixedPort)
        {
            operations?.Add(transparentProxyEnabled ? "configuration.tun" : "configuration.app");
            Requests.Add(new ConfigurationRequest(mode, transparentProxyEnabled, mixedPort));
            return State;
        }

        public async Task<RuntimeConfigurationTransactionResult> ApplyConfigurationAsync(
            ClashSharpMode mode,
            bool transparentProxyEnabled,
            int mixedPort,
            ICoreConfigurationRuntime runtime,
            CancellationToken cancellationToken)
        {
            CoreConfigurationState state = PrepareConfiguration(mode, transparentProxyEnabled, mixedPort);
            if (ResultOverride is not null)
            {
                return ResultOverride;
            }

            RuntimeConfigurationActivationPlan plan = new(mode, transparentProxyEnabled, mixedPort, "profile-a");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(state.ConfigPath)))
                .ToLowerInvariant();
            RuntimeConfigurationGenerationState generation = new(1, hash, plan, 1, hash, plan);
            try
            {
                await runtime.ApplyAsync(state, 1, plan, cancellationToken);
                if (!await runtime.WaitUntilReadyAsync(1, hash, plan, cancellationToken))
                {
                    throw new InvalidOperationException("not ready");
                }

                await runtime.CommitAsync(1, plan, cancellationToken);

                return new RuntimeConfigurationTransactionResult(
                    RuntimeConfigurationTransactionOutcome.Applied,
                    generation,
                    state,
                    Failure: null,
                    RollbackFailure: null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new RuntimeConfigurationTransactionResult(
                    RuntimeConfigurationTransactionOutcome.RolledBack,
                    generation with
                    {
                        AppliedGeneration = 0,
                        AppliedContentHash = hash,
                        AppliedPlan = plan,
                    },
                    state,
                    exception,
                    RollbackFailure: null);
            }
        }
    }

    private sealed class TestRuntimeDiagnosticException(string diagnosticCode)
        : InvalidOperationException("typed runtime failure"), IStableDiagnosticCodeProvider
    {
        public string DiagnosticCode { get; } = diagnosticCode;
    }

    private sealed class FakeNetworkTakeoverCore(List<string>? operations = null) : INetworkTakeoverCore
    {
        public bool IsRunning { get; private set; }

        public bool IsOwnershipKnown { get; set; } = true;

        public bool Stopped { get; private set; }

        public List<CoreConfigurationState> RestartedStates { get; } = [];

        public void Stop()
        {
            operations?.Add("app.stop");
            IsRunning = false;
            Stopped = true;
        }

        public void Restart(CoreConfigurationState configurationState)
        {
            operations?.Add("app.restart");
            IsRunning = true;
            RestartedStates.Add(configurationState);
        }
    }

    private sealed class FakeNetworkTakeoverWindowsProxy(List<string>? operations = null)
        : INetworkTakeoverWindowsProxy
    {
        public int DisableCount { get; private set; }

        public List<string> EnabledServers { get; } = [];

        public void DisableProxy()
        {
            operations?.Add("proxy.disable");
            DisableCount++;
        }

        public void EnableProxy(string proxyServer)
        {
            operations?.Add("proxy.enable");
            EnabledServers.Add(proxyServer);
        }
    }

    private sealed class FakeNetworkTakeoverMihomoService : INetworkTakeoverMihomoService
    {
        private MihomoServiceStatus _status;

        private readonly List<string>? _operations;

        public FakeNetworkTakeoverMihomoService(
            MihomoServiceStatus status,
            List<string>? operations = null)
        {
            _status = status;
            _operations = operations;
            RestartResult = status.IsInstalled
                ? new MihomoServiceStatus(true, true, "running")
                : status;
            StopResult = status.IsInstalled
                ? new MihomoServiceStatus(true, false, "stopped")
                : status;
        }

        public int RestartCount { get; private set; }

        public long? LastRestartGeneration { get; private set; }

        public string? LastRestartConfigurationHash { get; private set; }

        public int StopCount { get; private set; }

        public Queue<MihomoServiceStatus> StatusResults { get; } = new();

        public MihomoServiceStatus RestartResult { get; init; }

        public MihomoServiceStatus StopResult { get; init; }

        public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _operations?.Add("service.query");
            if (StatusResults.Count > 0)
            {
                _status = StatusResults.Dequeue();
            }

            return Task.FromResult(_status);
        }

        public Task<MihomoServiceStatus> RestartAsync(
            long generation,
            string configurationHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _operations?.Add("service.restart");
            RestartCount++;
            LastRestartGeneration = generation;
            LastRestartConfigurationHash = configurationHash;
            _status = RestartResult with
            {
                IsScmRunning = RestartResult.IsInstalled,
                ServiceSessionId = RestartResult.ServiceSessionId ?? Guid.NewGuid(),
                ActiveGeneration = generation,
                ActiveConfigurationHash = configurationHash,
            };
            return Task.FromResult(_status);
        }

        public Task<MihomoServiceStatus> StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _operations?.Add("service.stop");
            StopCount++;
            _status = StopResult;
            return Task.FromResult(StopResult);
        }
    }

    private sealed class CancellingNetworkTakeoverMihomoService(CancellationTokenSource cancellation)
        : INetworkTakeoverMihomoService
    {
        public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new MihomoServiceStatus(true, true, "running"));
        }

        public Task<MihomoServiceStatus> RestartAsync(
            long generation,
            string configurationHash,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Restart must not run after cancellation.");
        }

        public Task<MihomoServiceStatus> StopAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Stop must not run after cancellation.");
        }
    }

    private sealed class FakeNetworkTakeoverProxyRecovery : INetworkTakeoverProxyRecovery
    {
        public string BuildLoopbackProxyServer(int mixedPort)
        {
            return $"127.0.0.1:{mixedPort}";
        }
    }

    private sealed class FakeNetworkTakeoverReadiness(List<string>? operations = null)
        : INetworkTakeoverReadiness
    {
        public List<ReadinessRequest> Requests { get; } = [];

        public Task<bool> MatchesRuntimeConfigurationAsync(
            RuntimeConfigurationActivationPlan plan,
            long generation,
            string configurationHash,
            MihomoServiceStatus observedServiceStatus,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations?.Add("controller.ready");
            Requests.Add(new ReadinessRequest(
                plan,
                generation,
                configurationHash,
                observedServiceStatus));
            return Task.FromResult(true);
        }
    }

    private readonly record struct ReadinessRequest(
        RuntimeConfigurationActivationPlan Plan,
        long Generation,
        string ConfigurationHash,
        MihomoServiceStatus ObservedServiceStatus);

    private readonly record struct ConfigurationRequest(
        ClashSharpMode Mode,
        bool TransparentProxyEnabled,
        int MixedPort);
}
