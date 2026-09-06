using System.ComponentModel;
using System.Security.AccessControl;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerOwnerTransferPersistenceTests
{
    private const string ProductRoot = @"C:\ProgramData\ClashSharp";
    private const string AuthorityRoot = ProductRoot + @"\InstallerAuthority";
    private const string PrivateRoot = AuthorityRoot + @"\v1";
    private const string UserSid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public async Task PrivateRootCreatesOnlyTwoAuthorityDirectoriesAndPinsExistingAncestors()
    {
        var native = new FakeDirectories();
        using var guard = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferForTesting(@"C:\ProgramData", native);
        Assert.Empty(native.Calls);

        await guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None);
        await guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None);

        Assert.Equal(PrivateRoot, guard.RootPath);
        Assert.Equal([AuthorityRoot, PrivateRoot], native.CreatedPaths);
        Assert.Equal(5, native.LiveLeases);
        Assert.Equal(3, native.Observations[ProductRoot].Security.AccessEntries.Count);
        WindowsInstallerPrivateStateSecurity.Validate(native.Observations[AuthorityRoot].Security, directory: true);
        WindowsInstallerPrivateStateSecurity.Validate(native.Observations[PrivateRoot].Security, directory: true);
        guard.Dispose();
        Assert.Equal(0, native.LiveLeases);
    }

    [Fact]
    public async Task MissingProductRootIsNotCreatedOrReplacedByPrivateStateSetup()
    {
        var native = new FakeDirectories();
        native.Observations.Remove(ProductRoot);
        using var guard = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferForTesting(@"C:\ProgramData", native);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None));

        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Theory]
    [InlineData("user-readable")]
    [InlineData("wrong-owner")]
    [InlineData("unprotected")]
    [InlineData("no-dacl")]
    [InlineData("deny")]
    [InlineData("inherited")]
    [InlineData("object-specific")]
    [InlineData("extra-entry")]
    [InlineData("wrong-rights")]
    [InlineData("duplicate-admin")]
    public async Task ExistingPrivateRootWithUnexpectedSecurityIsRejectedWithoutAclWashing(string condition)
    {
        var native = new FakeDirectories();
        WindowsInstallerDirectorySecuritySnapshot original = AlterSecurity(
            Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity()), condition);
        native.Observations[AuthorityRoot] = new(true, false, original);
        using var guard = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferForTesting(@"C:\ProgramData", native);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.private_acl_invalid", exception.DiagnosticCode);
        Assert.Same(original, native.Observations[AuthorityRoot].Security);
        Assert.DoesNotContain(PrivateRoot, native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongKindOrReparseAuthorityDirectoryIsRejectedBeforePrivateDescendantCreation(bool reparse)
    {
        var native = new FakeDirectories();
        native.Observations[AuthorityRoot] = new(reparse, reparse, Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity()));
        using var guard = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferForTesting(@"C:\ProgramData", native);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None));

        Assert.DoesNotContain(PrivateRoot, native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Fact]
    public async Task PrivateAclIsRevalidatedOnEveryOperation()
    {
        var native = new FakeDirectories();
        using var guard = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferForTesting(@"C:\ProgramData", native);
        await guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None);
        native.Observations[PrivateRoot] = new(true, false, Snapshot(WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(UserSid)));

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.private_acl_invalid", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateDescriptorsContainOnlyTwoExplicitAuthorityEntries(bool directory)
    {
        WindowsInstallerDirectorySecuritySnapshot security = Snapshot(directory
            ? WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity()
            : WindowsInstallerPrivateStateSecurity.CreateFileSecurity());

        WindowsInstallerPrivateStateSecurity.Validate(security, directory);

        Assert.True(security.DaclProtected);
        Assert.Equal(2, security.AccessEntries.Count);
        Assert.DoesNotContain(security.AccessEntries, entry => entry.Sid == UserSid);
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerPrivateStateSecurity.Validate(security, !directory));
    }

    [Fact]
    public async Task PersistenceIsLazyUsesOneFixedLeafAndRevalidatesBeforeEveryFileCall()
    {
        var calls = new List<string>();
        var guard = new RecordingGuard(calls);
        var files = new RecordingFiles(calls);
        using var persistence = WindowsInstallerOwnerTransferPersistence.CreateForTesting(PrivateRoot, guard, files);
        Assert.Empty(calls);

        await persistence.ReadAsync(CancellationToken.None);
        await persistence.WriteAtomicallyAsync(new byte[] { 1 }, CancellationToken.None);
        await persistence.DeleteAsync(CancellationToken.None);

        Assert.Equal(["guard", "read", "guard", "write", "guard", "delete"], calls);
        persistence.Dispose();
        Assert.True(guard.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => persistence.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task GuardRefusalPreventsAllPrivateFileAccess(string operation)
    {
        var calls = new List<string>();
        var guard = new RecordingGuard(calls) { Refuse = true };
        using var persistence = WindowsInstallerOwnerTransferPersistence.CreateForTesting(PrivateRoot, guard, new RecordingFiles(calls));

        await Assert.ThrowsAsync<InstallerProtocolException>(() => operation switch
        {
            "read" => persistence.ReadAsync(CancellationToken.None),
            "write" => persistence.WriteAtomicallyAsync(new byte[] { 1 }, CancellationToken.None),
            "delete" => persistence.DeleteAsync(CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        Assert.Equal(["guard"], calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OrdinaryAdmissionDoesNotCreateMissingPrivateChainOrOpenALeaf(int presentProtectedSegments)
    {
        var native = new FakeDirectories();
        if (presentProtectedSegments == 0)
        {
            native.Observations.Remove(ProductRoot);
        }
        if (presentProtectedSegments == 2)
        {
            native.Observations[AuthorityRoot] = new(true, false, Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity()));
        }
        var files = new RecordingPresence(native);
        var admission = WindowsInstallerOwnerTransferAdmission.CreateForTesting(
            () => WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferForTesting(@"C:\ProgramData", native), files);
        Assert.Empty(native.Calls);

        await admission.EnsureOrdinaryActionAllowedAsync(CancellationToken.None);

        Assert.Equal(0, files.Calls);
        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryAdmissionChecksPresenceWithinTheValidatedLeaseWithoutReadingCredentials(bool present)
    {
        FakeDirectories native = CompletePrivateChain();
        var files = new RecordingPresence(native) { Present = present };
        var admission = WindowsInstallerOwnerTransferAdmission.CreateForTesting(
            () => WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferForTesting(@"C:\ProgramData", native), files);

        if (present)
        {
            InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                admission.EnsureOrdinaryActionAllowedAsync(CancellationToken.None));
            Assert.Equal("installer.owner_transfer.pending", exception.DiagnosticCode);
        }
        else
        {
            await admission.EnsureOrdinaryActionAllowedAsync(CancellationToken.None);
        }

        Assert.Equal(1, files.Calls);
        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustedPrivateChainOrFileCannotBeReportedAsAbsent(bool fileFailure)
    {
        FakeDirectories native = CompletePrivateChain();
        var expected = new InstallerProtocolException("installer.owner_transfer.file_open_failed");
        var files = new RecordingPresence(native) { Failure = fileFailure ? expected : null };
        if (!fileFailure)
        {
            native.Observations[PrivateRoot] = new(true, false, Snapshot(WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(UserSid)));
        }
        var admission = WindowsInstallerOwnerTransferAdmission.CreateForTesting(
            () => WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferForTesting(@"C:\ProgramData", native), files);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            admission.EnsureOrdinaryActionAllowedAsync(CancellationToken.None));

        Assert.Equal(fileFailure ? expected.DiagnosticCode : "installer.owner_transfer.private_acl_invalid", exception.DiagnosticCode);
        Assert.Equal(fileFailure ? 1 : 0, files.Calls);
        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Fact]
    public async Task MissingProgramDataAncestorIsNotTreatedAsMissingProductState()
    {
        var native = new FakeDirectories();
        native.Observations.Remove(@"C:\ProgramData");
        var files = new RecordingPresence(native);
        var admission = WindowsInstallerOwnerTransferAdmission.CreateForTesting(
            () => WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferForTesting(@"C:\ProgramData", native), files);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => admission.EnsureOrdinaryActionAllowedAsync(CancellationToken.None));

        Assert.Equal(0, files.Calls);
        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.LiveLeases);
    }

    [Fact]
    public async Task PreCancelledAdmissionDoesNotCreateGuardOrOpenObjects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var native = new FakeDirectories();
        var admission = WindowsInstallerOwnerTransferAdmission.CreateForTesting(
            () => throw new InvalidOperationException("Guard creation must remain deferred."), new RecordingPresence(native));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission.EnsureOrdinaryActionAllowedAsync(cancellation.Token));

        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task ReadOnlyPrivateGuardObservesNewlyAppearingStateWithoutCreatingDirectories()
    {
        var native = new FakeDirectories();
        native.Observations.Remove(ProductRoot);
        using var guard = WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferForTesting(@"C:\ProgramData", native);
        await guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None);
        Assert.False(guard.IsProtectedRootPresent);
        Assert.Equal(2, native.LiveLeases);
        foreach ((string path, WindowsInstallerDirectoryObservation observation) in CompletePrivateChain().Observations)
        {
            native.Observations[path] = observation;
        }

        await guard.EnsureProtectedAsync(PrivateRoot, CancellationToken.None);

        Assert.True(guard.IsProtectedRootPresent);
        Assert.Equal(5, native.LiveLeases);
        Assert.Empty(native.CreatedPaths);
    }

    private static FakeDirectories CompletePrivateChain()
    {
        var native = new FakeDirectories();
        native.Observations[AuthorityRoot] = new(true, false, Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity()));
        native.Observations[PrivateRoot] = new(true, false, Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity()));
        return native;
    }

    private static WindowsInstallerDirectorySecuritySnapshot AlterSecurity(WindowsInstallerDirectorySecuritySnapshot security, string condition)
    {
        WindowsInstallerDirectoryAce first = security.AccessEntries[0];
        WindowsInstallerDirectoryAce second = security.AccessEntries[1];
        return condition switch
        {
            "user-readable" => Snapshot(WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(UserSid)),
            "wrong-owner" => security with { OwnerSid = UserSid },
            "unprotected" => security with { DaclProtected = false },
            "no-dacl" => security with { HasDacl = false },
            "deny" => security with { AccessEntries = [first with { Kind = WindowsInstallerDirectoryAceKind.Deny }, second] },
            "inherited" => security with { AccessEntries = [first with { Flags = first.Flags | AceFlags.Inherited }, second] },
            "object-specific" => security with { AccessEntries = [first with { IsObjectSpecific = true }, second] },
            "extra-entry" => security with { AccessEntries = [first, second, first with { Sid = UserSid }] },
            "wrong-rights" => security with { AccessEntries = [first with { AccessMask = (int)FileSystemRights.ReadAndExecute }, second] },
            "duplicate-admin" => security with { AccessEntries = [second, second] },
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
    }

    private static WindowsInstallerDirectorySecuritySnapshot Snapshot(ObjectSecurity security)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        return new(raw.Owner?.Value, raw.DiscretionaryAcl is not null,
            (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
            raw.DiscretionaryAcl!.Cast<CommonAce>().Select(ace => new WindowsInstallerDirectoryAce(
                ace.SecurityIdentifier.Value,
                ace.AceQualifier == AceQualifier.AccessAllowed ? WindowsInstallerDirectoryAceKind.Allow : WindowsInstallerDirectoryAceKind.Deny,
                ace.AccessMask, ace.AceFlags, false)).ToArray());
    }

    private sealed class FakeDirectories : IWindowsInstallerDirectoryNative
    {
        internal Dictionary<string, WindowsInstallerDirectoryObservation> Observations { get; } =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\"] = new(true, false, Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity())),
                [@"C:\ProgramData"] = new(true, false, Snapshot(WindowsInstallerPrivateStateSecurity.CreateDirectorySecurity())),
                [ProductRoot] = new(true, false, Snapshot(WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(UserSid))),
            };
        internal List<string> Calls { get; } = [];
        internal List<string> CreatedPaths { get; } = [];
        internal int LiveLeases { get; private set; }

        public void CreateDirectory(string path, DirectorySecurity security)
        {
            Calls.Add("create:" + path);
            if (!Observations.ContainsKey(path))
            {
                CreatedPaths.Add(path);
                Observations.Add(path, new(true, false, Snapshot(security)));
            }
        }

        public IWindowsInstallerDirectoryLease OpenDirectory(string path)
        {
            Calls.Add("open:" + path);
            if (!Observations.ContainsKey(path))
            {
                throw new Win32Exception(2);
            }

            LiveLeases++;
            return new Lease(this, path);
        }

        private sealed class Lease(FakeDirectories owner, string path) : IWindowsInstallerDirectoryLease
        {
            private bool _disposed;
            public WindowsInstallerDirectoryObservation Observe() => owner.Observations[path];
            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    owner.LiveLeases--;
                }
            }
        }
    }

    private sealed class RecordingPresence(FakeDirectories directories) : IWindowsInstallerPrivateJournalPresenceNative
    {
        internal bool Present { get; init; }
        internal Exception? Failure { get; init; }
        internal int Calls { get; private set; }

        public bool IsPresent(string path, CancellationToken cancellationToken)
        {
            Assert.Equal(Path.Combine(PrivateRoot, InstallerOwnerTransferStateLayout.JournalFileName), path);
            Assert.Equal(5, directories.LiveLeases);
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Failure is not null)
            {
                throw Failure;
            }
            return Present;
        }
    }

    private sealed class RecordingGuard(List<string> calls) : IInstallerTransactionRootGuard, IDisposable
    {
        internal bool Refuse { get; init; }
        internal bool Disposed { get; private set; }
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            Assert.Equal(PrivateRoot, absoluteRootPath);
            calls.Add("guard");
            if (Refuse)
            {
                throw new InstallerProtocolException("installer.owner_transfer.private_acl_invalid");
            }

            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingFiles(List<string> calls) : IWindowsInstallerPrivateJournalFileNative
    {
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            Check(path, "read");
            return Task.FromResult<byte[]?>(null);
        }

        public Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Check(path, "write");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            Check(path, "delete");
            return Task.CompletedTask;
        }

        private void Check(string path, string operation)
        {
            Assert.Equal(Path.Combine(PrivateRoot, InstallerOwnerTransferStateLayout.JournalFileName), path);
            calls.Add(operation);
        }
    }
}
