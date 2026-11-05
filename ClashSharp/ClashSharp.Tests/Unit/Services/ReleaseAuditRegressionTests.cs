extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using ClashSharp.Model.Rules;
using ClashSharp.Service;
using ClearOperation = ClashSharpUi::ClashSharp.Service.ApplicationDataClearOperation;
using NativeSettingsAdapter = ClashSharpUi::ClashSharp.Service.WindowsDiagnosticSettingsAdapter;

namespace ClashSharp.Tests.Unit.Services;

public sealed class ReleaseAuditRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeDiagnosticsRetainsFatalGraphsAndClassifiesOrdinaryUnknownState(bool fatal)
    {
#pragma warning disable CA2201 // Deliberately exercise fatal-graph classification without allocating memory.
        InvalidOperationException failure = new("observation unavailable", fatal ? new OutOfMemoryException("simulated") : null);
#pragma warning restore CA2201
        NativeSettingsAdapter settings = new(new FailedObserver(failure));
        if (fatal) { Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => settings.ReadNetworkAsync(CancellationToken.None))); }
        else { Assert.False((await settings.ReadNetworkAsync(CancellationToken.None)).IsKnown); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WslOneAndMirroredRetainSupportedBridgeConfiguration(bool wslOne)
    {
        using TemporaryData data = new();
        NativeEnvironment environment = new();
        NativeProcesses processes = new();
        WindowsNetworkDiagnosticService diagnostics = new(new ObservedSettings { IsDefaultWslVersionOne = wslOne }, environment, processes,
            new WindowsDiagnosticMutationJournalFileStore(Path.Combine(data.Path, "journal.json")), key => key);
        Assert.True((await diagnostics.ApplyAsync(WindowsDiagnosticTarget.Wsl, CancellationToken.None)).IsHealthy);
        Assert.Equal("http://127.0.0.1:19090", environment.Values["HTTP_PROXY"]);
        Assert.Equal(wslOne, processes.WslModeReads == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearDataRestoresNativeChangesBeforeDeletingTheirRecoveryProof(bool refuseRestore)
    {
        using TemporaryData data = new();
        MutationAdmissionBarrier admission = new();
        NativeEnvironment environment = new();
        environment.Values["HTTP_PROXY"] = "http://corporate.example:8080";
        environment.Values["WSLENV"] = "USERPROFILE/up";
        NativeProcesses processes = new();
        string journalPath = Path.Combine(data.Path, "WindowsNetworkMutationJournal.json");
        WindowsNetworkDiagnosticService diagnostics = new(new ObservedSettings(), environment, processes,
            new WindowsDiagnosticMutationJournalFileStore(journalPath), key => key, admission);
        await diagnostics.ApplyAsync(WindowsDiagnosticTarget.Wsl, CancellationToken.None);
        await diagnostics.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);
        await diagnostics.ApplyAsync(WindowsDiagnosticTarget.MicrosoftStore, CancellationToken.None);
        environment.Values["ALL_PROXY"] = "socks5://external.example:1080";
        environment.RefuseWrites = refuseRestore;
        bool hostCleared = false;
        ClearOperation clear = new(async token =>
        {
            await using (await admission.CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, token)) { }
            return new(RuntimeShutdownOutcome.PreparedForHostDisposal, null, []);
        }, () => hostCleared = true, data.Path, () => true, diagnostics.RestoreForDataRemovalAsync);
        await clear.PrepareShutdownAsync(CancellationToken.None);
        if (refuseRestore)
        {
            await Assert.ThrowsAsync<IOException>(() => clear.ClearHostDataAsync(CancellationToken.None));
            Assert.False(hostCleared);
            Assert.True(File.Exists(journalPath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => clear.ClearLocalFilesAsync(CancellationToken.None));
            return;
        }
        await clear.ClearHostDataAsync(CancellationToken.None);
        Assert.True(hostCleared);
        Assert.Equal("http://corporate.example:8080", environment.Values["HTTP_PROXY"]);
        Assert.Equal("USERPROFILE/up", environment.Values["WSLENV"]);
        Assert.Equal("socks5://external.example:1080", environment.Values["ALL_PROXY"]);
        Assert.False(processes.StorePresent);
        Assert.False(File.Exists(journalPath));
        await clear.ClearLocalFilesAsync(CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(() => diagnostics.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None));
    }

    [Fact]
    public async Task NativeWriteRemainsOwnedUntilShutdownDrainAndRecovery()
    {
        using TemporaryData data = new();
        MutationAdmissionBarrier admission = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeEnvironment environment = new() { BeforeWrite = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        WindowsNetworkDiagnosticService diagnostics = new(new ObservedSettings(), environment, new NativeProcesses(),
            new WindowsDiagnosticMutationJournalFileStore(Path.Combine(data.Path, "journal.json")), key => key, admission);
        Task write = Task.Run(() => diagnostics.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<MutationAdmissionLease> drain = admission.CloseAndDrainAsync(MutationAdmissionClosure.Shutdown, CancellationToken.None).AsTask();
        try { Assert.False(drain.IsCompleted); }
        finally { release.Set(); }
        try { await write; } catch (OperationCanceledException) { }
        (await drain.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        environment.BeforeWrite = null;
        await diagnostics.RestoreForDataRemovalAsync(CancellationToken.None);
        Assert.Empty(environment.Values);
    }

    [Theory]
    [InlineData("nat")]
    [InlineData("unknown")]
    public async Task WslDoesNotApplyLocalhostRepairToAnUnreachableOrUnknownMode(string mode)
    {
        using TemporaryData data = new();
        NativeEnvironment environment = new();
        WindowsNetworkDiagnosticService diagnostics = new(new ObservedSettings(), environment, new NativeProcesses { WslMode = mode },
            new WindowsDiagnosticMutationJournalFileStore(Path.Combine(data.Path, "journal.json")), key => key);
        WindowsDiagnosticResult result = await diagnostics.ApplyAsync(WindowsDiagnosticTarget.Wsl, CancellationToken.None);
        Assert.False(result.IsHealthy);
        Assert.Empty(environment.Values);
        Assert.Equal(mode == "nat" ? "WindowsDiagnostic.Wsl.NatUnsupported" : "WindowsDiagnostic.Wsl.NetworkModeUnknown", result.MessageResourceKey);
    }

    [Fact]
    public async Task DiagnosticsUsesObservedPortAndReleasesOwnedVariablesWhenTunTakesOver()
    {
        using TemporaryData data = new();
        NativeEnvironment environment = new();
        ObservedSettings settings = new() { Network = new(ClashSharpMode.RuleTakeover, true, true, false, 19091, "installed-a") };
        WindowsNetworkDiagnosticService diagnostics = new(settings, environment, new NativeProcesses(),
            new WindowsDiagnosticMutationJournalFileStore(Path.Combine(data.Path, "journal.json")), key => key);
        await diagnostics.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);
        Assert.Equal("http://127.0.0.1:19091", environment.Values["HTTP_PROXY"]);
        settings.Network = settings.Network with { TransparentProxyEnabled = true, SystemProxyEnabled = false };
        Assert.False((await diagnostics.DiagnoseAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None)).IsHealthy);
        Assert.True((await diagnostics.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None)).IsHealthy);
        Assert.Empty(environment.Values);
        settings.Network = settings.Network with { IsKnown = false };
        Assert.False((await diagnostics.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None)).IsHealthy);
        Assert.Empty(environment.Values);
    }

    [Theory]
    [InlineData("DomainSuffix", "example.com", "DOMAIN-SUFFIX", "example.com")]
    [InlineData("AND", "((Network,udp) && (DstPort,443))", "AND", "((NETWORK,UDP),(DST-PORT,443))")]
    [InlineData("NOT", "(!(Network,udp))", "NOT", "((NETWORK,UDP))")]
    public async Task SqliteRuleObservationsReachCatalogAndRemainScopedToInstalledConfiguration(string runtimeType, string runtimePayload, string sourceType, string sourcePayload)
    {
        using TemporaryData data = new();
        await using LogStorageService storage = new(Path.Combine(data.Path, "logs.db"), () => "desired-unapplied");
        Guid epoch = Guid.NewGuid();
        ActiveConnection connection = new("one", "browser", "example.com", runtimeType, runtimePayload, "Node / Group", 10, 20, DateTimeOffset.UtcNow);
        storage.AppendTrafficSnapshot(new(epoch, 10, 20, [connection], ProfileId: "a", ConfigurationIdentity: "installed-a"));
        RuleSource source = new() { StatisticsIdentity = "installed-a", Type = sourceType, Payload = sourcePayload };
        RuleCatalogService catalog = new(source, new RuleHits(storage), key => key);
        Assert.Equal(1, Assert.Single(catalog.GetRules()).HitCount);
        storage.AppendTrafficSnapshot(new(epoch, 20, 40, [connection with { Id = "two" }], ProfileId: "b", ConfigurationIdentity: "installed-b"));
        source.StatisticsIdentity = "installed-b";
        Assert.Equal(1, Assert.Single(catalog.GetRules()).HitCount);
        source.StatisticsIdentity = "pending-without-observations";
        Assert.Equal(0, Assert.Single(catalog.GetRules()).HitCount);
        Assert.Equal("MATCH", RuleObservationKey.Create("Match", string.Empty));
        storage.ClearAll();
        Assert.Empty(storage.GetRuleHitCounts("installed-a"));
    }

    private sealed class RuleSource : IRuleCatalogProfileRules
    {
        public string? StatisticsIdentity { get; set; }
        public string Type { get; init; } = "DOMAIN-SUFFIX";
        public string Payload { get; init; } = "example.com";
        public IReadOnlyList<RulePreview> ParseActiveProfileRules() => [new("profile", Type, Payload, "Group", 0)];
    }

    private sealed class RuleHits(LogStorageService storage) : IRuleCatalogHitStorage
    {
        public void EnsureRuleHitRows(IReadOnlyList<RulePreview> rules) => storage.EnsureRuleHitRows(rules);
        public IReadOnlyDictionary<string, long> GetRuleHitCounts() => storage.GetRuleHitCounts();
        public IReadOnlyDictionary<string, long> GetRuleHitCounts(string identity) => storage.GetRuleHitCounts(identity);
    }

    private sealed class ObservedSettings : IWindowsDiagnosticSettings
    {
        public bool IsDefaultWslVersionOne { get; set; }
        public NetworkStateSnapshot Network { get; set; } = new(ClashSharpMode.Standby, true, false, false, 19090, "observed");
        public Task<NetworkStateSnapshot> ReadNetworkAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Network);
        }
    }

    private sealed class FailedObserver(Exception failure) : INetworkStateObserver
    {
        public Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken) => Task.FromException<NetworkStateSnapshot>(failure);
    }

    private sealed class NativeEnvironment : IWindowsDiagnosticEnvironment
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RefuseWrites { get; set; }
        public Action? BeforeWrite { get; set; }
        public string? GetUserEnvironmentVariable(string name) => Values.GetValueOrDefault(name);
        public void SetUserEnvironmentVariable(string name, string? value)
        {
            BeforeWrite?.Invoke();
            if (RefuseWrites) { return; }
            if (value is null) { Values.Remove(name); } else { Values[name] = value; }
        }
    }

    private sealed class NativeProcesses : IWindowsDiagnosticProcessRunner
    {
        public int WslModeReads { get; private set; }
        public string WslMode { get; set; } = "mirrored";
        public bool StorePresent { get; set; }
        public Task<WindowsDiagnosticProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (fileName == "wsl.exe")
            {
                bool modeRead = arguments.Contains("--networking-mode");
                if (modeRead) { WslModeReads++; }
                return Task.FromResult(new WindowsDiagnosticProcessResult(0, modeRead ? WslMode : string.Empty, string.Empty));
            }
            if (arguments.Contains("-a")) { StorePresent = true; }
            if (arguments.Contains("-d")) { StorePresent = false; }
            return Task.FromResult(new WindowsDiagnosticProcessResult(0, StorePresent ? "Microsoft.WindowsStore_8wekyb3d8bbwe" : string.Empty, string.Empty));
        }
    }

    private sealed class TemporaryData : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clashsharp-release-fix-" + Guid.NewGuid().ToString("N"));
        public TemporaryData() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
