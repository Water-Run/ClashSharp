using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerTransactionRootGuardTests
{
    private const string ProgramDataPath = @"C:\ProgramData";
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string UsersSid = "S-1-5-32-545";

    [Fact]
    public async Task MissingProtectedChainIsCreatedPinnedAndRevalidated()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakeDirectoryNative(TargetSid);
        native.Set(@"C:\", Anchor());
        native.Set(ProgramDataPath, Anchor());
        var guard = WindowsInstallerTransactionRootGuard.CreateForTesting(
            ProgramDataPath,
            TargetSid,
            native);

        await guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None);

        Assert.Equal(
            Path.Combine(ProgramDataPath, "ClashSharp", "Installer", "v2"),
            guard.RootPath);
        Assert.Equal(
            [
                Path.Combine(ProgramDataPath, "ClashSharp"),
                Path.Combine(ProgramDataPath, "ClashSharp", "Installer"),
                Path.Combine(ProgramDataPath, "ClashSharp", "Installer", "v2"),
            ],
            native.CreatedPaths);
        Assert.Equal(5, native.ActiveLeaseCount);

        await guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None);
        Assert.Equal(15, native.ObservationCount);
        Assert.Equal(5, native.ActiveLeaseCount);

        guard.Dispose();
        Assert.Equal(0, native.ActiveLeaseCount);
    }

    [Fact]
    public void CreatedDirectorySecurityIsExactAndTargetUserIsReadOnly()
    {
        WindowsPayloadFixture.AssertWindows11X64();

        DirectorySecurity security =
            WindowsInstallerDirectorySecurityPolicy.CreateProtectedDirectorySecurity(TargetSid);
        SecurityIdentifier owner = Assert.IsType<SecurityIdentifier>(
            security.GetOwner(typeof(SecurityIdentifier)));
        FileSystemAccessRule[] rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.Equal(
            WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
            owner.Value);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(3, rules.Length);
        AssertExactRule(
            rules,
            WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
            FileSystemRights.FullControl);
        AssertExactRule(
            rules,
            WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
            FileSystemRights.FullControl);
        AssertExactRule(rules, TargetSid, FileSystemRights.ReadAndExecute);
        Assert.DoesNotContain(rules, rule =>
            string.Equals(
                ((SecurityIdentifier)rule.IdentityReference).Value,
                TargetSid,
                StringComparison.Ordinal)
            && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete)) != 0);
    }

    [Fact]
    public async Task PrepositionedProtectedDirectoryIsRejectedWithoutAclRepair()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakeDirectoryNative(TargetSid);
        native.Set(@"C:\", Anchor());
        native.Set(ProgramDataPath, Anchor());
        string productRoot = Path.Combine(ProgramDataPath, "ClashSharp");
        string installerRoot = Path.Combine(productRoot, "Installer");
        native.Set(productRoot, Anchor());
        native.Set(installerRoot, Protected(TargetSid, includeUntrustedWriter: true));
        using WindowsInstallerTransactionRootGuard guard =
            WindowsInstallerTransactionRootGuard.CreateForTesting(
                ProgramDataPath,
                TargetSid,
                native);

        await AssertDiagnosticAsync(
            () => guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None),
            "installer.transaction.root_acl_invalid");

        Assert.Equal([productRoot, installerRoot], native.CreatedPaths);
        Assert.Equal(0, native.ActiveLeaseCount);
        Assert.DoesNotContain(
            Path.Combine(installerRoot, "v2"),
            native.CreatedPaths);
    }

    [Fact]
    public async Task RenameAnchorRejectsUntrustedDeleteChildGrant()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        WindowsInstallerDirectorySecurityPolicy.ValidateRenameAnchor(
            Anchor(new WindowsInstallerDirectoryAce(
                UsersSid,
                WindowsInstallerDirectoryAceKind.Allow,
                (int)(FileSystemRights.CreateDirectories
                    | FileSystemRights.CreateFiles
                    | FileSystemRights.WriteAttributes),
                AceFlags.None,
                IsObjectSpecific: false)).Security);
        var native = new FakeDirectoryNative(TargetSid);
        native.Set(@"C:\", Anchor());
        native.Set(
            ProgramDataPath,
            Anchor(new WindowsInstallerDirectoryAce(
                UsersSid,
                WindowsInstallerDirectoryAceKind.Allow,
                (int)FileSystemRights.DeleteSubdirectoriesAndFiles,
                AceFlags.None,
                IsObjectSpecific: false)));
        using WindowsInstallerTransactionRootGuard guard =
            WindowsInstallerTransactionRootGuard.CreateForTesting(
                ProgramDataPath,
                TargetSid,
                native);

        await AssertDiagnosticAsync(
            () => guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None),
            "installer.transaction.root_ancestor_acl_invalid");

        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.ActiveLeaseCount);
    }

    [Fact]
    public async Task ReparseAnchorAndWrongRootFailBeforeProtectedMutation()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakeDirectoryNative(TargetSid);
        native.Set(@"C:\", Anchor());
        native.Set(ProgramDataPath, Anchor() with { IsReparsePoint = true });
        using WindowsInstallerTransactionRootGuard guard =
            WindowsInstallerTransactionRootGuard.CreateForTesting(
                ProgramDataPath,
                TargetSid,
                native);

        await AssertDiagnosticAsync(
            () => guard.EnsureProtectedAsync(
                guard.RootPath + "-other",
                CancellationToken.None),
            "installer.transaction.root_path_invalid");
        Assert.Equal(0, native.OpenCount);

        await AssertDiagnosticAsync(
            () => guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None),
            "installer.transaction.root_ancestor_reparse_rejected");
        Assert.Empty(native.CreatedPaths);
        Assert.Equal(0, native.ActiveLeaseCount);
    }

    [Theory]
    [InlineData("ProgramData")]
    [InlineData(@"C:ProgramData")]
    [InlineData(@"\\server\share\ProgramData")]
    [InlineData(@"\\?\C:\ProgramData")]
    [InlineData(@"C:\ProgramData\..\ProgramData")]
    [InlineData("C:/ProgramData")]
    public void NoncanonicalOrNonlocalProgramDataRootsAreRejected(string path)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsInstallerTransactionRootGuard.CreateForTesting(
                path,
                TargetSid,
                new FakeDirectoryNative(TargetSid)));

        Assert.Equal(
            "installer.transaction.root_path_invalid",
            exception.DiagnosticCode);
    }

    [Fact]
    public async Task SecurityIsReobservedAndDriftFailsClosed()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakeDirectoryNative(TargetSid);
        native.Set(@"C:\", Anchor());
        native.Set(ProgramDataPath, Anchor());
        using WindowsInstallerTransactionRootGuard guard =
            WindowsInstallerTransactionRootGuard.CreateForTesting(
                ProgramDataPath,
                TargetSid,
                native);
        await guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None);
        native.Set(
            guard.RootPath,
            Protected(TargetSid, includeUntrustedWriter: true));

        await AssertDiagnosticAsync(
            () => guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None),
            "installer.transaction.root_acl_invalid");

        Assert.Equal(5, native.ActiveLeaseCount);
    }

    [Fact]
    public async Task InitialDoubleObservationRejectsAclDriftAndReleasesLeases()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakeDirectoryNative(TargetSid);
        native.Set(@"C:\", Anchor());
        native.Set(ProgramDataPath, Anchor());
        string productRoot = Path.Combine(ProgramDataPath, "ClashSharp");
        string installerRoot = Path.Combine(productRoot, "Installer");
        string stateRoot = Path.Combine(installerRoot, "v2");
        native.Set(productRoot, Protected(TargetSid));
        native.Set(installerRoot, Protected(TargetSid));
        native.Set(stateRoot, Protected(TargetSid));
        native.ChangeAfterFirstObservation(
            stateRoot,
            Protected(TargetSid, includeUntrustedWriter: true));
        using WindowsInstallerTransactionRootGuard guard =
            WindowsInstallerTransactionRootGuard.CreateForTesting(
                ProgramDataPath,
                TargetSid,
                native);

        await AssertDiagnosticAsync(
            () => guard.EnsureProtectedAsync(guard.RootPath, CancellationToken.None),
            "installer.transaction.root_acl_invalid");

        Assert.Equal(0, native.ActiveLeaseCount);
    }

    [Fact]
    public async Task PreCancellationDoesNotOpenOrCreateAnything()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakeDirectoryNative(TargetSid);
        using WindowsInstallerTransactionRootGuard guard =
            WindowsInstallerTransactionRootGuard.CreateForTesting(
                ProgramDataPath,
                TargetSid,
                native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guard.EnsureProtectedAsync(guard.RootPath, cancellation.Token));

        Assert.Equal(0, native.OpenCount);
        Assert.Empty(native.CreatedPaths);
    }

    [Fact]
    public void NativeLeaseDoesNotShareDirectoryDeleteAccess()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        string original = Path.Combine(
            Path.GetTempPath(),
            $"ClashSharp-root-lease-{Guid.NewGuid():N}");
        string renamed = original + "-renamed";
        Directory.CreateDirectory(original);
        try
        {
            var native = new WindowsInstallerDirectoryNative();
            IWindowsInstallerDirectoryLease lease = native.OpenDirectory(original);
            try
            {
                WindowsInstallerDirectoryObservation observation = lease.Observe();
                Assert.True(observation.IsDirectory);
                Assert.False(observation.IsReparsePoint);
                Assert.True(observation.Security.HasDacl);
                Assert.NotNull(observation.Security.OwnerSid);
                Assert.NotEmpty(observation.Security.AccessEntries);

                Assert.ThrowsAny<IOException>(() => Directory.Move(original, renamed));
            }
            finally
            {
                lease.Dispose();
            }

            Directory.Move(original, renamed);
        }
        finally
        {
            if (Directory.Exists(original))
            {
                Directory.Delete(original);
            }

            if (Directory.Exists(renamed))
            {
                Directory.Delete(renamed);
            }
        }
    }

    [Fact]
    public async Task ParentReaderAndAuthorityStoresShareOneGuardAndRoot()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            $"ClashSharp-state-composition-{Guid.NewGuid():N}");
        var rootGuard = new RecordingRootGuard();
        using WindowsInstallerProtectedStateStores stores =
            WindowsInstallerProtectedStateStores.CreateForTesting(rootPath, rootGuard);

        InstallerTransactionSnapshot? transaction =
            await stores.TransactionReader.LoadAsync(CancellationToken.None);
        InstallerCertificateOwnershipSnapshot? certificate =
            await stores.CertificateOwnership.LoadAsync(CancellationToken.None);

        Assert.Null(transaction);
        Assert.Null(certificate);
        Assert.Same(stores.Transactions, stores.TransactionReader);
        Assert.Equal(Path.GetFullPath(rootPath), stores.RootPath);
        Assert.Equal([stores.RootPath, stores.RootPath], rootGuard.ObservedRoots);
        Assert.Equal(0, rootGuard.DisposeCount);

        stores.Dispose();
        Assert.Equal(1, rootGuard.DisposeCount);
    }

    private static WindowsInstallerDirectoryObservation Anchor(
        params WindowsInstallerDirectoryAce[] additionalEntries)
    {
        var entries = new List<WindowsInstallerDirectoryAce>
        {
            Ace(
                WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
                FileSystemRights.FullControl,
                AceFlags.None),
            Ace(
                WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
                FileSystemRights.FullControl,
                AceFlags.None),
            Ace(UsersSid, FileSystemRights.ReadAndExecute, AceFlags.None),
        };
        entries.AddRange(additionalEntries);
        return new WindowsInstallerDirectoryObservation(
            IsDirectory: true,
            IsReparsePoint: false,
            new WindowsInstallerDirectorySecuritySnapshot(
                WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
                HasDacl: true,
                DaclProtected: false,
                entries));
    }

    private static WindowsInstallerDirectoryObservation Protected(
        string targetSid,
        bool includeUntrustedWriter = false)
    {
        const AceFlags inheritance = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
        var entries = new List<WindowsInstallerDirectoryAce>
        {
            Ace(
                WindowsInstallerDirectorySecurityPolicy.LocalSystemSid,
                FileSystemRights.FullControl,
                inheritance),
            Ace(
                WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
                FileSystemRights.FullControl,
                inheritance),
            Ace(targetSid, FileSystemRights.ReadAndExecute, inheritance),
        };
        if (includeUntrustedWriter)
        {
            entries.Add(Ace(UsersSid, FileSystemRights.Write, inheritance));
        }

        return new WindowsInstallerDirectoryObservation(
            IsDirectory: true,
            IsReparsePoint: false,
            new WindowsInstallerDirectorySecuritySnapshot(
                WindowsInstallerDirectorySecurityPolicy.AdministratorsSid,
                HasDacl: true,
                DaclProtected: true,
                entries));
    }

    private static WindowsInstallerDirectoryAce Ace(
        string sid,
        FileSystemRights rights,
        AceFlags flags) =>
        new(
            sid,
            WindowsInstallerDirectoryAceKind.Allow,
            (int)rights,
            flags,
            IsObjectSpecific: false);

    private static void AssertExactRule(
        IReadOnlyList<FileSystemAccessRule> rules,
        string sid,
        FileSystemRights rights)
    {
        FileSystemAccessRule rule = Assert.Single(rules, candidate =>
            candidate.IdentityReference is SecurityIdentifier identifier
            && string.Equals(identifier.Value, sid, StringComparison.Ordinal));
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(rights, rule.FileSystemRights);
        Assert.Equal(
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
    }

    private static async Task AssertDiagnosticAsync(
        Func<Task> action,
        string expectedCode)
    {
        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }

    private sealed class FakeDirectoryNative : IWindowsInstallerDirectoryNative
    {
        private readonly Dictionary<string, MutableObservation> _observations =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly string _targetSid;

        internal FakeDirectoryNative(string targetSid)
        {
            _targetSid = targetSid;
        }

        internal List<string> CreatedPaths { get; } = [];

        internal int ActiveLeaseCount { get; private set; }

        internal int ObservationCount { get; private set; }

        internal int OpenCount { get; private set; }

        public void CreateDirectory(string path, DirectorySecurity security)
        {
            ArgumentNullException.ThrowIfNull(security);
            CreatedPaths.Add(path);
            _observations.TryAdd(path, new MutableObservation(Protected(_targetSid)));
        }

        public IWindowsInstallerDirectoryLease OpenDirectory(string path)
        {
            OpenCount++;
            if (!_observations.TryGetValue(path, out MutableObservation? observation))
            {
                throw new DirectoryNotFoundException();
            }

            ActiveLeaseCount++;
            return new FakeLease(this, observation);
        }

        internal void Set(string path, WindowsInstallerDirectoryObservation observation)
        {
            if (_observations.TryGetValue(path, out MutableObservation? existing))
            {
                existing.Value = observation;
            }
            else
            {
                _observations.Add(path, new MutableObservation(observation));
            }
        }

        internal void ChangeAfterFirstObservation(
            string path,
            WindowsInstallerDirectoryObservation observation)
        {
            _observations[path].AfterFirstObservation = observation;
        }

        private sealed class FakeLease : IWindowsInstallerDirectoryLease
        {
            private readonly FakeDirectoryNative _owner;
            private readonly MutableObservation _observation;
            private bool _disposed;

            internal FakeLease(
                FakeDirectoryNative owner,
                MutableObservation observation)
            {
                _owner = owner;
                _observation = observation;
            }

            public WindowsInstallerDirectoryObservation Observe()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _owner.ObservationCount++;
                WindowsInstallerDirectoryObservation current = _observation.Value;
                if (_observation.AfterFirstObservation is not null)
                {
                    _observation.Value = _observation.AfterFirstObservation;
                    _observation.AfterFirstObservation = null;
                }

                return current;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _owner.ActiveLeaseCount--;
                _disposed = true;
            }
        }

        private sealed class MutableObservation(
            WindowsInstallerDirectoryObservation value)
        {
            internal WindowsInstallerDirectoryObservation Value { get; set; } = value;

            internal WindowsInstallerDirectoryObservation? AfterFirstObservation { get; set; }
        }
    }

    private sealed class RecordingRootGuard : IInstallerTransactionRootGuard, IDisposable
    {
        internal List<string> ObservedRoots { get; } = [];

        internal int DisposeCount { get; private set; }

        public Task EnsureProtectedAsync(
            string absoluteRootPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedRoots.Add(absoluteRootPath);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
