using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

internal sealed class MaintenanceRecoveryFixture : IDisposable
{
    internal const string Owner = "S-1-5-21-100-200-300-1001";
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly WindowsPayloadFixture _payload = new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
    internal MaintenanceRecoveryFixture()
    {
        Request = _payload.Request(InstallerOperation.Repair, Owner);
        Plan = WindowsMachineDeploymentPlan.Create(Request, _payload.Manifest, InstallerMachineAssociation.Create(Owner, Hash),
            @"C:\Program Files", @"C:\ProgramData", @"C:\Users\owner");
    }
    internal InstallerRequest Request { get; }
    internal WindowsMachineDeploymentPlan Plan { get; }
    internal Files PrivateFiles { get; } = new();
    internal PublicStore Public { get; } = new();
    internal Authority Outer { get; } = new();
    internal List<string> Events { get; } = [];
    internal bool OriginalUnavailable { get; set; }
    internal bool OriginalChanged { get; set; }
    internal bool ServicePrepared { get; set; }
    internal bool CandidateInstalled { get; set; }
    internal int Captures { get; set; }
    internal int Restores { get; set; }
    internal int ActiveOriginalSessions { get; set; }
    internal CancellationTokenSource? CancelAfterRestore { get; set; }
    internal bool LoseRestoreReply { get; set; }
    internal bool CertificateConflict { get; set; }

    internal WindowsMaintenanceOriginalBaseline Baseline(InstallerTransactionJournal intent)
    {
        var identity = Plan.Manifest.PackageIdentity;
        var package = new InstallerInstalledPackage(identity.Name, identity.Publisher, identity.PublisherId,
            Plan.Manifest.ExpectedPackageVersion, identity.Architecture, identity.ResourceId, identity.PackageFullName, identity.PackageFamilyName, true);
        var contents = new WindowsMaintenanceOriginalContents(package, new WindowsPackageFootprint(1, Hash, 1, Hash), Hash,
            Plan.PayloadTargets.Select(target => new WindowsMaintenanceFileFingerprint(target.Source.Path,
                target.RelativeTargetPath.Replace('\\', '/'), target.Source.Length, target.Source.Sha256)).ToArray(), Hash,
            new WindowsMaintenanceTrustFingerprint(Hash, Hash, Plan.Manifest.PackageCertificateThumbprint, Plan.Manifest.CertificateSha256));
        var service = WindowsServicePreparationBaseline.Capture(Plan, intent,
            new WindowsServiceSnapshot(Plan.Service, WindowsServiceRuntimeState.Running, WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(Owner)));
        return new(WindowsMaintenanceOriginalBaseline.CurrentSchema, service, contents);
    }
    internal WindowsMaintenanceRecoveryRecord Captured(bool available = true)
    {
        InstallerTransactionJournal intent = InstallerTransactionJournal.Create(Request);
        return WindowsMaintenanceRecoveryRecord.Capture(intent, available ? Baseline(intent) : null,
            available ? null : "installer.recovery.original_capture_unavailable");
    }
    internal WindowsMaintenanceRecoveryStore Store(Func<bool>? rootPresent = null) => new(_payload.RootDirectory,
        new Guard(), new Guard(), rootPresent ?? (() => true), PrivateFiles, Outer, Outer, Public);
    internal WindowsMaintenanceRecoveryRecord? Private => PrivateFiles.Bytes is null ? null : WindowsMaintenanceRecoveryRecord.Parse(PrivateFiles.Bytes);
    internal WindowsMachineHelperOperationExecutor Executor()
    {
        var backend = new Backend(this);
        var recovery = new WindowsMaintenanceRecoveryCoordinator(Store(), Public, backend);
        return new(backend, backend, backend, backend, backend, backend, backend, recovery);
    }
    internal static InstallerMachineHelperCommand Command(InstallerMachineHelperVerb verb, InstallerTransactionSnapshot state) =>
        InstallerMachineHelperCommand.Create(InstallerMachineHelperInvocation.Create(verb, state), state);
    internal static InstallerTransactionSnapshot CandidateTerminal(InstallerTransactionJournal intent) => InstallerTransactionSnapshot.Create(intent
        .TransitionTo(InstallerTransactionPhase.MachineReserved).TransitionTo(InstallerTransactionPhase.PackageCommitted)
        .TransitionTo(InstallerTransactionPhase.MachineCommitted).TransitionTo(InstallerTransactionPhase.Verified));
    internal Task<InstallerMachineHelperAuthoritySession> SessionAsync(InstallerMachineHelperCommand first, WindowsMachineHelperOperationExecutor executor) =>
        InstallerMachineHelperAuthoritySession.CreateAsync(first.ToInvocation(), Owner, Public, executor, CancellationToken.None);
    public void Dispose() { PrivateFiles.Clear(); _payload.Dispose(); }

