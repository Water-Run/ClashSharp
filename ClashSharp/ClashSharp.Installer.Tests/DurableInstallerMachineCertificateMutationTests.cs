using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Tests;

public sealed class DurableInstallerMachineCertificateMutationTests
{
    [Fact]
    public async Task InstallationRecordsOwnershipBeforeImportAndUninstallClearsItAfterRemoval()
    {
        var state = new State();
        await RunAsync(state, InstallerOperation.Install);
        Assert.True(state.Ownership?.InstallerOwned);
        Assert.Equal(1, state.Imports);
        await RunAsync(state, InstallerOperation.Uninstall);
        Assert.Null(state.Ownership);
        Assert.Equal(1, state.Removals);
        Assert.Equal(InstallerCertificatePresence.Missing, state.Presence);
    }

    [Fact]
    public async Task PreExistingTrustSurvivesInstallRepairAndUninstall()
    {
        var state = new State { Presence = InstallerCertificatePresence.ExactMatch };
        await RunAsync(state, InstallerOperation.Install);
        await RunAsync(state, InstallerOperation.Repair);
        Assert.False(state.Ownership?.InstallerOwned);
        await RunAsync(state, InstallerOperation.Uninstall);
        Assert.Null(state.Ownership);
        Assert.Equal(0, state.Imports);
        Assert.Equal(0, state.Removals);
        Assert.Equal(InstallerCertificatePresence.ExactMatch, state.Presence);
    }

    [Fact]
    public async Task RepairClaimsMissingPreviouslyExternalTrustBeforeImport()
    {
        var state = new State { Presence = InstallerCertificatePresence.ExactMatch };
        await RunAsync(state, InstallerOperation.Install);
        state.Presence = InstallerCertificatePresence.Missing;
        await RunAsync(state, InstallerOperation.Repair);
        Assert.True(state.Ownership?.InstallerOwned);
        Assert.Equal(1, state.Imports);
        await RunAsync(state, InstallerOperation.Uninstall);
        Assert.Equal(1, state.Removals);
    }

    [Fact]
    public async Task OtherPackageReferencesRetainTrustAndOwnershipUntilTheLastReferenceIsGone()
    {
        var state = new State();
        await RunAsync(state, InstallerOperation.Install);
        state.References = true;
        await RunAsync(state, InstallerOperation.Uninstall);
        Assert.True(state.Ownership?.InstallerOwned);
        Assert.Equal(0, state.Removals);
        state.References = false;
        await RunAsync(state, InstallerOperation.Uninstall);
        Assert.Null(state.Ownership);
        Assert.Equal(1, state.Removals);
    }

    [Fact]
    public async Task ANewInteractiveOwnerReusesMachineEvidenceWithoutRebindingUserTrust()
    {
        var state = new State();
        await RunAsync(state, InstallerOperation.Install);
        var before = state.Ownership;
        await using var lease = InstallerTestData.Lease();
        var nextRequest = InstallerTestData.Request(InstallerOperation.Repair) with { TargetSid = "S-1-5-21-100-200-300-1002" };
        await new DurableInstallerMachineCertificateMutation(state, state, state).ApplyAsync(nextRequest, lease, CancellationToken.None);
        Assert.Equal(before, state.Ownership);
        Assert.Equal(1, state.Imports);
    }

    [Theory]
    [InlineData("write.before", false)]
    [InlineData("write.after", false)]
    [InlineData("import.before", false)]
    [InlineData("import.after", false)]
    [InlineData("remove.before", true)]
    [InlineData("remove.after", true)]
    [InlineData("delete.before", true)]
    [InlineData("delete.after", true)]
    public async Task LostMutationReceiptsReconcileAndANewCapabilityCanResume(string cutPoint, bool uninstall)
    {
        var state = new State();
        if (uninstall)
        {
            await RunAsync(state, InstallerOperation.Install);
        }
        state.CutPoint = cutPoint;
        InstallerOperation operation = uninstall ? InstallerOperation.Uninstall : InstallerOperation.Install;
        Exception? failure = await Record.ExceptionAsync(() => RunAsync(state, operation));
        if (cutPoint.EndsWith("before", StringComparison.Ordinal))
        {
            Assert.IsType<InstallerStateUncertainException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }
        await RunAsync(state, operation);
        Assert.Equal(1, state.Imports);
        Assert.Equal(uninstall ? 1 : 0, state.Removals);
        Assert.Equal(uninstall, state.Ownership is null);
    }

