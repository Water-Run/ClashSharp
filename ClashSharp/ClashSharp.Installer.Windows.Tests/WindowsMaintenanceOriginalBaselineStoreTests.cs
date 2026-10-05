using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceOriginalBaselineStoreTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Credential = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task CompleteFirstCaptureSurvivesNewStoreAndExactReplayNeverWritesAgain()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsMaintenanceOriginalBaseline baseline = Baseline(plan);
        var files = new Files();
        using (WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files))
        {
            await store.CaptureOnceAsync(plan, baseline, CancellationToken.None);
        }
        using (WindowsMaintenanceOriginalBaselineStore reopened = Store(fixture.RootDirectory, files))
        {
            await reopened.CaptureOnceAsync(plan, baseline, CancellationToken.None);
            WindowsMaintenanceOriginalBaseline? observed = await reopened.ReadAsync(plan,
                baseline.Service.Intent.TransitionTo(InstallerTransactionPhase.MachineReserved), CancellationToken.None);
            Assert.Equal(baseline.Service.Intent, observed?.Service.Intent);
            Assert.True(baseline.Contents.Matches(observed!.Contents));
        }
        Assert.Equal(1, files.Writes);
        Assert.Equal(0, files.Deletes);
        files.AssertTemporaryBuffersCleared();
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("service")]
    [InlineData("package")]
    [InlineData("package-contents")]
    [InlineData("payload")]
    [InlineData("association")]
    [InlineData("trust")]
    public async Task DifferentObservationCannotReplaceAnyFirstEvidenceCategory(string category)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsMaintenanceOriginalBaseline original = Baseline(plan);
        WindowsServicePreparationBaseline service = original.Service;
        WindowsMaintenanceOriginalContents contents = original.Contents;
        if (category == "transaction")
        {
            service = new(service.Schema, service.Intent with { TransactionId = new string('b', 64) }, service.Service);
        }
        else if (category == "service")
        {
            service = new(service.Schema, service.Intent, service.Service with { RuntimeState = WindowsServiceRuntimeState.Stopped });
        }
        else
        {
            InstallerInstalledPackage package = contents.Package;
            string packageContents = contents.PackageContentsSha256;
            WindowsMaintenanceFileFingerprint[] payload = contents.Files.ToArray();
            string association = contents.AssociationSha256;
            WindowsMaintenanceTrustFingerprint trust = contents.Trust;
            if (category == "package")
            {
                const string version = "1.2.3.3";
                package = package with { Version = version, PackageFullName = $"{package.Name}_{version}_x64__{package.PublisherId}" };
            }
            else if (category == "payload") { payload[0] = payload[0] with { Sha256 = new string('b', 64) }; }
            else if (category == "package-contents") { packageContents = new string('b', 64); }
            else if (category == "association") { association = new string('b', 64); }
            else { trust = trust with { MachineLedgerSha256 = new string('b', 64) }; }
            contents = new(package, contents.Footprint, packageContents, payload, association, trust);
        }
        var replacement = new WindowsMaintenanceOriginalBaseline(original.Schema, service, contents);
        var files = new Files { Bytes = original.Serialize() };
        byte[] before = files.Bytes.ToArray();
        using WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.CaptureOnceAsync(plan, replacement, CancellationToken.None));

        Assert.Equal("installer.recovery.original_baseline_conflict", failure.DiagnosticCode);
        Assert.Equal(before, files.Bytes);
        Assert.Equal(0, files.Writes);
        files.AssertTemporaryBuffersCleared();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostWriteReplyUsesActualPrivateStateAndKeepsUncertainEvidence(bool committed)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var files = new Files { FailWrite = true, ApplyWrite = committed };
        using WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files);

        Exception? failure = await Record.ExceptionAsync(() => store.CaptureOnceAsync(plan, Baseline(plan), CancellationToken.None));

        if (committed) { Assert.Null(failure); Assert.NotNull(files.Bytes); }
        else { Assert.IsType<InstallerStateUncertainException>(failure); Assert.Null(files.Bytes); }
        Assert.Equal(1, files.Writes);
        Assert.Equal(0, files.Deletes);
        files.AssertTemporaryBuffersCleared();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterWriteAdmissionObservesActualCommit(bool committed)
    {
        using var fixture = Fixture();
        using var cancellation = new CancellationTokenSource();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var files = new Files { ApplyWrite = committed, CancelAfterWrite = cancellation };
        using WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files);

        Exception? failure = await Record.ExceptionAsync(() => store.CaptureOnceAsync(plan, Baseline(plan), cancellation.Token));

        if (committed) { Assert.Null(failure); Assert.NotNull(files.Bytes); }
        else { Assert.IsType<OperationCanceledException>(failure); Assert.Null(files.Bytes); }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, files.Writes);
        files.AssertTemporaryBuffersCleared();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("authority")]
    [InlineData("different")]
    public async Task FailedPostWriteProofCannotReturnSuccessOrDestroyTheRecord(string failure)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var authority = new Authority();
        var files = new Files();
        files.AfterWrite = () =>
        {
            if (failure == "read") { files.FailRead = true; }
            else if (failure == "authority") { authority.Expired = true; }
            else
            {
                WindowsMaintenanceOriginalBaseline different = Baseline(plan);
                files.Bytes = new WindowsMaintenanceOriginalBaseline(different.Schema,
                    new WindowsServicePreparationBaseline(different.Service.Schema,
                        different.Service.Intent with { TransactionId = new string('b', 64) }, different.Service.Service),
                    different.Contents).Serialize();
            }
        };
        using WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files, authority);

        InstallerStateUncertainException observed = await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            store.CaptureOnceAsync(plan, Baseline(plan), CancellationToken.None));

        Assert.Equal("installer.recovery.original_baseline_write_uncertain", observed.DiagnosticCode);
        Assert.NotNull(files.Bytes);
        Assert.Equal(1, files.Writes);
        Assert.Equal(0, files.Deletes);
        files.AssertTemporaryBuffersCleared();
    }

    [Fact]
    public async Task ExpiredAuthorityRefusesBeforeReadingOrWritingAnyEvidence()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var files = new Files();
        using WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files, new Authority { Expired = true });

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.CaptureOnceAsync(plan, Baseline(plan), CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.ReadAsync(plan, Baseline(plan).Service.Intent, CancellationToken.None));

        Assert.Equal(0, files.Reads);
        Assert.Equal(0, files.Writes);
    }

    [Fact]
    public async Task MissingRootObservationNeverCreatesDirectoryOrReadsFile()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var files = new Files();
        var writeGuard = new Guard();
        using var store = new WindowsMaintenanceOriginalBaselineStore(fixture.RootDirectory, new Guard(), writeGuard,
            () => false, files, new Authority());

        Assert.Null(await store.ReadAsync(plan, Baseline(plan).Service.Intent, CancellationToken.None));

        Assert.Equal(0, writeGuard.Calls);
        Assert.Equal(0, files.Reads);
    }

    [Fact]
    public async Task EvidenceAppearingDuringRootPreparationCannotBeOverwritten()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsMaintenanceOriginalBaseline original = Baseline(plan);
        var files = new Files();
        var write = new Guard { AfterEnsure = () => files.Bytes = original.Serialize() };
        using var store = new WindowsMaintenanceOriginalBaselineStore(fixture.RootDirectory, new Guard(), write,
            () => true, files, new Authority());

        await store.CaptureOnceAsync(plan, original, CancellationToken.None);

        Assert.Equal(0, files.Writes);
        files.AssertTemporaryBuffersCleared();
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("committed")]
    public async Task RecoveryReadRejectsDifferentOrAlreadyCommittedPublicIntent(string category)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsMaintenanceOriginalBaseline original = Baseline(plan);
        InstallerTransactionJournal current = category == "transaction"
            ? original.Service.Intent with { TransactionId = new string('b', 64) }
            : original.Service.Intent.TransitionTo(InstallerTransactionPhase.MachineReserved).TransitionTo(InstallerTransactionPhase.PackageCommitted);
        var files = new Files { Bytes = original.Serialize() };
        using WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.ReadAsync(plan, current, CancellationToken.None));

        Assert.Equal(0, files.Writes);
        files.AssertTemporaryBuffersCleared();
    }

    [Fact]
    public async Task CorruptExistingEvidenceFailsBeforeAnyWrite()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var files = new Files { Bytes = "{}"u8.ToArray() };
        using WindowsMaintenanceOriginalBaselineStore store = Store(fixture.RootDirectory, files);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.CaptureOnceAsync(plan, Baseline(plan), CancellationToken.None));

        Assert.Equal(0, files.Writes);
        Assert.Equal("{}"u8.ToArray(), files.Bytes);
        files.AssertTemporaryBuffersCleared();
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
    private static WindowsMachineDeploymentPlan Plan(WindowsPayloadFixture fixture) => WindowsMachineDeploymentPlan.Create(
        fixture.Request(InstallerOperation.Repair, Owner), fixture.Manifest, InstallerMachineAssociation.Create(Owner, Credential),
        @"C:\Program Files", @"C:\ProgramData", @"C:\Users\owner");
    private static WindowsMaintenanceOriginalBaseline Baseline(WindowsMachineDeploymentPlan plan)
    {
        var identity = plan.Manifest.PackageIdentity;
        var package = new InstallerInstalledPackage(identity.Name, identity.Publisher, identity.PublisherId,
            plan.Manifest.ExpectedPackageVersion, identity.Architecture, identity.ResourceId, identity.PackageFullName, identity.PackageFamilyName, true);
        var contents = new WindowsMaintenanceOriginalContents(package, new WindowsPackageFootprint(4, Hash, 8, Hash), Hash,
            plan.PayloadTargets.Select(target => new WindowsMaintenanceFileFingerprint(target.Source.Path,
                target.RelativeTargetPath.Replace('\\', '/'), target.Source.Length, target.Source.Sha256)).ToArray(), Hash,
            new WindowsMaintenanceTrustFingerprint(Hash, Hash, plan.Manifest.PackageCertificateThumbprint, plan.Manifest.CertificateSha256));
        WindowsServicePreparationBaseline service = WindowsServicePreparationBaseline.Capture(plan, InstallerTransactionJournal.Create(plan.Request),
            new WindowsServiceSnapshot(plan.Service, WindowsServiceRuntimeState.Running, WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(Owner)));
        return new(WindowsMaintenanceOriginalBaseline.CurrentSchema, service, contents);
    }
    private static WindowsMaintenanceOriginalBaselineStore Store(string root, Files files, Authority? authority = null) =>
        new(root, new Guard(), new Guard(), () => true, files, authority ?? new Authority());

    private sealed class Guard : IInstallerTransactionRootGuard
    {
        internal int Calls { get; private set; }
        internal Action? AfterEnsure { get; init; }
        public Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            AfterEnsure?.Invoke();
            return Task.CompletedTask;
        }
    }
    private sealed class Authority : IWindowsMaintenanceRecoveryAuthorityLease
    {
        internal bool Expired { get; set; }
        public Task ReverifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Expired ? throw new InstallerProtocolException("installer.recovery.authority_expired") : Task.CompletedTask;
        }
    }
    private sealed class Files : IWindowsInstallerPrivateJournalFileNative
    {
        internal byte[]? Bytes { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal int Deletes { get; private set; }
        internal bool ApplyWrite { get; init; } = true;
        internal bool FailWrite { get; init; }
        internal bool FailRead { get; set; }
        internal CancellationTokenSource? CancelAfterWrite { get; init; }
        internal Action? AfterWrite { get; set; }
        private readonly List<byte[]> _temporaryReads = [];
        private byte[]? _writeBuffer;

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WindowsMaintenanceOriginalBaseline.FileName, Path.GetFileName(path));
            Reads++;
            if (FailRead) { throw new IOException("private observation unavailable"); }
            byte[]? bytes = Bytes?.ToArray();
            if (bytes is not null) { _temporaryReads.Add(bytes); }
            return Task.FromResult(bytes);
        }
        public Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WindowsMaintenanceOriginalBaseline.FileName, Path.GetFileName(path));
            Writes++;
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> array));
            _writeBuffer = array.Array;
            if (ApplyWrite) { Bytes = bytes.ToArray(); }
            AfterWrite?.Invoke();
            if (CancelAfterWrite is { } cancel) { cancel.Cancel(); throw new OperationCanceledException(cancellationToken); }
            if (FailWrite) { throw new IOException("private write receipt lost"); }
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            Deletes++;
            throw new InvalidOperationException("Baseline deletion is not an available recovery capability.");
        }
        internal void AssertTemporaryBuffersCleared()
        {
            foreach (byte[] buffer in _temporaryReads) { Assert.True(buffer.All(value => value == 0)); }
            if (_writeBuffer is not null) { Assert.True(_writeBuffer.All(value => value == 0)); }
        }
    }
}
