using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed partial class WindowsMachineHelperHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallOwnsLocksUntilCleanupAndNeverSendsSuccessAfterFinalizerFailure(bool failCleanup)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashSharp.Cleanup.Host." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var events = new List<string>();
            var rootGuard = new CleanupRootGuard(root, events);
            var ledger = new DirectoryCleanupTestLedger();
            using WindowsInstallerProtectedStateStores stores = WindowsInstallerProtectedStateStores.CreateForTesting(root, rootGuard, ledger);
            InstallerTransactionSnapshot verified = await PrepareUninstallAsync(stores.Transactions);
            InstallerMachineHelperCommand clear = InstallerMachineHelperCommand.Create(
                InstallerMachineHelperInvocation.Create(InstallerMachineHelperVerb.Clear, verified), verified);
            using var commands = new MemoryStream();
            await InstallerMachineHelperFraming.WriteCommandAsync(commands, clear, CancellationToken.None);
            commands.Position = 0;
            using var results = new MemoryStream();
            var operations = new CleanupOperations(events);
            var finalizer = new WindowsInstallerEmptyDirectoryFinalizer(
                new WindowsInstallerDirectoryCleanupLayout(@"C:\Program Files", @"C:\ProgramData"), ledger,
                new CleanupDirectories(() =>
                {
                    Assert.Equal(verified, ledger.Current!.Terminal);
                    Assert.Equal(1, rootGuard.DisposeCount);
                    Assert.Equal(1, operations.DisposeCount);
                    Assert.True(events.IndexOf("operations-dispose") < events.IndexOf("root-dispose"));
                    Assert.Equal(0, results.Length);
                    Assert.Contains("application-lock", events);
                    Assert.Contains("authority-lock", events);
                    Assert.DoesNotContain("application-unlock", events);
                    Assert.DoesNotContain("authority-unlock", events);
                    events.Add("cleanup");
                    if (failCleanup) { throw new IOException("Injected finalizer failure."); }
                }));
            var resources = new WindowsMachineHelperAuthorityResources(stores, operations, finalizer);
            var authority = new WindowsMachineHelperAuthorityFactory(new CleanupResourcesFactory(resources),
                new RecordingAuthorityLock(events), new RecordingApplicationLock(events),
                new RecordingOwnerTransferAdmission(events), new AllowRetiredUninstallAdmission());
            WindowsMachineHelperHost host = CreateHost(events, new RecordingTrustVerifier(events),
                new RecordingParentVerifier(events), new RecordingClientFactory(events, commands, results), authority);
            InstallerMachineHelperBootstrap bootstrap = InstallerMachineHelperBootstrap.Create(clear.ToInvocation(), 4242);

            if (failCleanup)
            {
                await Assert.ThrowsAsync<IOException>(() => host.RunAsync(bootstrap, CancellationToken.None));
                Assert.Equal(0, results.Length);
                Assert.Equal(verified, ledger.Current!.Terminal);
                using var reopened = new FileInstallerTransactionStore(root, new CleanupRootGuard(root, events));
                Assert.Null(await reopened.LoadAsync(CancellationToken.None));
                Assert.Equal(verified, await new WindowsInstallerCleanupTransactionReader(reopened, ledger).LoadAsync(CancellationToken.None));
            }
            else
            {
                await host.RunAsync(bootstrap, CancellationToken.None);
                results.Position = 0;
                InstallerMachineHelperResult reply = await InstallerMachineHelperFraming.ReadResultAsync(results, CancellationToken.None);
                Assert.Equal(verified, reply.ValidateAgainst(clear));
                Assert.NotNull(reply.DirectoryCleanupReport);
                Assert.All(reply.DirectoryCleanupReport.Entries,
                    entry => Assert.Equal(InstallerDirectoryCleanupDisposition.Missing, entry.Disposition));
                Assert.Equal(6, events.Count(item => item == "cleanup"));
                Assert.Null(ledger.Current);
            }
            Assert.Equal(1, rootGuard.DisposeCount);
            Assert.Equal(1, operations.DisposeCount);
            Assert.Equal(1, events.Count(item => item == "application-unlock"));
            Assert.Equal(1, events.Count(item => item == "authority-unlock"));
            Assert.True(events.LastIndexOf("cleanup") < events.IndexOf("application-unlock"));
        }
        finally
        {
            File.Delete(Path.Combine(root, InstallerStateLayout.JournalFileName));
            Directory.Delete(root, recursive: false);
        }
    }

    private static async Task<InstallerTransactionSnapshot> PrepareUninstallAsync(IInstallerTransactionStore store)
    {
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(new(InstallerOperation.Uninstall,
            "S-1-5-21-100-200-300-1001", false, "1.2.3.4", new string('a', 64)));
        InstallerTransactionSnapshot saved = await store.SaveAsync(journal, null, CancellationToken.None);
        foreach (InstallerTransactionPhase phase in new[]
        {
            InstallerTransactionPhase.MachineRemovalAuthorized, InstallerTransactionPhase.MachineCommitted,
            InstallerTransactionPhase.PackageCommitted, InstallerTransactionPhase.Verified,
        })
        {
            saved = await store.SaveAsync(saved.Journal.TransitionTo(phase), saved.ContentHash, CancellationToken.None);
        }
        return saved;
    }

    private sealed class CleanupRootGuard(string root, List<string> events) : IInstallerTransactionRootGuard, IDisposable
    {
        internal int DisposeCount { get; private set; }
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            Assert.Equal(root, absoluteRootPath);
            return Task.CompletedTask;
        }
        public void Dispose() { DisposeCount++; events.Add("root-dispose"); }
    }

    private sealed class CleanupOperations(List<string> events) : IInstallerMachineHelperOperationExecutor, IDisposable
    {
        internal int DisposeCount { get; private set; }
        public Task ExecuteAsync(InstallerMachineHelperCommand command, InstallerMachineHelperSessionDisposition disposition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            command.Validate();
            Assert.Equal(InstallerMachineHelperVerb.Clear, command.Verb);
            events.Add("operation");
            return Task.CompletedTask;
        }
        public void Dispose() { DisposeCount++; events.Add("operations-dispose"); }
    }

    private sealed class CleanupResourcesFactory(IWindowsMachineHelperAuthorityResources resources)
        : IWindowsMachineHelperAuthorityResourcesFactory
    {
        public IWindowsMachineHelperAuthorityResources Create(string targetSid) => resources;
    }

    private sealed class CleanupDirectories(Action inspect) : IWindowsInstallerEmptyDirectoryNative
    {
        public IWindowsInstallerDirectoryDeletionLease? Open(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspect();
            return null;
        }
    }
}
