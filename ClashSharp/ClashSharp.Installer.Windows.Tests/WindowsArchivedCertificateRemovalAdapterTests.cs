using System.ComponentModel;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Certificates;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsArchivedCertificateRemovalAdapterTests
{
    internal const string RetiredSid = "S-1-5-21-100-200-300-1001";
    private static readonly WindowsCertificateIdentity OldIdentity = new(new string('A', 40), new string('a', 64));
    private static readonly WindowsCertificateIdentity CurrentIdentity = new(new string('B', 40), new string('b', 64));

    [Fact]
    public async Task ExactArchivedIdentityIsRemovedFromTheAuthenticatedAccountOnly()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new Native([CurrentIdentity, OldIdentity, OldIdentity]);
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);
        Assert.Empty(native.Opens);

        Assert.Equal(InstallerCertificatePresence.ExactMatch, await adapter.InspectAsync(Ledger(), CancellationToken.None));
        await adapter.RemoveExactAsync(Ledger(released: true), CancellationToken.None);

        Assert.Equal([CurrentIdentity], native.Identities);
        Assert.Equal(1, native.Deletes);
        Assert.Equal(0, native.Imports);
        Assert.Equal(native.Opens.Count, native.Disposals);
        Assert.All(native.Opens, open =>
        {
            Assert.Equal(RetiredSid, open.Sid);
            Assert.False(open.Create);
        });
        Assert.False(native.Opens[0].Writable);
        Assert.True(native.Opens[1].Writable);
        Assert.Equal(OldIdentity, native.DeletedIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCertificateOrMissingStoreIsNotCreatedOrDeleted(bool missingStore)
    {
        var native = new Native([CurrentIdentity]) { StoreMissing = missingStore };
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);
        Assert.Equal(InstallerCertificatePresence.Missing, await adapter.InspectAsync(Ledger(), CancellationToken.None));
        await adapter.RemoveExactAsync(Ledger(released: true), CancellationToken.None);
        Assert.Equal([CurrentIdentity], native.Identities);
        Assert.Equal(0, native.Deletes + native.Imports);
        Assert.All(native.Opens, open => Assert.False(open.Create));
        Assert.Equal(missingStore ? 0 : native.Opens.Count, native.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingDerForTheSameThumbprintBlocksDeletionEvenWithAnExactCopy(bool includeExact)
    {
        var conflicting = OldIdentity with { DerSha256 = new string('c', 64) };
        var native = new Native(includeExact ? [CurrentIdentity, conflicting, OldIdentity] : [CurrentIdentity, conflicting]);
        WindowsCertificateIdentity[] before = native.Identities.ToArray();
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);

        Assert.Equal(InstallerCertificatePresence.IdentityConflict, await adapter.InspectAsync(Ledger(), CancellationToken.None));
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            adapter.RemoveExactAsync(Ledger(released: true), CancellationToken.None));

        Assert.Equal("installer.certificate_archive.identity_conflict", error.DiagnosticCode);
        Assert.Equal(before, native.Identities);
        Assert.Equal(0, native.Deletes);
        Assert.Equal(native.Opens.Count, native.Disposals);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("pre-existing")]
    [InlineData("different-sid")]
    [InlineData("machine-store")]
    [InlineData("root-store")]
    [InlineData("generation")]
    public async Task InvalidRemovalAuthorityIsRejectedBeforeOpeningAnyNativeStore(string condition)
    {
        var native = new Native([OldIdentity]);
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);
        InstallerCertificateOwnershipLedger ledger = condition switch
        {
            "active" => Ledger(),
            "pre-existing" => Ledger(released: true, preExisting: true),
            "different-sid" => Ledger(released: true) with { TargetSid = "S-1-5-21-100-200-300-1002" },
            "machine-store" => Ledger(released: true) with { StoreLocation = (InstallerCertificateStoreLocation)1 },
            "root-store" => Ledger(released: true) with { StoreName = (InstallerCertificateStoreName)1 },
            "generation" => Ledger(released: true) with { Generation = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => adapter.RemoveExactAsync(ledger, CancellationToken.None));

        Assert.Empty(native.Opens);
        Assert.Equal([OldIdentity], native.Identities);
    }

    [Fact]
    public async Task AnotherAccountCannotEvenBeInspected()
    {
        var native = new Native([OldIdentity]);
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            adapter.InspectAsync(Ledger() with { TargetSid = "S-1-5-21-100-200-300-1002" }, CancellationToken.None));
        Assert.Empty(native.Opens);
    }

    [Theory]
    [InlineData("missing-delete")]
    [InlineData("late-conflict")]
    public async Task PostDeletionStateMustProveExactAbsence(string condition)
    {
        var native = new Native([OldIdentity]) { RemoveChangesState = condition != "missing-delete" };
        if (condition == "late-conflict")
        {
            native.AfterDelete = () => native.Identities.Add(OldIdentity with { DerSha256 = new string('d', 64) });
        }
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);

        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            adapter.RemoveExactAsync(Ledger(released: true), CancellationToken.None));

        Assert.Equal(condition == "late-conflict" ? "installer.certificate_archive.identity_conflict"
            : "installer.certificate_archive.removal_verification_failed", error.DiagnosticCode);
        Assert.Single(native.Identities);
        Assert.Equal(1, native.Disposals);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("enumerate")]
    [InlineData("delete")]
    public async Task NativeFailuresDoNotExposePrivateAccountStoreDetails(string operation)
    {
        var native = new Native([OldIdentity]) { Failure = operation };
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            operation == "delete" ? adapter.RemoveExactAsync(Ledger(released: true), CancellationToken.None)
                : adapter.InspectAsync(Ledger(), CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-fixture", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(operation == "open" ? 0 : 1, native.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterOpeningTheStoreDisposesItBeforeReturning(bool remove)
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native([OldIdentity]) { AfterOpen = cancellation.Cancel };
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            remove ? adapter.RemoveExactAsync(Ledger(released: true), cancellation.Token)
                : adapter.InspectAsync(Ledger(), cancellation.Token));
        Assert.Equal(1, native.Disposals);
        Assert.Equal(0, native.Deletes);
    }

    [Fact]
    public async Task PreCancellationOpensNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var native = new Native([OldIdentity]);
        var adapter = new WindowsArchivedCertificateRemovalAdapter(RetiredSid, native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.InspectAsync(Ledger(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.RemoveExactAsync(Ledger(released: true), cancellation.Token));
        Assert.Empty(native.Opens);
    }

    internal static InstallerCertificateOwnershipLedger Ledger(bool released = false, bool preExisting = false)
    {
        var ledger = new InstallerCertificateOwnershipLedger(1, new string('c', 64), RetiredSid,
            OldIdentity.Thumbprint, OldIdentity.DerSha256, InstallerCertificateStoreLocation.CurrentUser,
            InstallerCertificateStoreName.TrustedPeople, preExisting, !preExisting, 1, 1);
        return released ? ledger.PrepareRemoval() : ledger;
    }

    private sealed class Native(IEnumerable<WindowsCertificateIdentity> identities) : IWindowsCertificateStoreNative
    {
        internal List<WindowsCertificateIdentity> Identities { get; } = [.. identities];
        internal List<(string Sid, bool Writable, bool Create)> Opens { get; } = [];
        internal bool StoreMissing { get; set; }
        internal bool RemoveChangesState { get; set; } = true;
        internal int Disposals { get; private set; }
        internal int Deletes { get; private set; }
        internal int Imports { get; private set; }
        internal WindowsCertificateIdentity? DeletedIdentity { get; private set; }
        internal string? Failure { get; set; }
        internal Action? AfterOpen { get; set; }
        internal Action? AfterDelete { get; set; }

        public IWindowsCertificateStore? Open(string targetSid, bool writable, bool createIfMissing)
        {
            Opens.Add((targetSid, writable, createIfMissing));
            ThrowFor("open");
            AfterOpen?.Invoke();
            return StoreMissing ? null : new Store(this, writable);
        }

        private void ThrowFor(string operation)
        {
            if (Failure == operation)
            {
                throw new Win32Exception(5, "private-fixture");
            }
        }

        private sealed class Store(Native native, bool writable) : IWindowsCertificateStore
        {
            public IReadOnlyList<WindowsCertificateIdentity> EnumerateCertificateIdentities(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                native.ThrowFor("enumerate");
                return native.Identities.ToArray();
            }

            public void AddEncodedCertificate(byte[] encodedCertificate)
            {
                native.Imports++;
                throw new InvalidOperationException("Archive removal cannot import certificates.");
            }

            public int DeleteExactCertificates(string expectedThumbprint, string expectedDerSha256, CancellationToken cancellationToken)
            {
                Assert.True(writable);
                cancellationToken.ThrowIfCancellationRequested();
                native.ThrowFor("delete");
                native.Deletes++;
                native.DeletedIdentity = new(expectedThumbprint, expectedDerSha256);
                int removed = native.RemoveChangesState
                    ? native.Identities.RemoveAll(identity => identity.Matches(expectedThumbprint, expectedDerSha256)) : 0;
                native.AfterDelete?.Invoke();
                return removed;
            }

            public void Dispose() => native.Disposals++;
        }
    }
}
