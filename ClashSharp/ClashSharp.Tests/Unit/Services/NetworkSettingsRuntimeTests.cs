extern alias ClashSharpUi;

using System.Globalization;
using ClashSharp.Model;
using ClashSharp.Tests.Integration;
using Host = ClashSharpUi::ClashSharp;
using NetworkSettingsConfiguration = ClashSharpUi::ClashSharp.Hosting.Settings.NetworkSettingsConfiguration;
using RuntimeConfigurationActivationPlan = ClashSharpUi::ClashSharp.Service.RuntimeConfigurationActivationPlan;
using RuntimeConfigurationIntegrityObservation = ClashSharpUi::ClashSharp.Service.RuntimeConfigurationIntegrityObservation;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies actual production network observation and transaction code with isolated operating-system ports.</summary>
public sealed class NetworkSettingsRuntimeTests
{
    [Fact]
    public async Task EmptyRuntime_RequiresReleasedOwnershipAndAllowsUnownedProxyWithoutCreatingStorage()
    {
        await using DataGenerationTestDirectory directory = new();
        NativePorts ports = new();
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath);
        NetworkSettingsConfiguration observed = await ports.Takeover.ObserveNetworkSettingsAsync(
            configuration.ObserveRuntimeConfigurationIntegrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None);
        Assert.Equal(new NetworkSettingsConfiguration(ClashSharpMode.Disabled, "builtin-direct", false, 10000), observed);
        Assert.False(Directory.Exists(directory.RootPath));
        Assert.Equal(0, ports.Writes);
        ports.Proxy = new(true, "other-proxy:8080");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ObserveNetworkSettingsAsync(
            configuration.ObserveRuntimeConfigurationIntegrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        ports.Journal.Current = null;
        Assert.Equal(observed, await ports.Takeover.ObserveNetworkSettingsAsync(
            configuration.ObserveRuntimeConfigurationIntegrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        Assert.True(ports.Proxy.IsEnabled);
        Assert.Equal(0, ports.Writes);
    }

    [Fact]
    public async Task ExplicitProfileTransaction_DoesNotReadLegacySettingsAndExposesVerifiedGenerationHash()
    {
        await using DataGenerationTestDirectory directory = new();
        NativePorts ports = new();
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath);
        NetworkSettingsConfiguration target = new(ClashSharpMode.RuleTakeover, "builtin-direct", false, 18301);
        await ports.Takeover.ApplyNetworkSettingsConfigurationAsync(configuration, target, CancellationToken.None);
        RuntimeConfigurationIntegrityObservation integrity = configuration.ObserveRuntimeConfigurationIntegrity();
        Assert.True(integrity.IsKnown);
        Assert.Equal(1, integrity.AppliedGeneration);
        Assert.NotNull(integrity.AppliedContentHash);
        Assert.Equal(64, integrity.AppliedContentHash.Length);
        NetworkSettingsConfiguration observed = await ports.Takeover.ObserveNetworkSettingsAsync(
            configuration.ObserveRuntimeConfigurationIntegrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None);
        Assert.Equal(target, observed);
        Assert.Equal(integrity.AppliedGeneration, ports.LastReadinessGeneration);
        Assert.Equal(integrity.AppliedContentHash, ports.LastReadinessHash);
        Assert.True(ports.CoreRunning);
        Assert.Equal(new Host.Model.WindowsProxyState(true, "127.0.0.1:18301"), ports.Proxy);
    }

    [Fact]
    public async Task ModifiedConfigOrAppliedSnapshot_CannotProvideGenerationEvidence()
    {
        await using DataGenerationTestDirectory directory = new();
        NativePorts ports = new();
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath);
        await ports.Takeover.ApplyNetworkSettingsConfigurationAsync(configuration,
            new(ClashSharpMode.Disabled, "builtin-direct", false, 18302), CancellationToken.None);
        string path = Path.Combine(directory.RootPath, "config.yaml");
        string original = await File.ReadAllTextAsync(path);
        await File.AppendAllTextAsync(path, "\n# isolated tamper\n");
        RuntimeConfigurationIntegrityObservation invalid = configuration.ObserveRuntimeConfigurationIntegrity();
        Assert.False(invalid.IsKnown);
        Assert.Null(invalid.AppliedGeneration);
        Assert.Null(invalid.AppliedContentHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ObserveNetworkSettingsAsync(
            configuration.ObserveRuntimeConfigurationIntegrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        await File.WriteAllTextAsync(path, original);
        Assert.True(configuration.ObserveRuntimeConfigurationIntegrity().IsKnown);
        string snapshot = Assert.Single(Directory.GetFiles(Path.Combine(directory.RootPath, "runtime-generations"), "*.yaml"));
        await File.AppendAllTextAsync(snapshot, "\n# isolated snapshot tamper\n");
        Assert.False(configuration.ObserveRuntimeConfigurationIntegrity().IsKnown);
    }

    [Fact]
    public async Task MissingTunService_FailsBeforeConfigurationOrProxyEffects()
    {
        await using DataGenerationTestDirectory directory = new();
        NativePorts ports = new();
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ApplyNetworkSettingsConfigurationAsync(configuration,
            new(ClashSharpMode.RuleTakeover, "builtin-direct", true, 18303), CancellationToken.None));
        Assert.False(Directory.Exists(directory.RootPath));
        Assert.Equal(0, ports.Writes);
        Assert.False(ports.CoreRunning);
    }

    [Theory]
    [InlineData("unknown-service")]
    [InlineData("missing-owner")]
    [InlineData("unknown-app-owner")]
    [InlineData("controller-mismatch")]
    [InlineData("wrong-proxy-port")]
    [InlineData("owner-lost-during-readiness")]
    [InlineData("generation-changed")]
    public async Task IndependentRuntimeMismatch_RefusesAppliedObservation(string failure)
    {
        NativePorts ports = new() { CoreRunning = true, Proxy = new(true, "127.0.0.1:18304") };
        RuntimeConfigurationIntegrityObservation integrity = new(true,
            new(ClashSharpMode.RuleTakeover, false, 18304, "builtin-direct"), 7, new string('a', 64));
        switch (failure)
        {
            case "unknown-service": ports.ServiceStatus = Host.Model.MihomoServiceStatus.Unknown("isolated unknown"); break;
            case "missing-owner": ports.CoreRunning = false; break;
            case "unknown-app-owner": ports.OwnerKnown = false; break;
            case "controller-mismatch": ports.Ready = false; break;
            case "wrong-proxy-port": ports.Proxy = new(true, "127.0.0.1:9999"); break;
            case "owner-lost-during-readiness": ports.OnReadiness = () => ports.CoreRunning = false; break;
            case "generation-changed": ports.OnReadiness = () => integrity = integrity with { AppliedGeneration = 8 }; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ObserveNetworkSettingsAsync(
            () => integrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        Assert.Equal(0, ports.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceTun_RequiresExactSessionGenerationAndNoAppOwner(bool staleGeneration)
    {
        NativePorts ports = new();
        RuntimeConfigurationIntegrityObservation integrity = new(true,
            new(ClashSharpMode.FullTakeover, true, 18305, "builtin-direct"), 9, new string('b', 64));
        ports.ServiceStatus = new(true, true, "isolated ready")
        {
            ServiceSessionId = Guid.NewGuid(),
            ActiveGeneration = staleGeneration ? 8 : 9,
            ActiveConfigurationHash = integrity.AppliedContentHash,
        };
        if (staleGeneration)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ObserveNetworkSettingsAsync(
                () => integrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        }
        else
        {
            NetworkSettingsConfiguration result = await ports.Takeover.ObserveNetworkSettingsAsync(() => integrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None);
            Assert.True(result.EffectiveTunEnabled);
            ports.CoreRunning = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ObserveNetworkSettingsAsync(
                () => integrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        }
        Assert.Equal(0, ports.Writes);
    }

    [Theory]
    [InlineData("bypass")]
    [InlineData("pac")]
    [InlineData("server-kind")]
    [InlineData("journal-missing")]
    [InlineData("journal-pending")]
    [InlineData("journal-invalid")]
    [InlineData("journal-unreadable")]
    public async Task SystemProxyProbe_RejectsTupleOrOwnershipDriftWithoutRegistryWrites(string drift)
    {
        NativePorts ports = new() { CoreRunning = true };
        ports.WindowsProxy.EnableProxy("127.0.0.1:18312");
        RuntimeConfigurationIntegrityObservation integrity = new(true,
            new(ClashSharpMode.RuleTakeover, false, 18312, "builtin-direct"), 11, new string('c', 64));
        Assert.Equal(18312, (await ports.Takeover.ObserveNetworkSettingsAsync(
            () => integrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None)).MixedPort);
        switch (drift)
        {
            case "bypass": ports.Registry.Current = ports.Registry.Current with { ProxyOverride = new(true, "*") }; break;
            case "pac": ports.Registry.Current = ports.Registry.Current with { AutoConfigUrl = new(true, "https://external.example/pac") }; break;
            case "server-kind":
                ports.Registry.Current = ports.Registry.Current with
                { ProxyServer = new(true, "127.0.0.1:18312", Host.Service.WindowsProxyStringKind.ExpandString) }; break;
            case "journal-missing": ports.Journal.Current = null; break;
            case "journal-pending":
                ports.Journal.Current = ports.Journal.Current! with
                { Phase = Host.Service.WindowsProxyMutationPhase.Applying, PendingApplied = ports.Registry.Current }; break;
            case "journal-invalid": ports.Journal.Current = ports.Journal.Current! with { SchemaVersion = 999 }; break;
            case "journal-unreadable": ports.Journal.Failure = new IOException("isolated journal read failure"); break;
        }
        int writes = ports.Registry.Writes;
        Assert.True(ports.Proxy.IsEnabled);
        Assert.Equal("127.0.0.1:18312", ports.Proxy.ProxyServer);
        await Assert.ThrowsAnyAsync<Exception>(() => ports.Takeover.ObserveNetworkSettingsAsync(
            () => integrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        Assert.Equal(writes, ports.Registry.Writes);
        Assert.Equal(0, ports.Writes);
    }

    [Theory]
    [InlineData(ClashSharpMode.Disabled, false)]
    [InlineData(ClashSharpMode.Standby, false)]
    [InlineData(ClashSharpMode.FullTakeover, true)]
    public async Task ReleasingOwnedProxy_RestoresAndObservesEnabledThirdPartyBaseline(ClashSharpMode mode, bool tun)
    {
        await using DataGenerationTestDirectory directory = new();
        NativePorts ports = new();
        Host.Service.WindowsProxyRegistrySnapshot baseline = new(new(true, 1), new(true, "corporate.example:8080"),
            new(true, "intranet.example"), new(true, "https://corporate.example/pac"));
        ports.Registry.Current = baseline;
        ports.ServiceStatus = new(true, false, "isolated installed");
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath);
        await ports.Takeover.ApplyNetworkSettingsConfigurationAsync(configuration,
            new(ClashSharpMode.RuleTakeover, "builtin-direct", false, 18313), CancellationToken.None);
        Assert.NotNull(ports.Journal.Current);
        NetworkSettingsConfiguration target = new(mode, "builtin-direct", tun, 18313);
        await ports.Takeover.ApplyNetworkSettingsConfigurationAsync(configuration, target, CancellationToken.None);
        Assert.Equal(baseline, ports.Registry.Current);
        Assert.Null(ports.Journal.Current);
        int writes = ports.Registry.Writes;
        Assert.Equal(target, await ports.Takeover.ObserveNetworkSettingsAsync(
            configuration.ObserveRuntimeConfigurationIntegrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        Assert.Equal(writes, ports.Registry.Writes);
        Assert.True(ports.Proxy.IsEnabled);
    }

    [Fact]
    public async Task ServiceSessionChangedDuringReadiness_RejectsEvenWithSameGenerationAndHash()
    {
        NativePorts ports = new();
        RuntimeConfigurationIntegrityObservation integrity = new(true,
            new(ClashSharpMode.FullTakeover, true, 18314, "builtin-direct"), 12, new string('d', 64));
        ports.ServiceStatus = new(true, true, "isolated ready")
        { ServiceSessionId = Guid.NewGuid(), ActiveGeneration = 12, ActiveConfigurationHash = integrity.AppliedContentHash };
        ports.OnReadiness = () => ports.ServiceStatus = ports.ServiceStatus with { ServiceSessionId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ObserveNetworkSettingsAsync(
            () => integrity, ports.WindowsProxy.ObserveOwnership, CancellationToken.None));
        Assert.Equal(0, ports.Writes);
    }

    [Fact]
    public async Task JournalPathOccupiedByDirectory_IsUnknownOwnershipInsteadOfReleased()
    {
        await using DataGenerationTestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        ProxyRegistry registry = new();
        Host.Service.WindowsProxyService proxy = new(registry,
            new Host.Service.WindowsProxyMutationJournalFileStore(directory.RootPath));
        Assert.Throws<UnauthorizedAccessException>(() => proxy.ObserveOwnership());
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task AbsentJournalAndParentDirectory_CanProveNoOwnershipWithoutCreatingStorage()
    {
        await using DataGenerationTestDirectory directory = new();
        ProxyRegistry registry = new();
        Host.Service.WindowsProxyService proxy = new(registry,
            new Host.Service.WindowsProxyMutationJournalFileStore(Path.Combine(directory.RootPath, "journal.json")));
        Assert.True(proxy.ObserveOwnership().HasReleasedOwnership);
        Assert.False(Directory.Exists(directory.RootPath));
        Assert.Equal(0, registry.Writes);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("activation")]
    [InlineData("readiness")]
    [InlineData("commit")]
    public async Task FatalRuntimeGraph_PreservesOriginalAndDurableDesiredWithoutCompensation(string stage)
    {
        await using DataGenerationTestDirectory directory = new();
        Validator validator = new();
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath, validator);
        FaultRuntime runtime = new();
        await configuration.ApplyRuntimeConfigurationAsync("builtin-direct", ClashSharpMode.Standby, false, 18306, runtime, CancellationToken.None);
        Exception fatal = new OperationCanceledException("isolated fatal wrapper", Activator.CreateInstance<OutOfMemoryException>());
        if (stage == "validation") { validator.Failure = fatal; }
        else { runtime.Failure = fatal; runtime.Stage = stage; }
        int appliedBefore = runtime.Applies;
        Assert.Same(fatal, await Assert.ThrowsAsync<OperationCanceledException>(() => configuration.ApplyRuntimeConfigurationAsync(
            "builtin-direct", ClashSharpMode.Standby, false, 18307, runtime, CancellationToken.None)));
        Assert.Equal(appliedBefore + (stage == "validation" ? 0 : 1), runtime.Applies);
        Assert.Equal(1, runtime.Deactivations);
        Host.Service.RuntimeConfigurationGenerationState retained = await configuration.GetRuntimeGenerationStateAsync(CancellationToken.None);
        Assert.Equal(1, retained.AppliedGeneration);
        Assert.Equal(2, retained.DesiredGeneration);
        Assert.False(configuration.ObserveRuntimeConfigurationIntegrity().IsKnown);
        if (stage == "validation") { Assert.Single(Directory.GetFiles(directory.RootPath, "config.yaml.runtime-staging.*")); }
    }

    [Fact]
    public async Task FatalRollbackGraph_EscapesInsteadOfReturningRecoverableFailure()
    {
        await using DataGenerationTestDirectory directory = new();
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath);
        FaultRuntime runtime = new();
        await configuration.ApplyRuntimeConfigurationAsync("builtin-direct", ClashSharpMode.Standby, false, 18308, runtime, CancellationToken.None);
        Exception fatal = new AggregateException(Activator.CreateInstance<OutOfMemoryException>());
        runtime.Stage = "activation";
        runtime.Failure = new IOException("isolated activation failure");
        runtime.RollbackFailure = fatal;
        Assert.Same(fatal, await Assert.ThrowsAsync<AggregateException>(() => configuration.ApplyRuntimeConfigurationAsync(
            "builtin-direct", ClashSharpMode.Standby, false, 18309, runtime, CancellationToken.None)));
        Assert.Equal(3, runtime.Applies);
        Assert.Equal(1, runtime.Deactivations);
        Assert.False(configuration.ObserveRuntimeConfigurationIntegrity().IsKnown);
    }

    [Fact]
    public async Task FatalControllerGraph_IsNotRetriedByReadinessLoop()
    {
        await using DataGenerationTestDirectory directory = new();
        NativePorts ports = new();
        Host.Service.CoreConfigurationService configuration = CreateConfiguration(directory.RootPath);
        await ports.Takeover.ApplyNetworkSettingsConfigurationAsync(configuration,
            new(ClashSharpMode.Standby, "builtin-direct", false, 18310), CancellationToken.None);
        Exception fatal = new InvalidOperationException("isolated controller wrapper", Activator.CreateInstance<OutOfMemoryException>());
        int readsBefore = ports.ReadinessCalls;
        ports.ReadinessFailure = fatal;
        Assert.Same(fatal, await Assert.ThrowsAsync<InvalidOperationException>(() => ports.Takeover.ApplyNetworkSettingsConfigurationAsync(configuration,
            new(ClashSharpMode.Standby, "builtin-direct", false, 18311), CancellationToken.None)));
        Assert.Equal(readsBefore + 1, ports.ReadinessCalls);
        Assert.False(configuration.ObserveRuntimeConfigurationIntegrity().IsKnown);
    }

    private static Host.Service.CoreConfigurationService CreateConfiguration(string path, Validator? validator = null) =>
        new(path, new RejectLegacySettings(), new FixedControllerCredentialProvider(), new EmptyMetrics(), validator ?? new Validator(), key => key);

    private sealed class RejectLegacySettings : Host.Service.ICoreConfigurationSettings
    {
        public bool TransparentProxyEnabled => throw new InvalidOperationException("Legacy preferences must not be read.");
        public int MixedPort => throw new InvalidOperationException("Legacy preferences must not be read.");
        public string ActiveProfileId => throw new InvalidOperationException("Legacy preferences must not be read.");
    }
    private sealed class EmptyMetrics : Host.Service.ICoreConfigurationProfileMetrics
    {
        public int CountNodes(string configurationText) => 0;
        public int CountRules(string configurationText) => 0;
    }
    private sealed class Validator : Host.Service.ICoreConfigurationValidator
    {
        public Exception? Failure { get; set; }
        public Task ValidateAsync(string workingDirectory, string configurationPath, CancellationToken cancellationToken) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    private sealed class FaultRuntime : Host.Service.ICoreConfigurationRuntime
    {
        public int Applies { get; private set; }
        public int Deactivations { get; private set; }
        public string? Stage { get; set; }
        public Exception? Failure { get; set; }
        public Exception? RollbackFailure { get; set; }
        public Task ApplyAsync(Host.Model.CoreConfigurationState configuration, long generation,
            RuntimeConfigurationActivationPlan plan, CancellationToken cancellationToken)
        {
            ++Applies;
            Exception? failure = generation == 1 && Applies > 1 ? RollbackFailure : Stage == "activation" ? Failure : null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
        public Task<bool> WaitUntilReadyAsync(long generation, string configurationHash, RuntimeConfigurationActivationPlan plan, CancellationToken cancellationToken) =>
            Stage == "readiness" && Failure is not null ? Task.FromException<bool>(Failure) : Task.FromResult(true);
        public Task CommitAsync(long generation, RuntimeConfigurationActivationPlan plan, CancellationToken cancellationToken) =>
            Stage == "commit" && Failure is not null ? Task.FromException(Failure) : Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken cancellationToken) { ++Deactivations; return Task.CompletedTask; }
    }

    private sealed class NativePorts : Host.Service.INetworkTakeoverCoreConfiguration, Host.Service.INetworkTakeoverCore,
        Host.Service.INetworkTakeoverWindowsProxy, Host.Service.INetworkTakeoverMihomoService,
        Host.Service.INetworkTakeoverProxyRecovery, Host.Service.INetworkTakeoverReadiness
    {
        public NativePorts()
        {
            WindowsProxy = new(Registry, Journal);
            Takeover = new(this, this, this, this, this, this, key => key);
        }
        public Host.Service.NetworkTakeoverService Takeover { get; }
        public ProxyRegistry Registry { get; } = new();
        public ProxyJournal Journal { get; } = new();
        public Host.Service.WindowsProxyService WindowsProxy { get; }
        public Host.Model.WindowsProxyState Proxy
        {
            get => WindowsProxy.GetCurrentState();
            set
            {
                Registry.Current = new(new(true, value.IsEnabled ? 1 : 0), new(true, value.ProxyServer ?? string.Empty),
                    new(true, "<local>"), new(false, null, Host.Service.WindowsProxyStringKind.None));
                Journal.Current = value.IsEnabled ? new(Host.Service.WindowsProxyMutationJournal.CurrentSchemaVersion,
                    Registry.Current, Registry.Current) : null;
            }
        }
        public Host.Model.MihomoServiceStatus ServiceStatus { get; set; } = new(false, false, "isolated missing");
        public bool CoreRunning { get; set; }
        public bool OwnerKnown { get; set; } = true;
        public bool Ready { get; set; } = true;
        public int Writes { get; private set; }
        public Action? OnReadiness { get; set; }
        public long LastReadinessGeneration { get; private set; }
        public string? LastReadinessHash { get; private set; }
        public int ReadinessCalls { get; private set; }
        public Exception? ReadinessFailure { get; set; }
        bool Host.Service.INetworkTakeoverCore.IsRunning => CoreRunning;
        bool Host.Service.INetworkTakeoverCore.IsOwnershipKnown => OwnerKnown;
        public void Restart(Host.Model.CoreConfigurationState configurationState) { ++Writes; CoreRunning = true; }
        public void Stop() { ++Writes; CoreRunning = false; }
        public void DisableProxy() { ++Writes; WindowsProxy.DisableProxy(); }
        public void EnableProxy(string proxyServer) { ++Writes; WindowsProxy.EnableProxy(proxyServer); }
        public string BuildLoopbackProxyServer(int mixedPort) => "127.0.0.1:" + mixedPort.ToString(CultureInfo.InvariantCulture);
        public Task<Host.Model.MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(ServiceStatus);
        public Task<Host.Model.MihomoServiceStatus> StopAsync(CancellationToken cancellationToken)
        { ++Writes; ServiceStatus = new(ServiceStatus.IsInstalled, false, "isolated stopped"); return Task.FromResult(ServiceStatus); }
        public Task<Host.Model.MihomoServiceStatus> RestartAsync(long generation, string configurationHash, CancellationToken cancellationToken)
        {
            ++Writes;
            ServiceStatus = new(true, true, "isolated running")
            { ServiceSessionId = Guid.NewGuid(), ActiveGeneration = generation, ActiveConfigurationHash = configurationHash };
            return Task.FromResult(ServiceStatus);
        }
        public Task<bool> MatchesRuntimeConfigurationAsync(RuntimeConfigurationActivationPlan plan, long generation, string configurationHash,
            Host.Model.MihomoServiceStatus observedServiceStatus, CancellationToken cancellationToken)
        {
            ++ReadinessCalls;
            LastReadinessGeneration = generation;
            LastReadinessHash = configurationHash;
            OnReadiness?.Invoke();
            return ReadinessFailure is null ? Task.FromResult(Ready) : Task.FromException<bool>(ReadinessFailure);
        }
        public Task<Host.Service.RuntimeConfigurationTransactionResult> ApplyConfigurationAsync(ClashSharpMode mode, bool transparentProxyEnabled,
            int mixedPort, Host.Service.ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The explicit settings path must not use legacy configuration selection.");
    }

    private sealed class ProxyRegistry : Host.Service.IWindowsProxyRegistryStore
    {
        public Host.Service.WindowsProxyRegistrySnapshot Current { get; set; } = new(new(true, 0),
            new(false, null, Host.Service.WindowsProxyStringKind.None), new(false, null, Host.Service.WindowsProxyStringKind.None),
            new(false, null, Host.Service.WindowsProxyStringKind.None));
        public int Writes { get; private set; }
        public Host.Service.WindowsProxyRegistrySnapshot Read() => Current;
        public void Write(Host.Service.WindowsProxyRegistrySnapshot snapshot) { ++Writes; Current = snapshot; }
    }

    private sealed class ProxyJournal : Host.Service.IWindowsProxyMutationJournalStore
    {
        public Host.Service.WindowsProxyMutationJournal? Current { get; set; }
        public Exception? Failure { get; set; }
        public Host.Service.WindowsProxyMutationJournal? Read() => Failure is null ? Current : throw Failure;
        public void Write(Host.Service.WindowsProxyMutationJournal journal) => Current = journal;
        public void Clear() => Current = null;
    }
}
