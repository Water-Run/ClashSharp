using System.Security.Cryptography;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

public sealed class DurableInstallerCertificateMutationTests
{
    [Fact]
    public async Task MissingCertificateIsOwnedDurablyBeforeImport()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events);
        RecordingCertificateStore certificates = new(events, InstallerCertificatePresence.Missing);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await ApplyAsync(
            mutation,
            InstallerTestData.Request(),
            InstallerTestData.Release(),
            CancellationToken.None);

        Assert.Equal(
            [
                "ownership.load",
                "certificate.inspect:Missing",
                "ownership.save:1:1",
                "certificate.inspect:Missing",
                "certificate.import",
                "certificate.inspect:ExactMatch",
            ],
            events);
        Assert.True(ownership.Current?.Ledger.InstallerOwned);
        Assert.Equal(1, ownership.Current?.Ledger.ManagedReferenceCount);
    }

    [Fact]
    public async Task PreExistingCertificateIsRecordedButNeverImportedOrRemoved()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events);
        RecordingCertificateStore certificates = new(
            events,
            InstallerCertificatePresence.ExactMatch);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await ApplyAsync(
            mutation,
            InstallerTestData.Request(),
            InstallerTestData.Release(),
            CancellationToken.None);
        await ApplyAsync(
            mutation,
            InstallerTestData.Request(InstallerOperation.Uninstall),
            InstallerTestData.Release(
                packagePayloadAvailable: false,
                certificatePayloadAvailable: false),
            CancellationToken.None);

        Assert.Equal(
            [
                "ownership.load",
                "certificate.inspect:ExactMatch",
                "ownership.save:1:1",
                "certificate.inspect:ExactMatch",
                "ownership.load",
                "ownership.save:2:0",
                "ownership.clear",
            ],
            events);
        Assert.Equal(0, certificates.ImportCount);
        Assert.Equal(0, certificates.RemoveCount);
        Assert.Equal(InstallerCertificatePresence.ExactMatch, certificates.Presence);
        Assert.Null(ownership.Current);
    }

    [Fact]
    public async Task RepairClaimsOwnershipBeforeReplacingADisappearedPreExistingCertificate()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(
            events,
            InstallerTestData.CertificateLedger(wasPreExisting: true));
        RecordingCertificateStore certificates = new(events, InstallerCertificatePresence.Missing);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await ApplyAsync(
            mutation,
            InstallerTestData.Request(InstallerOperation.Repair, allowReassociation: true),
            InstallerTestData.Release(),
            CancellationToken.None);

        Assert.True(ownership.Current?.Ledger.InstallerOwned);
        Assert.Equal(2, ownership.Current?.Ledger.Generation);
        Assert.True(events.IndexOf("ownership.save:2:1") < events.IndexOf("certificate.import"));
    }

    [Fact]
    public async Task OwnedCertificateIsUnreferencedBeforeExactDeletionAndClearedLast()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events, InstallerTestData.CertificateLedger());
        RecordingCertificateStore certificates = new(
            events,
            InstallerCertificatePresence.ExactMatch);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await ApplyAsync(
            mutation,
            InstallerTestData.Request(InstallerOperation.Uninstall),
            InstallerTestData.Release(
                packagePayloadAvailable: false,
                certificatePayloadAvailable: false),
            CancellationToken.None);

        Assert.Equal(
            [
                "ownership.load",
                "ownership.save:2:0",
                "certificate.inspect:ExactMatch",
                "certificate.remove",
                "certificate.inspect:Missing",
                "ownership.clear",
            ],
            events);
        Assert.Null(ownership.Current);
    }

    [Fact]
    public async Task RetryAfterImportSideEffectDoesNotImportTwice()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events);
        RecordingCertificateStore certificates = new(events, InstallerCertificatePresence.Missing)
        {
            FailAfterImportOnce = true,
        };
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await Assert.ThrowsAsync<IOException>(() => ApplyAsync(
            mutation,
            InstallerTestData.Request(),
            InstallerTestData.Release(),
            CancellationToken.None));
        await ApplyAsync(
            mutation,
            InstallerTestData.Request(),
            InstallerTestData.Release(),
            CancellationToken.None);

        Assert.Equal(1, certificates.ImportCount);
        Assert.True(ownership.Current?.Ledger.InstallerOwned);
        Assert.Equal(1, ownership.Current?.Ledger.Generation);
    }

    [Fact]
    public async Task RetryAfterRemovalSideEffectClearsTheUnreferencedLedger()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events, InstallerTestData.CertificateLedger());
        RecordingCertificateStore certificates = new(
            events,
            InstallerCertificatePresence.ExactMatch)
        {
            FailAfterRemoveOnce = true,
        };
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);
        InstallerRequest request = InstallerTestData.Request(InstallerOperation.Uninstall);
        VerifiedInstallerRelease release = InstallerTestData.Release(certificatePayloadAvailable: false);

        await Assert.ThrowsAsync<IOException>(() => ApplyAsync(
            mutation,
            request,
            release,
            CancellationToken.None));
        Assert.Equal(0, ownership.Current?.Ledger.ManagedReferenceCount);
        await ApplyAsync(mutation, request, release, CancellationToken.None);

        Assert.Equal(1, certificates.RemoveCount);
        Assert.Null(ownership.Current);
    }

    [Theory]
    [InlineData(InstallerCertificatePresence.IdentityConflict)]
    [InlineData((InstallerCertificatePresence)999)]
    public async Task AmbiguousCertificateIdentityFailsBeforeOwnershipIsCreated(
        InstallerCertificatePresence presence)
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events);
        RecordingCertificateStore certificates = new(events, presence);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            ApplyAsync(
                mutation,
                InstallerTestData.Request(),
                InstallerTestData.Release(),
                CancellationToken.None));

        Assert.Equal("installer.certificate.identity_conflict", exception.DiagnosticCode);
        Assert.Null(ownership.Current);
        Assert.Equal(0, certificates.ImportCount);
    }

    [Fact]
    public async Task MismatchedOwnershipCannotBeTakenOverByAnotherRelease()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(
            events,
            InstallerTestData.CertificateLedger(certificateHash: InstallerTestData.OtherHash));
        RecordingCertificateStore certificates = new(events, InstallerCertificatePresence.Missing);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            ApplyAsync(
                mutation,
                InstallerTestData.Request(),
                InstallerTestData.Release(),
                CancellationToken.None));

        Assert.Equal("installer.certificate.ownership_conflict", exception.DiagnosticCode);
        Assert.Equal(["ownership.load"], events);
    }

    [Fact]
    public async Task InstallCannotReuseAnUnreferencedUninstallLedger()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(
            events,
            InstallerTestData.CertificateLedger(managedReferenceCount: 0, generation: 2));
        RecordingCertificateStore certificates = new(events, InstallerCertificatePresence.Missing);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            ApplyAsync(
                mutation,
                InstallerTestData.Request(),
                InstallerTestData.Release(),
                CancellationToken.None));

        Assert.Equal("installer.certificate.ownership_conflict", exception.DiagnosticCode);
        Assert.Equal(["ownership.load"], events);
    }

    [Fact]
    public async Task ResumeCannotReimportWhenTheVerifiedCertificatePayloadDisappeared()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events, InstallerTestData.CertificateLedger());
        RecordingCertificateStore certificates = new(events, InstallerCertificatePresence.Missing);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            ApplyAsync(
                mutation,
                InstallerTestData.Request(),
                InstallerTestData.Release(certificatePayloadAvailable: false),
                CancellationToken.None));

        Assert.Equal("installer.release.certificate_payload_missing", exception.DiagnosticCode);
        Assert.Equal(["ownership.load", "certificate.inspect:Missing"], events);
        Assert.Equal(0, certificates.ImportCount);
    }

    [Fact]
    public async Task UninstallWithoutOwnershipEvidenceNeverTouchesTheCertificateStore()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events);
        RecordingCertificateStore certificates = new(
            events,
            InstallerCertificatePresence.ExactMatch);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await ApplyAsync(
            mutation,
            InstallerTestData.Request(InstallerOperation.Uninstall),
            InstallerTestData.Release(certificatePayloadAvailable: false),
            CancellationToken.None);

        Assert.Equal(["ownership.load"], events);
        Assert.Equal(0, certificates.RemoveCount);
    }

    [Fact]
    public async Task ImportAndRemovalMustBeIndependentlyVerified()
    {
        List<string> importEvents = [];
        MemoryOwnershipStore importOwnership = new(importEvents);
        RecordingCertificateStore badImport = new(importEvents, InstallerCertificatePresence.Missing)
        {
            PresenceAfterImport = InstallerCertificatePresence.Missing,
        };
        DurableInstallerCertificateMutation importMutation = new(importOwnership, badImport);
        InstallerProtocolException importFailure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            ApplyAsync(
                importMutation,
                InstallerTestData.Request(),
                InstallerTestData.Release(),
                CancellationToken.None));
        Assert.Equal("installer.certificate.import_verification_failed", importFailure.DiagnosticCode);

        List<string> removalEvents = [];
        MemoryOwnershipStore removalOwnership = new(
            removalEvents,
            InstallerTestData.CertificateLedger());
        RecordingCertificateStore badRemoval = new(
            removalEvents,
            InstallerCertificatePresence.ExactMatch)
        {
            PresenceAfterRemove = InstallerCertificatePresence.ExactMatch,
        };
        DurableInstallerCertificateMutation removalMutation = new(removalOwnership, badRemoval);
        InstallerProtocolException removalFailure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            ApplyAsync(
                removalMutation,
                InstallerTestData.Request(InstallerOperation.Uninstall),
                InstallerTestData.Release(certificatePayloadAvailable: false),
                CancellationToken.None));
        Assert.Equal(
            "installer.certificate.removal_verification_failed",
            removalFailure.DiagnosticCode);
        Assert.Equal(0, removalOwnership.Current?.Ledger.ManagedReferenceCount);
    }

    [Fact]
    public async Task InstalledVerificationRequiresActiveMatchingLedgerAndExactCertificate()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events, InstallerTestData.CertificateLedger());
        RecordingCertificateStore certificates = new(
            events,
            InstallerCertificatePresence.ExactMatch);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await VerifyAsync(
            mutation,
            InstallerTestData.Request(),
            InstallerTestData.Release(),
            CancellationToken.None);

        Assert.Equal(
            ["ownership.load", "certificate.inspect:ExactMatch"],
            events);
    }

    [Fact]
    public async Task InstalledVerificationRejectsMissingOwnershipLedger()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events);
        RecordingCertificateStore certificates = new(
            events,
            InstallerCertificatePresence.ExactMatch);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => VerifyAsync(
                mutation,
                InstallerTestData.Request(),
                InstallerTestData.Release(),
                CancellationToken.None));

        Assert.Equal("installer.certificate.ownership_missing", exception.DiagnosticCode);
    }

    [Fact]
    public async Task UninstallVerificationRejectsUnclearedMatchingLedger()
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events, InstallerTestData.CertificateLedger());
        RecordingCertificateStore certificates = new(
            events,
            InstallerCertificatePresence.Missing);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => VerifyAsync(
                mutation,
                InstallerTestData.Request(InstallerOperation.Uninstall),
                InstallerTestData.Release(certificatePayloadAvailable: false),
                CancellationToken.None));

        Assert.Equal("installer.certificate.removal_incomplete", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData(InstallerCertificatePresence.Missing)]
    [InlineData(InstallerCertificatePresence.ExactMatch)]
    public async Task UninstallVerificationAllowsClearedLedgerWithAbsentOrPreExistingCertificate(
        InstallerCertificatePresence presence)
    {
        List<string> events = [];
        MemoryOwnershipStore ownership = new(events);
        RecordingCertificateStore certificates = new(events, presence);
        DurableInstallerCertificateMutation mutation = new(ownership, certificates);

        await VerifyAsync(
            mutation,
            InstallerTestData.Request(InstallerOperation.Uninstall),
            InstallerTestData.Release(certificatePayloadAvailable: false),
            CancellationToken.None);

        Assert.Equal(2, events.Count);
    }

    private static async Task ApplyAsync(
        DurableInstallerCertificateMutation mutation,
        InstallerRequest request,
        VerifiedInstallerRelease release,
        CancellationToken cancellationToken)
    {
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease(release);
        await mutation.ApplyAsync(request, lease, cancellationToken);
    }

    private static async Task VerifyAsync(
        DurableInstallerCertificateMutation mutation,
        InstallerRequest request,
        VerifiedInstallerRelease release,
        CancellationToken cancellationToken)
    {
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease(release);
        await mutation.VerifyAppliedAsync(request, lease, cancellationToken);
    }

    private sealed class MemoryOwnershipStore : IInstallerCertificateOwnershipStore
    {
        private readonly List<string> _events;

        internal MemoryOwnershipStore(
            List<string> events,
            InstallerCertificateOwnershipLedger? initial = null)
        {
            _events = events;
            Current = initial is null ? null : Snapshot(initial);
        }

        internal InstallerCertificateOwnershipSnapshot? Current { get; private set; }

        public Task<InstallerCertificateOwnershipSnapshot?> LoadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("ownership.load");
            return Task.FromResult(Current);
        }

        public Task<InstallerCertificateOwnershipSnapshot> SaveAsync(
            InstallerCertificateOwnershipLedger ledger,
            string? expectedCurrentHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ledger.Validate();
            if (Current is null)
            {
                Assert.Null(expectedCurrentHash);
                Assert.Equal(1, ledger.Generation);
            }
            else
            {
                Assert.Equal(Current.ContentHash, expectedCurrentHash);
                Assert.Equal(Current.Ledger.LedgerId, ledger.LedgerId);
            }

            Current = Snapshot(ledger);
            _events.Add($"ownership.save:{ledger.Generation}:{ledger.ManagedReferenceCount}");
            return Task.FromResult(Current);
        }

        public Task ClearUnreferencedAsync(
            string ledgerId,
            string expectedCurrentHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(Current);
            Assert.Equal(0, Current.Ledger.ManagedReferenceCount);
            Assert.Equal(ledgerId, Current.Ledger.LedgerId);
            Assert.Equal(expectedCurrentHash, Current.ContentHash);
            Current = null;
            _events.Add("ownership.clear");
            return Task.CompletedTask;
        }

        private static InstallerCertificateOwnershipSnapshot Snapshot(
            InstallerCertificateOwnershipLedger ledger)
        {
            byte[] bytes = InstallerCertificateOwnershipCodec.Serialize(ledger);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new InstallerCertificateOwnershipSnapshot(ledger, hash);
        }
    }

    private sealed class RecordingCertificateStore : IInstallerCertificateStoreAdapter
    {
        private readonly List<string> _events;

        internal RecordingCertificateStore(
            List<string> events,
            InstallerCertificatePresence initialPresence)
        {
            _events = events;
            Presence = initialPresence;
        }

        internal InstallerCertificatePresence Presence { get; private set; }

        internal InstallerCertificatePresence PresenceAfterImport { get; init; } =
            InstallerCertificatePresence.ExactMatch;

        internal InstallerCertificatePresence PresenceAfterRemove { get; init; } =
            InstallerCertificatePresence.Missing;

        internal bool FailAfterImportOnce { get; set; }

        internal bool FailAfterRemoveOnce { get; set; }

        internal int ImportCount { get; private set; }

        internal int RemoveCount { get; private set; }

        public Task<InstallerCertificatePresence> InspectAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"certificate.inspect:{Presence}");
            return Task.FromResult(Presence);
        }

        public Task ImportAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportCount++;
            _events.Add("certificate.import");
            Presence = PresenceAfterImport;
            if (FailAfterImportOnce)
            {
                FailAfterImportOnce = false;
                throw new IOException("simulated crash after certificate import");
            }

            return Task.CompletedTask;
        }

        public Task RemoveExactAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCount++;
            _events.Add("certificate.remove");
            Presence = PresenceAfterRemove;
            if (FailAfterRemoveOnce)
            {
                FailAfterRemoveOnce = false;
                throw new IOException("simulated crash after certificate removal");
            }

            return Task.CompletedTask;
        }
    }
}
