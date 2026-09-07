using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineCertificateStoreAdapterTests
{
    [Fact]
    public void NativeFlagsAlwaysBindTheMachineTrustedPeopleStore()
    {
        Assert.Equal(0x0002_C000u, WindowsMachineCertificateStoreNative.BuildOpenFlags(false, false));
        Assert.Equal(0x0002_4000u, WindowsMachineCertificateStoreNative.BuildOpenFlags(true, false));
        Assert.Equal(0x0002_0000u, WindowsMachineCertificateStoreNative.BuildOpenFlags(true, true));
        Assert.Throws<InstallerProtocolException>(() => WindowsMachineCertificateStoreNative.BuildOpenFlags(false, true));
    }

    [Fact]
    public async Task InspectionIsReadOnlyAndDoesNotRequireElevation()
    {
        using var fixture = Fixture();
        await using var lease = fixture.Lock();
        var native = new Native { RejectElevation = true };
        var adapter = new WindowsMachineCertificateStoreAdapter(native, native, native);
        Assert.Equal(InstallerCertificatePresence.Missing, await adapter.InspectAsync(fixture.Request(), lease, CancellationToken.None));
        Assert.Equal([(false, false)], native.Opens);
        Assert.Equal(0, native.Elevations);
        Assert.Equal(1, native.Disposals);
    }

    [Fact]
    public async Task SignedPublisherPayloadCanBeImportedIdempotentlyAndRemovedExactly()
    {
        using var fixture = Fixture();
        var native = new Native();
        var adapter = new WindowsMachineCertificateStoreAdapter(native, native, native);
        await using (var lease = fixture.Lock())
        {
            await adapter.ImportAsync(fixture.Request(), lease, CancellationToken.None);
            await adapter.ImportAsync(fixture.Request(), lease, CancellationToken.None);
            Assert.Equal(1, native.Adds);
            Assert.Equal(InstallerCertificatePresence.ExactMatch, await adapter.InspectAsync(fixture.Request(), lease, CancellationToken.None));
        }
        InstallerRequest uninstall = fixture.Request(InstallerOperation.Uninstall);
        await using var removalLease = fixture.Lock(uninstall);
        await adapter.RemoveExactAsync(uninstall, removalLease, CancellationToken.None);
        Assert.Equal(1, native.Deletes);
        Assert.Empty(native.Identities);
        Assert.Equal(native.Opens.Count, native.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnelevatedMutationNeverOpensTheStore(bool remove)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(remove ? InstallerOperation.Uninstall : InstallerOperation.Install);
        await using var lease = fixture.Lock(request);
        var native = new Native { RejectElevation = true };
        var adapter = new WindowsMachineCertificateStoreAdapter(native, native, native);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => remove
            ? adapter.RemoveExactAsync(request, lease, CancellationToken.None)
            : adapter.ImportAsync(request, lease, CancellationToken.None));
        Assert.Empty(native.Opens);
    }

    [Fact]
    public async Task TheMachineAdapterIndependentlyRejectsPackageReferencesBeforeDeletion()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);
        await using var lease = fixture.Lock(request);
        var native = new Native { Referenced = true };
        var adapter = new WindowsMachineCertificateStoreAdapter(native, native, native);
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            adapter.RemoveExactAsync(request, lease, CancellationToken.None));
        Assert.Equal("installer.machine_certificate.still_referenced", error.DiagnosticCode);
        Assert.Empty(native.Opens);
    }

    [Fact]
    public async Task ACertificateWithoutTheExactPublisherAndCodeSigningUsageIsNotImported()
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        await using var lease = fixture.Lock();
        var native = new Native();
        var adapter = new WindowsMachineCertificateStoreAdapter(native, native, native);
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            adapter.ImportAsync(fixture.Request(), lease, CancellationToken.None));
        Assert.Equal("installer.machine_certificate.publisher_certificate_invalid", error.DiagnosticCode);
        Assert.Empty(native.Opens);
    }

    private static WindowsPayloadFixture Fixture() => new(removeCurrentUserCertificateOnDispose: false, machineTrustCertificate: true);

    private sealed class Native : IWindowsCertificateStoreNative, IWindowsMachineHelperElevationVerifier, IInstallerMachineCertificateReferences
    {
        internal bool RejectElevation { get; init; }
        internal bool Referenced { get; init; }
        internal int Elevations { get; private set; }
        internal int Adds { get; set; }
        internal int Deletes { get; set; }
        internal int Disposals { get; set; }
        internal List<(bool Writable, bool Create)> Opens { get; } = [];
        internal List<WindowsCertificateIdentity> Identities { get; } = [];
        public void VerifyElevated()
        {
            Elevations++;
            if (RejectElevation)
            {
                throw new InstallerProtocolException("fixture.elevation_rejected");
            }
        }
        public Task<bool> HasReferencesAsync(InstallerReleaseManifest manifest, CancellationToken cancellationToken) => Task.FromResult(Referenced);
        public IWindowsCertificateStore Open(string targetSid, bool writable, bool createIfMissing)
        {
            Opens.Add((writable, createIfMissing));
            return new Store(this, writable);
        }
    }

    private sealed class Store(Native native, bool writable) : IWindowsCertificateStore
    {
        public IReadOnlyList<WindowsCertificateIdentity> EnumerateCertificateIdentities(CancellationToken cancellationToken) => native.Identities.ToArray();
        public void AddEncodedCertificate(byte[] encodedCertificate)
        {
            Assert.True(writable);
            native.Adds++;
            native.Identities.Add(WindowsCertificateIdentity.FromEncoded(encodedCertificate));
        }
        public int DeleteExactCertificates(string expectedThumbprint, string expectedDerSha256, CancellationToken cancellationToken)
        {
            Assert.True(writable);
            native.Deletes++;
            return native.Identities.RemoveAll(identity => identity.Matches(expectedThumbprint, expectedDerSha256));
        }
        public void Dispose() => native.Disposals++;
    }
}
