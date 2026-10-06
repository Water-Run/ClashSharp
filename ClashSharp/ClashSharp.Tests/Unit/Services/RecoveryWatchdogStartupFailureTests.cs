extern alias ClashSharpUi;
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
