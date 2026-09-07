using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Retirement;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed partial class WindowsRetiredUninstallAuthorityTests
{
    [Theory]
    [InlineData("global")]
    [InlineData("admission")]
    [InlineData("old-app")]
    [InlineData("release")]
    [InlineData("shared")]
    [InlineData("resources")]
    public async Task FailedAuthorityAcquisitionDrainsAllOwnedResourcesWithoutPreparingRemoval(string failure)
    {
        using var fixture = new Fixture { Failure = failure };
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Factory.CreateAsync(fixture.Request, default));
        Assert.Empty(fixture.Active);
        Assert.Null(fixture.Journal.Bytes);
        Assert.True(fixture.CertificatePresent);
        Assert.Equal(0, fixture.CertificateRemovals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentCandidateOrAccountCannotResumeAnExistingPrivateJournal(bool account)
    {
        using var fixture = new Fixture();
        InstallerRequest other = account ? fixture.Request with { TargetSid = WindowsOwnerTransferAccessFixture.NextSid }
            : fixture.Request with { InstallerPayloadSha256 = new string('f', 64) };
        fixture.Journal.Bytes = InstallerTransactionCodec.Serialize(InstallerTransactionJournal.Create(other));
        byte[] before = fixture.Journal.Bytes.ToArray();
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Factory.CreateAsync(fixture.Request, default));
        Assert.Equal(before, fixture.Journal.Bytes);
        Assert.Empty(fixture.Active);
        Assert.Equal(0, fixture.CertificateRemovals);
    }

    [Fact]
    public async Task CertificateDeletionRequiresIndependentPackageAbsenceAndPreservesPrivateRecoveryOnFailure()
    {
        using var fixture = new Fixture();
        WindowsRetiredUninstallHandoff handoff = await fixture.Factory.CreateAsync(fixture.Request, default);
        await using IWindowsMachineHelperAuthorityLease authority = handoff.Authority;
        InstallerTransactionSnapshot state = handoff.Ready;
        state = await Execute(authority.Session, state, InstallerMachineHelperVerb.Prepare);
        state = await Execute(authority.Session, state, InstallerMachineHelperVerb.Remove);
        InstallerMachineHelperResult failed = await authority.Session.ExecuteAsync(Command(state, InstallerMachineHelperVerb.CommitPackage), default);
        Assert.Equal(InstallerMachineHelperOutcome.Failed, failed.Outcome);
        Assert.Equal(InstallerTransactionPhase.MachineCommitted, InstallerTransactionCodec.Parse(fixture.Journal.Bytes!).Phase);
        Assert.Equal(1, InstallerCertificateOwnershipCodec.Parse(fixture.Archive.Bytes!).ManagedReferenceCount);
        Assert.True(fixture.CertificatePresent);
        Assert.Equal(0, fixture.CertificateRemovals);
    }

    [Fact]
    public async Task CertificateRemovalFailureKeepsReleasedArchiveAndCanResumeWithoutLosingIdentity()
    {
        using var fixture = new Fixture { PackagePresent = false, RefuseCertificateRemoval = true };
        fixture.Seed(2);
        WindowsRetiredUninstallHandoff handoff = await fixture.Factory.CreateAsync(fixture.Request, default);
        await using (IWindowsMachineHelperAuthorityLease authority = handoff.Authority)
        {
            InstallerMachineHelperResult failed = await authority.Session.ExecuteAsync(Command(handoff.Ready, InstallerMachineHelperVerb.CommitPackage), default);
            Assert.Equal(InstallerMachineHelperOutcome.Failed, failed.Outcome);
            Assert.Equal(0, InstallerCertificateOwnershipCodec.Parse(fixture.Archive.Bytes!).ManagedReferenceCount);
            Assert.True(fixture.CertificatePresent);
        }
        fixture.RefuseCertificateRemoval = false;
        WindowsRetiredUninstallHandoff resumed = await fixture.Factory.CreateAsync(fixture.Request, default);
        await using (IWindowsMachineHelperAuthorityLease authority = resumed.Authority)
        {
            InstallerTransactionSnapshot state = await Execute(authority.Session, resumed.Ready, InstallerMachineHelperVerb.CommitPackage);
            state = await Execute(authority.Session, state, InstallerMachineHelperVerb.Verify);
            await Execute(authority.Session, state, InstallerMachineHelperVerb.Clear);
        }
        Assert.Null(fixture.Journal.Bytes);
        Assert.Null(fixture.Archive.Bytes);
        Assert.False(fixture.CertificatePresent);
        Assert.Empty(fixture.Active);
    }

    [Fact]
    public async Task ChangedSharedStateStopsBeforeUninstallAuthorization()
    {
        using var fixture = new Fixture();
        WindowsRetiredUninstallHandoff handoff = await fixture.Factory.CreateAsync(fixture.Request, default);
        await using IWindowsMachineHelperAuthorityLease authority = handoff.Authority;
        fixture.Failure = "shared-reverify";
        InstallerMachineHelperResult failed = await authority.Session.ExecuteAsync(Command(handoff.Ready, InstallerMachineHelperVerb.Prepare), default);
        Assert.Equal(InstallerMachineHelperOutcome.Failed, failed.Outcome);
        Assert.Equal(InstallerTransactionPhase.Prepared, InstallerTransactionCodec.Parse(fixture.Journal.Bytes!).Phase);
        Assert.Equal(0, fixture.CertificateRemovals);
    }

    private static InstallerMachineHelperCommand Command(InstallerTransactionSnapshot state, InstallerMachineHelperVerb verb) =>
        InstallerMachineHelperCommand.Create(InstallerMachineHelperInvocation.Create(verb, state), state);

    private static async Task<InstallerTransactionSnapshot> Execute(InstallerMachineHelperAuthoritySession session,
        InstallerTransactionSnapshot state, InstallerMachineHelperVerb verb)
    {
        InstallerMachineHelperCommand command = Command(state, verb);
        InstallerMachineHelperResult result = await session.ExecuteAsync(command, default);
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
        return result.ValidateAgainst(command);
    }

    private sealed class Fixture : IDisposable
    {
        internal WindowsPayloadFixture Payload { get; } = new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        internal HashSet<string> Active { get; } = [];
        internal List<string> Events { get; } = [];
        internal string? Failure;
        internal bool PackagePresent = true;
        internal bool CertificatePresent = true;
        internal bool RefuseCertificateRemoval;
        internal int CertificateRemovals;
        internal InstallerRequest Request { get; }
        internal MemoryState Journal { get; }
        internal MemoryState Archive { get; }
        internal WindowsRetiredUninstallAuthorityFactory Factory { get; }
        internal Fixture()
        {
            Request = new(InstallerOperation.Uninstall, WindowsOwnerTransferAccessFixture.PreviousSid, false,
                Payload.Manifest.ExpectedPackageVersion, Payload.Manifest.InstallerPayloadSha256);
            Journal = new(this);
            Archive = new(this);
            InstallerCertificateOwnershipLedger ledger = new(1, new string('b', 64), Request.TargetSid,
                new string('C', 40), new string('d', 64), InstallerCertificateStoreLocation.CurrentUser,
                InstallerCertificateStoreName.TrustedPeople, false, true, 1, 1);
            Archive.Bytes = InstallerCertificateOwnershipCodec.Serialize(ledger);
            Factory = new(new GlobalLock(this), new AppLock(this), new Admission(this), new ReleaseVerifier(this),
                _ => new Shared(this), new ResourcesFactory(this));
        }
        internal void Seed(int phase)
        {
            InstallerTransactionJournal journal = InstallerTransactionJournal.Create(Request);
            foreach (InstallerTransactionPhase next in new[] { InstallerTransactionPhase.MachineRemovalAuthorized,
                InstallerTransactionPhase.MachineCommitted, InstallerTransactionPhase.PackageCommitted, InstallerTransactionPhase.Verified }.Take(phase))
            {
                journal = journal.TransitionTo(next);
            }
            Journal.Bytes = InstallerTransactionCodec.Serialize(journal);
            if (phase >= 3) { Archive.Bytes = null; CertificatePresent = false; PackagePresent = false; }
        }
        internal void Hit(string value)
        {
            Events.Add(value);
            if (value == Failure) { throw new InstallerProtocolException("installer.retired_uninstall.injected_failure"); }
        }
        internal void Open(string value) { Hit(value); Assert.True(Active.Add(value)); }
        internal void Close(string value) { Assert.True(Active.Remove(value)); Events.Add("dispose:" + value); }
        internal void RequireHeld()
        {
            foreach (string value in new[] { "global", "old-app", "release" }) { Assert.Contains(value, Active); }
        }
        public void Dispose() { Assert.Empty(Active); Payload.Dispose(); }
    }

    private sealed class MemoryState(Fixture fixture) : IInstallerRetiredUninstallPersistence, IInstallerArchivedCertificatePersistence
    {
        internal byte[]? Bytes;
        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Bytes?.ToArray());
        }
        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            cancellationToken.ThrowIfCancellationRequested();
            Bytes = bytes.ToArray();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            cancellationToken.ThrowIfCancellationRequested();
            Bytes = null;
            return Task.CompletedTask;
        }
    }
    private sealed class GlobalLock(Fixture fixture) : IWindowsInstallerAuthorityLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Open("global");
            return Task.FromResult<IAsyncDisposable>(new AsyncLease(fixture, "global"));
        }
    }
    private sealed class Admission(Fixture fixture) : IWindowsInstallerOwnerTransferAdmission
    {
        public Task EnsureOrdinaryActionAllowedAsync(CancellationToken cancellationToken)
        {
            Assert.Contains("global", fixture.Active);
            fixture.Hit("admission");
            return Task.CompletedTask;
        }
    }
    private sealed class AppLock(Fixture fixture) : IWindowsInstallerApplicationLock
    {
        public IDisposable Acquire(string targetSid, CancellationToken cancellationToken)
        {
            Assert.Equal(fixture.Request.TargetSid, targetSid);
            fixture.Open("old-app");
            return new SyncLease(fixture, "old-app");
        }
    }
    private sealed class AsyncLease(Fixture fixture, string name) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { fixture.Close(name); return ValueTask.CompletedTask; }
    }
    private sealed class SyncLease(Fixture fixture, string name) : IDisposable
    {
        public void Dispose() => fixture.Close(name);
    }
    private sealed class ReleaseVerifier(Fixture fixture) : IInstallerReleaseVerifier
    {
        public Task<IInstallerReleaseLease> VerifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            fixture.Open("release");
            return Task.FromResult<IInstallerReleaseLease>(new ReleaseLease(fixture));
        }
    }
    private sealed class ReleaseLease(Fixture fixture) : IInstallerReleaseLease
    {
        public InstallerReleaseManifest Manifest => fixture.Payload.Manifest;
        public VerifiedInstallerRelease Release => new(Manifest.ExpectedPackageVersion, Manifest.InstallerPayloadSha256,
            false, Manifest.PackageCertificateThumbprint, Manifest.CertificateSha256, false);
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            Assert.Equal(fixture.Request, request);
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Hit("release-reverify");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { fixture.Close("release"); return ValueTask.CompletedTask; }
    }
    private sealed class Shared : IWindowsRetiredUninstallSharedState
    {
        private readonly Fixture _fixture;
        internal Shared(Fixture fixture) { _fixture = fixture; fixture.Open("shared"); }
        public Task ReverifyAsync(CancellationToken cancellationToken)
        {
            _fixture.RequireHeld();
            cancellationToken.ThrowIfCancellationRequested();
            _fixture.Hit("shared-reverify");
            return Task.CompletedTask;
        }
        public void Dispose() => _fixture.Close("shared");
    }
    private sealed class Packages(Fixture fixture) : IWindowsTargetUserPackageCommitInspector
    {
        public void Verify(InstallerRequest request, InstallerReleaseManifest manifest, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            Assert.Equal(fixture.Request, request);
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture.PackagePresent) { throw new InstallerProtocolException("installer.package.removal_verification_failed"); }
        }
    }
    private sealed class Certificates(Fixture fixture) : IInstallerArchivedCertificateRemovalAdapter
    {
        public Task<InstallerCertificatePresence> InspectAsync(InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            Assert.Equal(fixture.Request.TargetSid, ledger.TargetSid);
            return Task.FromResult(fixture.CertificatePresent ? InstallerCertificatePresence.ExactMatch : InstallerCertificatePresence.Missing);
        }
        public Task RemoveExactAsync(InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            Assert.False(fixture.PackagePresent);
            Assert.Equal(0, InstallerCertificateOwnershipCodec.Parse(fixture.Archive.Bytes!).ManagedReferenceCount);
            if (fixture.RefuseCertificateRemoval) { throw new InstallerProtocolException("installer.certificate_archive.removal_failed"); }
            fixture.CertificateRemovals++;
            fixture.CertificatePresent = false;
            return Task.CompletedTask;
        }
    }
    private sealed class ResourcesFactory(Fixture fixture) : IWindowsRetiredUninstallResourcesFactory
    {
        public IWindowsRetiredUninstallResources Create(InstallerRequest request, IInstallerReleaseLease release, IWindowsRetiredUninstallSharedState shared)
        {
            fixture.Open("resources");
            var packages = new Packages(fixture);
            var removal = new InstallerArchivedCertificateRemoval(request.TargetSid,
                new InstallerArchivedCertificateStore(request.TargetSid, fixture.Archive), new Certificates(fixture),
                new WindowsRetiredUninstallCertificateBoundary(request, release, shared, packages));
            return new Resources(fixture, new InstallerRetiredUninstallStore(request.TargetSid, fixture.Journal),
                new WindowsRetiredUninstallOperations(request, release, shared, packages, removal));
        }
    }
    private sealed class Resources(Fixture fixture, IInstallerTransactionStore store, IInstallerMachineHelperOperationExecutor operations)
        : IWindowsRetiredUninstallResources
    {
        public IInstallerTransactionStore Store { get; } = store;
        public IInstallerMachineHelperOperationExecutor Operations { get; } = operations;
        public void Dispose() => fixture.Close("resources");
    }
}
