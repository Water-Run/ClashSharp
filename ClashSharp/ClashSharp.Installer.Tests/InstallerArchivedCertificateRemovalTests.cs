using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using static ClashSharp.Installer.Tests.InstallerArchivedCertificateStoreTests;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerArchivedCertificateRemovalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingAndAlreadyReleasedOwnershipResumeWithoutChangingTheCertificateIdentity(
        bool referenceReleased, bool certificateMissing)
    {
        var fixture = new Fixture(referenceReleased: referenceReleased, certificateMissing: certificateMissing);
        Assert.Equal(0, fixture.Boundary.Calls);

        await fixture.Removal.RemoveAsync(CancellationToken.None);
        await fixture.Removal.VerifyCompletedAsync(CancellationToken.None);
        await fixture.Removal.RemoveAsync(CancellationToken.None);

        Assert.Null(fixture.Files.Bytes);
        Assert.Equal(certificateMissing ? 0 : 1, fixture.Certificates.Removals);
        Assert.Equal(referenceReleased ? 0 : 1, fixture.Files.Writes);
        Assert.Equal(1, fixture.Files.Deletes);
        Assert.All(fixture.Certificates.Observed, ledger =>
        {
            Assert.Equal(fixture.Original.CertificateThumbprint, ledger.CertificateThumbprint);
            Assert.Equal(fixture.Original.CertificateSha256, ledger.CertificateSha256);
            Assert.Equal(fixture.Original.TargetSid, ledger.TargetSid);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreExistingCertificateIsPreservedWithoutAnyStoreInspectionOrDeletion(bool referenceReleased)
    {
        var fixture = new Fixture(preExisting: true, referenceReleased: referenceReleased);
        fixture.Certificates.Presence = InstallerCertificatePresence.IdentityConflict;

        await fixture.Removal.RemoveAsync(CancellationToken.None);

        Assert.Null(fixture.Files.Bytes);
        Assert.Empty(fixture.Certificates.Observed);
        Assert.Equal(0, fixture.Certificates.Removals);
        Assert.Equal(InstallerCertificatePresence.IdentityConflict, fixture.Certificates.Presence);
    }

    [Fact]
    public async Task NoArchiveGrantsNoCertificateDeletionAuthority()
    {
        var fixture = new Fixture();
        fixture.Files.Bytes = null;
        await fixture.Removal.RemoveAsync(CancellationToken.None);
        Assert.Empty(fixture.Certificates.Observed);
        Assert.Equal(0, fixture.Files.Writes + fixture.Files.Deletes);
        Assert.Equal(2, fixture.Boundary.Calls);
    }

    [Theory]
    [InlineData(InstallerCertificatePresence.IdentityConflict)]
    [InlineData((InstallerCertificatePresence)99)]
    public async Task ConflictingIdentityCannotReleaseTheReference(InstallerCertificatePresence presence)
    {
        var fixture = new Fixture();
        fixture.Certificates.Presence = presence;
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Removal.RemoveAsync(CancellationToken.None));
        Assert.Equal("installer.certificate_archive.identity_conflict", error.DiagnosticCode);
        Assert.Equal(0, fixture.Files.Writes + fixture.Files.Deletes + fixture.Certificates.Removals);
        Assert.Equal(fixture.Original, (await fixture.Store.LoadAsync(CancellationToken.None))?.Ledger);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ChangedPackageOrRetiredOwnershipBoundaryStopsFurtherMutation(int rejectedCall)
    {
        var fixture = new Fixture();
        fixture.Boundary.RejectedCall = rejectedCall;

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Removal.RemoveAsync(CancellationToken.None));

        Assert.Equal(0, fixture.Files.Deletes);
        Assert.Equal(rejectedCall <= 2 ? 1 : 0, (await fixture.Store.LoadAsync(CancellationToken.None))?.Ledger.ManagedReferenceCount);
        Assert.Equal(rejectedCall == 5 ? 1 : 0, fixture.Certificates.Removals);
    }

    [Fact]
    public async Task AnotherAccountsArchiveIsRejectedBeforeCertificateStoreAccess()
    {
        var fixture = new Fixture();
        fixture.Files.Bytes = InstallerCertificateOwnershipCodec.Serialize(
            fixture.Original with { TargetSid = "S-1-5-21-100-200-300-1002" });
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Removal.RemoveAsync(CancellationToken.None));
        Assert.Empty(fixture.Certificates.Observed);
        Assert.Equal(0, fixture.Files.Writes + fixture.Files.Deletes);
    }

    [Fact]
    public async Task CertificateDeletionRequiresTheDurablyUnreferencedExactArchive()
    {
        var fixture = new Fixture();
        fixture.Certificates.BeforeRemove = async (ledger, token) =>
        {
            InstallerCertificateOwnershipSnapshot current = Assert.IsType<InstallerCertificateOwnershipSnapshot>(
                await fixture.Store.LoadAsync(token));
            Assert.Equal(0, current.Ledger.ManagedReferenceCount);
            Assert.Equal(ledger, current.Ledger);
            Assert.Equal(fixture.Original.PrepareRemoval(), ledger);
        };

        await fixture.Removal.RemoveAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Certificates.Removals);
        Assert.Null(fixture.Files.Bytes);
    }

    [Fact]
    public async Task CertificateStillPresentAfterDeletionRetainsRecoveryEvidence()
    {
        var fixture = new Fixture();
        fixture.Certificates.DeleteChangesPresence = false;
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Removal.RemoveAsync(CancellationToken.None));
        Assert.Equal("installer.certificate_archive.removal_verification_failed", error.DiagnosticCode);
        Assert.Equal(fixture.Original.PrepareRemoval(), (await fixture.Store.LoadAsync(CancellationToken.None))?.Ledger);
        Assert.Equal(0, fixture.Files.Deletes);
    }

    [Fact]
    public async Task ArchiveReplacementAfterCertificateDeletionIsNeverCleared()
    {
        var fixture = new Fixture();
        InstallerCertificateOwnershipLedger replacement = fixture.Original with { LedgerId = new string('e', 64) };
        fixture.Certificates.AfterRemove = _ =>
        {
            fixture.Files.Bytes = InstallerCertificateOwnershipCodec.Serialize(replacement);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Removal.RemoveAsync(CancellationToken.None));

        Assert.Equal(replacement, (await fixture.Store.LoadAsync(CancellationToken.None))?.Ledger);
        Assert.Equal(0, fixture.Files.Deletes);
    }

    [Fact]
    public async Task CancellationDuringDeletionWaitsForNativeDrainAndRetryClearsOnlyTheReleasedArchive()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Certificates.AfterRemove = async token =>
        {
            entered.TrySetResult();
            await drain.Task;
            token.ThrowIfCancellationRequested();
        };
        Task operation = fixture.Removal.RemoveAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(operation.IsCompleted);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Removal.RemoveAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Removal.VerifyCompletedAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Files.Deletes);
        drain.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(fixture.Original.PrepareRemoval(), (await fixture.Store.LoadAsync(CancellationToken.None))?.Ledger);

        fixture.Certificates.AfterRemove = null;
        await fixture.Removal.RemoveAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Certificates.Removals);
        Assert.Null(fixture.Files.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationRequiresBothArchiveAbsenceAndCurrentBoundary(bool archivePresent)
    {
        var fixture = new Fixture();
        if (!archivePresent)
        {
            fixture.Files.Bytes = null;
            fixture.Boundary.RejectedCall = 2;
        }
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Removal.VerifyCompletedAsync(CancellationToken.None));
        Assert.Empty(fixture.Certificates.Observed);
        Assert.Equal(0, fixture.Files.Writes + fixture.Files.Deletes);
    }

    [Fact]
    public async Task PreCancelledRemovalPerformsNoBoundaryOrStoreIo()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Removal.RemoveAsync(cancellation.Token));
        Assert.Equal(0, fixture.Boundary.Calls + fixture.Files.Reads + fixture.Files.Writes + fixture.Files.Deletes);
        Assert.Empty(fixture.Certificates.Observed);
    }

    private sealed class Fixture
    {
        internal Fixture(bool preExisting = false, bool referenceReleased = false, bool certificateMissing = false)
        {
            Original = InstallerTestData.CertificateLedger(wasPreExisting: preExisting);
            Files = new(referenceReleased ? Original.PrepareRemoval() : Original);
            Store = new(Original.TargetSid, Files);
            Certificates = new() { Presence = certificateMissing ? InstallerCertificatePresence.Missing : InstallerCertificatePresence.ExactMatch };
            Removal = new(Original.TargetSid, Store, Certificates, Boundary);
        }

        internal InstallerCertificateOwnershipLedger Original { get; }
        internal MemoryArchive Files { get; }
        internal InstallerArchivedCertificateStore Store { get; }
        internal CertificateAdapter Certificates { get; }
        internal Boundary Boundary { get; } = new();
        internal InstallerArchivedCertificateRemoval Removal { get; }
    }

    private sealed class Boundary : IInstallerArchivedCertificateRemovalBoundary
    {
        internal int Calls { get; private set; }
        internal int RejectedCall { get; set; }

        public Task ReverifyAsync(string authenticatedTargetSid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(InstallerTestData.Sid, authenticatedTargetSid);
            if (++Calls == RejectedCall)
            {
                throw new InstallerProtocolException("fixture.removal_precondition_changed");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CertificateAdapter : IInstallerArchivedCertificateRemovalAdapter
    {
        internal InstallerCertificatePresence Presence { get; set; }
        internal bool DeleteChangesPresence { get; set; } = true;
        internal int Removals { get; private set; }
        internal List<InstallerCertificateOwnershipLedger> Observed { get; } = [];
        internal Func<InstallerCertificateOwnershipLedger, CancellationToken, Task>? BeforeRemove { get; set; }
        internal Func<CancellationToken, Task>? AfterRemove { get; set; }

        public Task<InstallerCertificatePresence> InspectAsync(InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observed.Add(ledger);
            return Task.FromResult(Presence);
        }

        public async Task RemoveExactAsync(InstallerCertificateOwnershipLedger ledger, CancellationToken cancellationToken)
        {
            Assert.True(ledger.InstallerOwned);
            Assert.Equal(0, ledger.ManagedReferenceCount);
            if (BeforeRemove is { } before)
            {
                await before(ledger, cancellationToken);
            }
            Observed.Add(ledger);
            Removals++;
            if (DeleteChangesPresence)
            {
                Presence = InstallerCertificatePresence.Missing;
            }
            if (AfterRemove is { } after)
            {
                await after(cancellationToken);
            }
        }
    }
}