    internal sealed class Authority : IWindowsInstallerAuthorityLease, IWindowsInstallerApplicationLease
    {
        internal bool Expired { get; set; }
        public string TargetSid => Owner;
        public Task ReverifyAsync(CancellationToken cancellationToken) { Reverify(cancellationToken); return Task.CompletedTask; }
        public void Reverify(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Expired) { throw new InstallerProtocolException("installer.recovery.authority_expired"); }
        }
        public void Dispose() => Expired = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    internal sealed class Guard : IInstallerTransactionRootGuard
    {
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
    internal sealed class Files : IWindowsInstallerPrivateJournalFileNative
    {
        internal byte[]? Bytes { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal int Deletes { get; private set; }
        internal bool ApplyWrite { get; set; } = true;
        internal bool FailWrite { get; set; }
        internal bool FailDelete { get; set; }
        internal Action? AfterRead { get; set; }
        internal Action? AfterWrite { get; set; }
        internal Action? BeforeWrite { get; set; }
        private readonly List<byte[]> _returned = [];
        private readonly List<byte[]> _writtenBuffers = [];
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WindowsMaintenanceRecoveryRecord.FileName, Path.GetFileName(path));
            Reads++;
            byte[]? bytes = Bytes?.ToArray();
            if (bytes is not null) { _returned.Add(bytes); }
            AfterRead?.Invoke();
            return Task.FromResult(bytes);
        }
        public Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WindowsMaintenanceRecoveryRecord.FileName, Path.GetFileName(path));
            BeforeWrite?.Invoke();
            Writes++;
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> array));
            _writtenBuffers.Add(array.Array!);
            if (ApplyWrite) { Clear(); Bytes = bytes.ToArray(); }
            AfterWrite?.Invoke();
            if (FailWrite) { throw new IOException("private write acknowledgement lost"); }
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deletes++;
            Clear();
            if (FailDelete) { throw new IOException("private delete acknowledgement lost"); }
            return Task.CompletedTask;
        }
        internal void AssertBuffersCleared()
        {
            foreach (byte[] bytes in _returned.Concat(_writtenBuffers)) { Assert.True(bytes.All(value => value == 0)); }
        }
        internal void Clear() { if (Bytes is not null) { CryptographicOperations.ZeroMemory(Bytes); } Bytes = null; }
    }
    internal sealed class PublicStore : IInstallerTransactionStore, IInstallerOriginalRestorationStore
    {
        internal InstallerTransactionSnapshot? Current { get; set; }
        internal Action<InstallerTransactionJournal>? BeforeSave { get; set; }
        internal Action? BeforeClear { get; set; }
        internal bool LoseClearReply { get; set; }
        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }
        public Task<InstallerTransactionSnapshot> SaveAsync(InstallerTransactionJournal journal, string? expectedCurrentHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Current?.ContentHash, expectedCurrentHash);
            if (Current is not null) { Assert.Equal(Current.Journal.TransitionTo(journal.Phase), journal); }
            else { Assert.Equal(InstallerTransactionPhase.Prepared, journal.Phase); }
            BeforeSave?.Invoke(journal);
            Current = InstallerTransactionSnapshot.Create(journal);
            return Task.FromResult(Current);
        }
        public Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken) =>
            ClearAsync(transactionId, expectedCurrentHash, InstallerTransactionPhase.Verified, cancellationToken);
        public Task ClearOriginalRestoredAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken) =>
            ClearAsync(transactionId, expectedCurrentHash, InstallerTransactionPhase.OriginalRestored, cancellationToken);
        private Task ClearAsync(string transactionId, string expectedCurrentHash, InstallerTransactionPhase phase, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(Current);
            Assert.Equal(transactionId, Current.Journal.TransactionId);
            Assert.Equal(expectedCurrentHash, Current.ContentHash);
            Assert.Equal(phase, Current.Journal.Phase);
            BeforeClear?.Invoke();
            Current = null;
            if (LoseClearReply) { LoseClearReply = false; throw new IOException("public clear acknowledgement lost"); }
            return Task.CompletedTask;
        }
    }

    private sealed class Backend(MaintenanceRecoveryFixture owner) : IInstallerReleaseVerifier, IInstallerCertificateMutation,
        IInstallerCertificateMutationVerifier, IInstallerCertificatePreflight, IWindowsPackageDeploymentPreflight,
        IWindowsTargetUserPackageCommitInspector, IWindowsMachineHelperMachineOperations, IWindowsMaintenanceOriginalSessionFactory
    {
        public Task<IInstallerReleaseLease> VerifyAsync(InstallerRequest request, CancellationToken cancellationToken) => Task.FromResult<IInstallerReleaseLease>(new ReleaseLease(owner.Plan.Manifest));
        public Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VerifyAppliedAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VerifyCanInstallAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) =>
            owner.CertificateConflict ? throw new InstallerProtocolException("installer.machine_certificate.ownership_conflict") : Task.CompletedTask;
        public Task VerifyCanDeployAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Verify(InstallerRequest request, InstallerReleaseManifest manifest, CancellationToken cancellationToken) => Assert.True(owner.CandidateInstalled);
        public Task PrepareAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
        {
            if (disposition == InstallerMachineHelperSessionDisposition.Execute)
            {
                Assert.Equal(WindowsMaintenanceRecoveryStage.ContinueCandidate, owner.Private?.Stage);
                owner.Events.Add("machine.prepare");
                owner.ServicePrepared = true;
            }
            return Task.CompletedTask;
        }
        public Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
        {
            Assert.Equal(WindowsMaintenanceRecoveryStage.ContinueCandidate, owner.Private?.Stage);
            Assert.Equal(0, owner.ActiveOriginalSessions);
            owner.ServicePrepared = false;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task VerifyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            Assert.True(owner.CandidateInstalled);
            Assert.False(owner.ServicePrepared);
            return Task.CompletedTask;
        }
        public Task<IWindowsMaintenanceOriginalSession> OpenAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.OriginalUnavailable) { throw new InstallerProtocolException("installer.recovery.original_payload_changed"); }
            owner.ActiveOriginalSessions++;
            return Task.FromResult<IWindowsMaintenanceOriginalSession>(new OriginalSession(owner));
        }
    }
    private sealed class OriginalSession(MaintenanceRecoveryFixture owner) : IWindowsMaintenanceOriginalSession
    {
        private bool _disposed;
        public Task<WindowsMaintenanceOriginalBaseline> CaptureAsync(InstallerTransactionJournal intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Null(owner.Public.Current);
            Assert.False(owner.ServicePrepared);
            owner.Captures++;
            owner.Events.Add("original.capture");
            return Task.FromResult(owner.Baseline(intent));
        }
        public Task VerifyOriginalAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source, InstallerTransactionSnapshot? expectedPublic, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(_disposed);
            Assert.Equal(expectedPublic, owner.Public.Current);
            baseline.RequireBoundary(owner.Plan, source.Journal);
            if (owner.OriginalChanged) { throw new InstallerProtocolException("installer.recovery.original_state_changed"); }
            return Task.CompletedTask;
        }
        public Task RestoreServiceAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WindowsMaintenanceRecoveryStage.PreserveOriginal, owner.Private?.Stage);
            if (owner.ServicePrepared) { owner.Restores++; }
            owner.Events.Add("service.restore");
            owner.ServicePrepared = false;
            owner.CancelAfterRestore?.Cancel();
            if (owner.LoseRestoreReply) { owner.LoseRestoreReply = false; throw new InstallerStateUncertainException("installer.recovery.service_restoration_unverified"); }
            return Task.CompletedTask;
        }
        public async Task VerifyRestoredAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source, InstallerTransactionSnapshot? expectedPublic, CancellationToken cancellationToken)
        {
            await VerifyOriginalAsync(baseline, source, expectedPublic, cancellationToken);
            Assert.False(owner.ServicePrepared);
        }
        public void Dispose() { if (_disposed) { return; } _disposed = true; owner.ActiveOriginalSessions--; }
    }
    private sealed class ReleaseLease(InstallerReleaseManifest manifest) : IInstallerReleaseLease
    {
        public InstallerReleaseManifest Manifest { get; } = manifest;
        public VerifiedInstallerRelease Release { get; } = manifest.CreateVerifiedRelease(true, true);
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
