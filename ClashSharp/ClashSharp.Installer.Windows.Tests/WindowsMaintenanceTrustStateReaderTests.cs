using System.Security.Cryptography;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceTrustStateReaderTests
{
    [Fact]
    public async Task MatchingCanonicalLedgersAndPhysicalCertificatesProduceOnlyStableFingerprints()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(InstallerOperation.Repair);
        await using var release = new Lease(fixture.Manifest);
        var state = new State(request, release);

        WindowsMaintenanceTrustFingerprint fingerprint = await new WindowsMaintenanceTrustStateReader(state, state, state, state)
            .ReadAsync(request, release, CancellationToken.None);

        Assert.Equal(state.User!.ContentHash, fingerprint.UserLedgerSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(state.Machine!)), fingerprint.MachineLedgerSha256);
        Assert.Equal(fixture.Manifest.PackageCertificateThumbprint, fingerprint.CertificateThumbprint);
        Assert.Equal(2, state.Inspections);
    }

    [Theory]
    [InlineData("missing-user")]
    [InlineData("missing-machine")]
    [InlineData("hash-mismatch")]
    [InlineData("physical-missing")]
    [InlineData("released-user")]
    public async Task AnyMissingOrInconsistentTrustEvidenceRejectsTheOriginalState(string scenario)
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(InstallerOperation.Repair);
        await using var release = new Lease(fixture.Manifest);
        var state = new State(request, release);
        if (scenario == "missing-user") { state.User = null; }
        else if (scenario == "missing-machine") { state.Machine = null; }
        else if (scenario == "hash-mismatch") { state.User = state.User! with { ContentHash = new string('0', 64) }; }
        else if (scenario == "physical-missing") { state.Presence = InstallerCertificatePresence.Missing; }
        else { state.User = state.User! with { Ledger = state.User!.Ledger.PrepareRemoval() }; }

        await Assert.ThrowsAsync<InstallerProtocolException>(() => new WindowsMaintenanceTrustStateReader(state, state, state, state)
            .ReadAsync(request, release, CancellationToken.None));
    }

    private sealed class Lease(InstallerReleaseManifest manifest) : IInstallerReleaseLease
    {
        public InstallerReleaseManifest Manifest { get; } = manifest;
        public VerifiedInstallerRelease Release { get; } = manifest.CreateVerifiedRelease(true, true);
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class State : IInstallerCertificateOwnershipStore, IInstallerMachineCertificatePersistence, IInstallerCertificateStoreAdapter
    {
        internal State(InstallerRequest request, IInstallerReleaseLease release)
        {
            InstallerCertificateOwnershipLedger ledger = InstallerCertificateOwnershipLedger.Create(request, release.Release, true);
            User = new(ledger, Convert.ToHexStringLower(SHA256.HashData(InstallerCertificateOwnershipCodec.Serialize(ledger))));
            Machine = InstallerMachineCertificateOwnership.Create(release.Manifest, true).Serialize();
        }
        internal InstallerCertificateOwnershipSnapshot? User { get; set; }
        internal byte[]? Machine { get; set; }
        internal InstallerCertificatePresence Presence { get; set; } = InstallerCertificatePresence.ExactMatch;
        internal int Inspections { get; private set; }
        public Task<InstallerCertificateOwnershipSnapshot?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(User);
        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Machine?.ToArray());
        public Task<InstallerCertificatePresence> InspectAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inspections++;
            return Task.FromResult(Presence);
        }
        public Task<InstallerCertificateOwnershipSnapshot> SaveAsync(InstallerCertificateOwnershipLedger ledger, string? expectedCurrentHash, CancellationToken cancellationToken) => throw new InvalidOperationException("Read-only evidence must not save trust.");
        public Task ClearUnreferencedAsync(string ledgerId, string expectedCurrentHash, CancellationToken cancellationToken) => throw new InvalidOperationException("Read-only evidence must not clear trust.");
        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new InvalidOperationException("Read-only evidence must not write trust.");
        public Task DeleteAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Read-only evidence must not delete trust.");
        public Task ImportAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) => throw new InvalidOperationException("Read-only evidence must not import trust.");
        public Task RemoveExactAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) => throw new InvalidOperationException("Read-only evidence must not remove trust.");
    }
}
