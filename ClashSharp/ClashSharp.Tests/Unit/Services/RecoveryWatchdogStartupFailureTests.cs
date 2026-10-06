extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Startup;
using Coordinator = ClashSharpUi::ClashSharp.Service.RecoveryWatchdogCoordinator;
using Dword = ClashSharpUi::ClashSharp.Service.WindowsProxyDwordValue;
using Journal = ClashSharpUi::ClashSharp.Service.WindowsProxyMutationJournal;
using LeaseStore = ClashSharpUi::ClashSharp.Recovery.RecoveryWatchdogLeaseFileStore;
using Proxy = ClashSharpUi::ClashSharp.Service.WindowsProxyService;
using ProxyJournal = ClashSharpUi::ClashSharp.Service.IWindowsProxyMutationJournalStore;
using ProxyRegistry = ClashSharpUi::ClashSharp.Service.IWindowsProxyRegistryStore;
using Snapshot = ClashSharpUi::ClashSharp.Service.WindowsProxyRegistrySnapshot;
using StringKind = ClashSharpUi::ClashSharp.Service.WindowsProxyStringKind;
using StringValue = ClashSharpUi::ClashSharp.Service.WindowsProxyStringValue;

namespace ClashSharp.Tests.Unit.Services;

public sealed class RecoveryWatchdogStartupFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailurePresentation_AwaitsHostStopAndRetainsProtectionAfterRestoringOwnedFields(bool externalServer)
    {
        using TestScope scope = new();
        Registry registry = new();
        Store journal = new();
        Proxy proxy = new(registry, journal);
        Snapshot expected = registry.Current;
        proxy.EnableProxy("127.0.0.1:10000");
        if (externalServer)
        {
            expected = new(new Dword(true, 1), Text("corporate.example:8080"), Text("corp-bypass"), Text("https://corp.example/proxy.pac"));
            registry.Current = expected;
        }
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeHost host = new(() => stopped.Task);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);
        bool restored = false;
        using Coordinator owner = scope.Create(() =>
        {
            Assert.False(lifetime.HasAttachedHost);
            Assert.Equal(1, host.Disposals);
            scope.AssertLocksHeld();
            proxy.RestoreOwnedProxy();
            restored = true;
        });

        Task preparation = owner.PrepareStartupFailureAsync(() => lifetime.StopAsync(CancellationToken.None));
        Assert.False(restored);
        Assert.NotNull(journal.Current);
        scope.AssertLocksHeld();
        stopped.SetResult();
        await preparation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, registry.Current);
        Assert.Null(journal.Current);
        Assert.True(restored);
        scope.AssertLocksHeld();
        owner.CompleteNormalExit(startupFailed: true);
        scope.AssertLocksReleased();
    }

    [Fact]
    public async Task FailurePresentation_FailedHostStopRetainsProxyAndProtectionForExitRetry()
    {
        using TestScope scope = new();
        Registry registry = new();
        Store journal = new();
        Proxy proxy = new(registry, journal);
        Snapshot baseline = registry.Current;
        proxy.EnableProxy("127.0.0.1:10000");
        Snapshot active = registry.Current;
        int attempts = 0;
        IOException failure = new("Runtime has not released its ownership.");
        RuntimeHost host = new(() => ++attempts == 1 ? Task.FromException(failure) : Task.CompletedTask);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);
        using Coordinator owner = scope.Create(proxy.RestoreOwnedProxy);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(
            () => owner.PrepareStartupFailureAsync(() => lifetime.StopAsync(CancellationToken.None))));

        Assert.Equal(active, registry.Current);
        Assert.NotNull(journal.Current);
        Assert.True(lifetime.HasAttachedHost);
        Assert.Equal(0, host.Disposals);
        scope.AssertLocksHeld();
        await owner.PrepareStartupFailureAsync(() => lifetime.StopAsync(CancellationToken.None));
        Assert.Equal(baseline, registry.Current);
        Assert.Equal(2, host.Stops);
        Assert.Equal(1, host.Disposals);
        scope.AssertLocksHeld();
        owner.CompleteNormalExit(startupFailed: true);
        scope.AssertLocksReleased();
    }

    [Fact]
    public async Task FailurePresentation_FailedProxyRestoreRetainsProtectionWithoutDisposingHostTwice()
    {
        using TestScope scope = new();
        Registry registry = new();
        Store journal = new();
        Proxy proxy = new(registry, journal);
        Snapshot baseline = registry.Current;
        proxy.EnableProxy("127.0.0.1:10000");
        Snapshot active = registry.Current;
        RuntimeHost host = new(() => Task.CompletedTask);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);
        IOException failure = new("Durable proxy journal is temporarily unreadable.");
        int restores = 0;
        using Coordinator owner = scope.Create(() =>
        {
            scope.AssertLocksHeld();
            if (++restores == 1) { throw failure; }
            proxy.RestoreOwnedProxy();
        });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(
            () => owner.PrepareStartupFailureAsync(() => lifetime.StopAsync(CancellationToken.None))));

        Assert.Equal(active, registry.Current);
        Assert.NotNull(journal.Current);
        Assert.False(lifetime.HasAttachedHost);
        scope.AssertLocksHeld();
        await owner.PrepareStartupFailureAsync(() => lifetime.StopAsync(CancellationToken.None));
        Assert.Equal(baseline, registry.Current);
        Assert.Null(journal.Current);
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Disposals);
        scope.AssertLocksHeld();
        owner.CompleteNormalExit(startupFailed: true);
        scope.AssertLocksReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedStartup_RestoresOnlyJournalProvenFieldsBeforeReleasingProcessOwnership(bool externalServer)
    {
        using TestScope scope = new();
        Registry registry = new();
        Store journal = new();
        Proxy proxy = new(registry, journal);
        Snapshot baseline = registry.Current;
        proxy.EnableProxy("127.0.0.1:10000");
        Snapshot expected = baseline;
        if (externalServer)
        {
            expected = new(new Dword(true, 1), Text("corporate.example:8080"), Text("corp-bypass"), Text("https://corp.example/proxy.pac"));
            registry.Current = expected;
        }
        using Coordinator owner = scope.Create(() =>
        {
            scope.AssertLocksHeld();
            proxy.RestoreOwnedProxy();
        });

        owner.CompleteNormalExit(startupFailed: true);

        Assert.Equal(expected, registry.Current);
        Assert.Null(journal.Current);
        scope.AssertLocksReleased();
    }

    [Fact]
    public void SuccessfulStartup_DoesNotOverrideTheConfiguredShutdownPolicy()
    {
        using TestScope scope = new();
        Registry registry = new();
        Store journal = new();
        Proxy proxy = new(registry, journal);
        proxy.EnableProxy("127.0.0.1:10000");
        Snapshot configured = registry.Current;
        using Coordinator owner = scope.Create(() => throw new InvalidOperationException("Normal shutdown already applied its configured policy."));

        owner.CompleteNormalExit(startupFailed: false);

        Assert.Equal(configured, registry.Current);
        Assert.NotNull(journal.Current);
        scope.AssertLocksReleased();
    }

    [Fact]
    public void FailedStartup_RestoreFailureRetainsOwnershipUntilTheExactRetrySucceeds()
    {
        using TestScope scope = new();
        int calls = 0;
        IOException expected = new("Owned proxy restoration failed.");
        using Coordinator owner = scope.Create(() =>
        {
            scope.AssertLocksHeld();
            if (++calls == 1) { throw expected; }
        });

        Assert.Same(expected, Assert.Throws<IOException>(() => owner.CompleteNormalExit(startupFailed: true)));
        scope.AssertLocksHeld();
        owner.CompleteNormalExit(startupFailed: true);

        Assert.Equal(2, calls);
        scope.AssertLocksReleased();
    }

    [Fact]
    public void FailedStartup_WithoutAJournalDoesNotClaimTheExternalProxy()
    {
        using TestScope scope = new();
        Registry registry = new() { Current = new(new Dword(true, 1), Text("127.0.0.1:10000"), Text("external"), Text("https://corp.example/proxy.pac")) };
        Store journal = new();
        Proxy proxy = new(registry, journal);
        Snapshot external = registry.Current;
        using Coordinator owner = scope.Create(proxy.RestoreOwnedProxy);

        owner.CompleteNormalExit(startupFailed: true);

        Assert.Equal(external, registry.Current);
        Assert.Equal(0, registry.Writes);
    }

    private static StringValue Text(string value) => new(true, value, StringKind.String);

    private sealed class RuntimeHost(Func<Task> stop) : IApplicationHost
    {
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fixture starts at the failed-startup shutdown boundary.");
        public Task StopAsync(CancellationToken cancellationToken) { ++Stops; return stop(); }
        public ValueTask DisposeAsync() { ++Disposals; return ValueTask.CompletedTask; }
    }

    private sealed class Registry : ProxyRegistry
    {
        public Snapshot Current { get; set; } = new(new Dword(true, 0), new(false, null, StringKind.None), new(false, null, StringKind.None), new(false, null, StringKind.None));
        public int Writes { get; private set; }
        public Snapshot Read() => Current;
        public void Write(Snapshot value) { Current = value; ++Writes; }
    }

    private sealed class Store : ProxyJournal
    {
        public Journal? Current { get; private set; }
        public Journal? Read() => Current;
        public void Write(Journal value) => Current = value;
        public void Clear() => Current = null;
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ClashSharp-startup-exit-" + Guid.NewGuid().ToString("N"));
        private string Installer => Path.Combine(_root, "installer.lock");
        private string Recovery => Path.Combine(_root, "recovery.lock");
        public Coordinator Create(Action restore)
        {
            Directory.CreateDirectory(_root);
            return new(new FileStream(Installer, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None),
                new FileStream(Recovery, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None),
                new LeaseStore(Path.Combine(_root, "lease.json")), restore);
        }
        public void AssertLocksHeld()
        {
            Assert.Throws<IOException>(() => { using FileStream stream = File.OpenRead(Installer); });
            Assert.Throws<IOException>(() => { using FileStream stream = File.OpenRead(Recovery); });
        }
        public void AssertLocksReleased()
        {
            using FileStream installer = File.OpenRead(Installer);
            using FileStream recovery = File.OpenRead(Recovery);
        }
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
}
