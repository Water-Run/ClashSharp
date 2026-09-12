using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerProtectedTransactionReaderTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public async Task MissingAppearingAndRemovedStateAreObservedWithoutRetainedLeases()
    {
        using var fixture = new Fixture();
        using WindowsInstallerProtectedTransactionReader reader = fixture.CreateReader();
        fixture.Native.Present = false;

        Assert.Null(await reader.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Native.ActiveLeases);

        fixture.Native.Present = true;
        Assert.Equal(fixture.Snapshot, await reader.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Native.ActiveLeases);

        File.Delete(fixture.JournalPath);
        fixture.Native.Present = false;
        Assert.Null(await reader.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Native.ActiveLeases);
        Assert.Equal(0, fixture.Native.CreateCount);
    }

    [Theory]
    [InlineData("reparse")]
    [InlineData("acl")]
    [InlineData("io")]
    public async Task EveryReadRevalidatesTheCurrentDirectoryChain(string change)
    {
        using var fixture = new Fixture();
        using WindowsInstallerProtectedTransactionReader reader = fixture.CreateReader();
        Assert.Equal(fixture.Snapshot, await reader.LoadAsync(CancellationToken.None));
        fixture.Native.Change = change;

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => reader.LoadAsync(CancellationToken.None));

        Assert.Equal(change switch
        {
            "reparse" => "installer.transaction.root_reparse_rejected",
            "acl" => "installer.transaction.root_acl_invalid",
            _ => "installer.transaction.root_verification_failed",
        }, failure.DiagnosticCode);
        Assert.Equal(0, fixture.Native.ActiveLeases);
        Assert.Equal(0, fixture.Native.CreateCount);
    }

    [Fact]
    public async Task InvalidJournalReleasesAllLeasesAndDoesNotPoisonTheNextRead()
    {
        using var fixture = new Fixture();
        using WindowsInstallerProtectedTransactionReader reader = fixture.CreateReader();
        File.WriteAllText(fixture.JournalPath, "{}");

        await Assert.ThrowsAsync<InstallerProtocolException>(() => reader.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Native.ActiveLeases);

        File.WriteAllBytes(fixture.JournalPath, InstallerTransactionCodec.Serialize(fixture.Snapshot.Journal));
        Assert.Equal(fixture.Snapshot, await reader.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Native.ActiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeDrainsAnAcceptedReadWithoutReleasingItsProtection(bool cancelRead)
    {
        using var fixture = new Fixture();
        using WindowsInstallerProtectedTransactionReader reader = fixture.CreateReader();
        using var cancellation = new CancellationTokenSource();
        using var releaseRead = new ManualResetEventSlim();
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int paused = 0;
        fixture.Native.OnRootObservation = () =>
        {
            if (Interlocked.Exchange(ref paused, 1) == 0)
            {
                enteredRead.SetResult();
                if (!releaseRead.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("The test did not release its directory observation.");
                }
            }
        };
        Task<InstallerTransactionSnapshot?> read = Task.Run(() => reader.LoadAsync(cancellation.Token));
        Task? disposal = null;
        try
        {
            await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposal = Task.Run(() =>
            {
                enteredDispose.SetResult();
                reader.Dispose();
            });
            await enteredDispose.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Wait for disposal admission without relying on scheduling delays. Any read accepted
            // before Dispose entered is independently guarded and fully awaited here.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try
                {
                    await reader.LoadAsync(deadline.Token);
                    await Task.Yield();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }

            Assert.False(disposal.IsCompleted);
            Assert.True(fixture.Native.ActiveLeases > 0);
            if (cancelRead)
            {
                cancellation.Cancel();
            }
        }
        finally
        {
            releaseRead.Set();
            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        if (cancelRead)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        else
        {
            Assert.Equal(fixture.Snapshot, await read);
        }
        Assert.Equal(0, fixture.Native.ActiveLeases);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreCancelledReadDoesNotOpenDirectories()
    {
        using var fixture = new Fixture();
        using WindowsInstallerProtectedTransactionReader reader = fixture.CreateReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.LoadAsync(cancellation.Token));

        Assert.Equal(0, fixture.Native.OpenCount);
        Assert.Equal(0, fixture.Native.ActiveLeases);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryRoot;

        internal Fixture()
        {
            WindowsPayloadFixture.AssertWindows11X64();
            _temporaryRoot = Path.Combine(Path.GetTempPath(), "ClashSharp.Reader.Tests." + Guid.NewGuid().ToString("N"));
            ProgramDataPath = Path.Combine(_temporaryRoot, "ProgramData");
            string stateRoot = Path.Combine(ProgramDataPath, "ClashSharp", "Installer", "v2");
            Directory.CreateDirectory(stateRoot);
            JournalPath = Path.Combine(stateRoot, InstallerStateLayout.JournalFileName);
            Snapshot = InstallerTransactionSnapshot.Create(InstallerTransactionJournal.Create(
                new InstallerRequest(InstallerOperation.Uninstall, TargetSid, false, "1.0.0.0", new string('a', 64))));
            File.WriteAllBytes(JournalPath, InstallerTransactionCodec.Serialize(Snapshot.Journal));
            Native = new FakeDirectories(ProgramDataPath, stateRoot);
        }

        internal string ProgramDataPath { get; }
        internal string JournalPath { get; }
        internal InstallerTransactionSnapshot Snapshot { get; }
        internal FakeDirectories Native { get; }

        internal WindowsInstallerProtectedTransactionReader CreateReader() =>
            WindowsInstallerProtectedTransactionReader.CreateForTesting(ProgramDataPath, TargetSid, Native);

        public void Dispose()
        {
            File.Delete(JournalPath);
            string? current = Path.GetDirectoryName(JournalPath);
            while (current is not null && current.StartsWith(_temporaryRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(current, recursive: false);
                if (string.Equals(current, _temporaryRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }
    }

    private sealed class FakeDirectories(string programDataPath, string stateRoot) : IWindowsInstallerDirectoryNative
    {
        private readonly string _stateRoot = stateRoot;
        private int _activeLeases;
        private int _openCount;
        private int _createCount;

        internal bool Present { get; set; } = true;
        internal string? Change { get; set; }
        internal Action? OnRootObservation { get; set; }
        internal int ActiveLeases => Volatile.Read(ref _activeLeases);
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int CreateCount => Volatile.Read(ref _createCount);

        public void CreateDirectory(string path, DirectorySecurity security) =>
            Interlocked.Increment(ref _createCount);

        public IWindowsInstallerDirectoryLease OpenDirectory(string path)
        {
            Interlocked.Increment(ref _openCount);
            if (!Present && path.StartsWith(programDataPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException();
            }
            if (Change == "io" && path == _stateRoot)
            {
                throw new IOException("Injected directory observation failure.");
            }
            Interlocked.Increment(ref _activeLeases);
            return new Lease(this, path);
        }

        private sealed class Lease(FakeDirectories owner, string path) : IWindowsInstallerDirectoryLease
        {
            private bool _disposed;

            public WindowsDirectoryObservation Observe()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                bool exact = path == owner._stateRoot || path == Path.GetDirectoryName(owner._stateRoot);
                if (path == owner._stateRoot)
                {
                    owner.OnRootObservation?.Invoke();
                }
                AceFlags flags = exact ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
                var entries = new List<WindowsDirectoryAce>
                {
                    Ace(WindowsInstallerDirectorySecurityPolicy.LocalSystemSid, FileSystemRights.FullControl, flags),
                    Ace(WindowsInstallerDirectorySecurityPolicy.AdministratorsSid, FileSystemRights.FullControl, flags),
                };
                if (exact)
                {
                    entries.Add(Ace(TargetSid, WindowsInstallerDirectorySecurityPolicy.TargetUserReadOnlyRights, flags));
                }
                if (owner.Change == "acl" && path == owner._stateRoot)
                {
                    entries.Add(Ace("S-1-5-32-545", FileSystemRights.Write, flags));
                }
                return new WindowsDirectoryObservation(true, owner.Change == "reparse" && path == owner._stateRoot,
                    new WindowsDirectorySecuritySnapshot(WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
                        HasDacl: true, DaclProtected: exact, entries));
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    Interlocked.Decrement(ref owner._activeLeases);
                }
            }
        }

        private static WindowsDirectoryAce Ace(string sid, FileSystemRights rights, AceFlags flags) =>
            new(sid, WindowsDirectoryAceKind.Allow, (int)rights, flags, IsObjectSpecific: false);
    }
}