    [Theory]
    [InlineData("write.before")]
    [InlineData("write.after")]
    [InlineData("import.before")]
    [InlineData("import.after")]
    public async Task CancellationRetainsRecoverableEvidenceAndUncancelledReconciliation(string cutPoint)
    {
        var state = new State { CutPoint = cutPoint };
        using var cancellation = new CancellationTokenSource();
        state.CancelAtCut = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(state, InstallerOperation.Install, cancellation.Token));
        state.CancelAtCut = null;
        await RunAsync(state, InstallerOperation.Install);
        Assert.True(state.Ownership?.InstallerOwned);
        Assert.Equal(1, state.Imports);
    }

    [Theory]
    [InlineData(InstallerCertificatePresence.IdentityConflict)]
    [InlineData((InstallerCertificatePresence)99)]
    public async Task ConflictingIdentitiesNeverGrantMutationAuthority(InstallerCertificatePresence presence)
    {
        var state = new State { Presence = presence };
        await Assert.ThrowsAsync<InstallerProtocolException>(() => RunAsync(state, InstallerOperation.Install));
        Assert.Null(state.Ownership);
        Assert.Equal(0, state.Imports);
    }

    [Fact]
    public async Task MissingLedgerDoesNotAuthorizeRemovalOfAnExistingCertificate()
    {
        var state = new State { Presence = InstallerCertificatePresence.ExactMatch };
        await RunAsync(state, InstallerOperation.Uninstall);
        Assert.Equal(0, state.Removals);
        Assert.Null(state.Ownership);
    }

    [Fact]
    public async Task FailedReferenceInventoryPreservesTrustAndOwnership()
    {
        var state = new State();
        await RunAsync(state, InstallerOperation.Install);
        state.FailReferences = true;
        await Assert.ThrowsAsync<IOException>(() => RunAsync(state, InstallerOperation.Uninstall));
        Assert.True(state.Ownership?.InstallerOwned);
        Assert.Equal(0, state.Removals);
    }

    [Fact]
    public async Task AReferenceAppearingBeforeRemovalPreventsDeletion()
    {
        var state = new State();
        await RunAsync(state, InstallerOperation.Install);
        state.ReferenceAfterQuery = 2;
        await RunAsync(state, InstallerOperation.Uninstall);
        Assert.Equal(0, state.Removals);
        Assert.NotNull(state.Ownership);
    }

    [Fact]
    public async Task AReferenceAppearingAfterRemovalRetainsRecoveryEvidence()
    {
        var state = new State();
        await RunAsync(state, InstallerOperation.Install);
        state.ReferenceAfterQuery = 3;
        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => RunAsync(state, InstallerOperation.Uninstall));
        Assert.Equal(1, state.Removals);
        Assert.NotNull(state.Ownership);
        await RunAsync(state, InstallerOperation.Repair);
        Assert.Equal(InstallerCertificatePresence.ExactMatch, state.Presence);
    }

    [Fact]
    public async Task MissingPayloadDoesNotCreateOwnershipOrImport()
    {
        var state = new State();
        await using var lease = InstallerTestData.Lease(InstallerTestData.Release(certificatePayloadAvailable: false));
        var mutation = new DurableInstallerMachineCertificateMutation(state, state, state);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => mutation.ApplyAsync(InstallerTestData.Request(), lease, CancellationToken.None));
        Assert.Null(state.Ownership);
    }

    [Fact]
    public async Task MissingOwnedTrustWithLiveReferencesCannotLoseItsRecoveryRecord()
    {
        var state = new State();
        await RunAsync(state, InstallerOperation.Install);
        var ownership = state.Ownership;
        state.Presence = InstallerCertificatePresence.Missing;
        state.References = true;
        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => RunAsync(state, InstallerOperation.Uninstall));
        Assert.Equal(ownership, state.Ownership);
        await RunAsync(state, InstallerOperation.Repair);
        Assert.Equal(InstallerCertificatePresence.ExactMatch, state.Presence);
    }

    [Fact]
    public async Task MissingOrDifferentOwnershipCannotPassInstallationVerification()
    {
        var state = new State { Presence = InstallerCertificatePresence.ExactMatch };
        await using var lease = InstallerTestData.Lease();
        var mutation = new DurableInstallerMachineCertificateMutation(state, state, state);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => mutation.VerifyAppliedAsync(InstallerTestData.Request(), lease, CancellationToken.None));
        state.Bytes = (InstallerMachineCertificateOwnership.Create(lease.Manifest, false) with { CertificateSha256 = InstallerTestData.OtherHash }).Serialize();
        await Assert.ThrowsAsync<InstallerProtocolException>(() => mutation.ApplyAsync(InstallerTestData.Request(), lease, CancellationToken.None));
        Assert.Equal(0, state.Imports);
    }

    private static async Task RunAsync(State state, InstallerOperation operation, CancellationToken cancellationToken = default)
    {
        await using var lease = InstallerTestData.Lease();
        var mutation = new DurableInstallerMachineCertificateMutation(state, state, state);
        InstallerRequest request = InstallerTestData.Request(operation);
        await mutation.ApplyAsync(request, lease, cancellationToken);
        await mutation.VerifyAppliedAsync(request, lease, cancellationToken);
    }

    private sealed class State : IInstallerMachineCertificatePersistence, IInstallerCertificateStoreAdapter, IInstallerMachineCertificateReferences
    {
        internal byte[]? Bytes { get; set; }
        internal InstallerMachineCertificateOwnership? Ownership => Bytes is null ? null : InstallerMachineCertificateOwnership.Parse(Bytes);
        internal InstallerCertificatePresence Presence { get; set; }
        internal int Imports { get; private set; }
        internal int Removals { get; private set; }
        internal bool References { get; set; }
        internal bool FailReferences { get; set; }
        internal int? ReferenceAfterQuery { get; set; }
        internal int ReferenceQueries { get; private set; }
        internal string? CutPoint { get; set; }
        internal CancellationTokenSource? CancelAtCut { get; set; }

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Bytes?.ToArray());
        }

        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fault("write.before");
            Bytes = bytes.ToArray();
            Fault("write.after");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fault("delete.before");
            Assert.True(Ownership is { InstallerOwned: false } || Presence == InstallerCertificatePresence.Missing);
            Bytes = null;
            Fault("delete.after");
            return Task.CompletedTask;
        }

        public Task<InstallerCertificatePresence> InspectAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Presence);
        }

        public Task ImportAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallerMachineCertificateOwnership ownership = Assert.IsType<InstallerMachineCertificateOwnership>(Ownership);
            Assert.True(ownership.InstallerOwned);
            ownership.RequireManifest(release.Manifest);
            Fault("import.before");
            Imports++;
            Presence = InstallerCertificatePresence.ExactMatch;
            Fault("import.after");
            return Task.CompletedTask;
        }

        public Task RemoveExactAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Ownership?.InstallerOwned);
            Assert.False(References);
            Assert.True(ReferenceQueries >= 2);
            Fault("remove.before");
            Removals++;
            Presence = InstallerCertificatePresence.Missing;
            Fault("remove.after");
            return Task.CompletedTask;
        }

        public Task<bool> HasReferencesAsync(InstallerReleaseManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailReferences)
            {
                throw new IOException("Injected inventory failure.");
            }
            ReferenceQueries++;
            return Task.FromResult(References || (ReferenceAfterQuery is { } threshold && ReferenceQueries >= threshold));
        }

        private void Fault(string point)
        {
            if (CutPoint != point)
            {
                return;
            }
            CutPoint = null;
            if (CancelAtCut is { } cancellation)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            throw new IOException("Injected mutation boundary failure.");
        }
    }
}
