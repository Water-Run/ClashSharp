using System.ComponentModel;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Certificates;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsTargetUserCertificateStoreAdapterTests
{
    private const string AlternateTargetSid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public void NativeStoreNameIsOnlyCanonicalSidTrustedPeople()
    {
        Assert.Equal(
            $"{AlternateTargetSid}\\TrustedPeople",
            WindowsTargetUserCertificateStoreNative.BuildSystemStoreName(
                AlternateTargetSid));

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsTargetUserCertificateStoreNative.BuildSystemStoreName(
                $"{AlternateTargetSid}\\Root"));

        Assert.Equal("installer.request.target_sid_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void NativeOpenModesCreateOnlyForWritableImport()
    {
        Assert.Equal(
            0x0006_C000u,
            WindowsTargetUserCertificateStoreNative.BuildOpenFlags(
                writable: false,
                createIfMissing: false));
        Assert.Equal(
            0x0006_4000u,
            WindowsTargetUserCertificateStoreNative.BuildOpenFlags(
                writable: true,
                createIfMissing: false));
        Assert.Equal(
            0x0006_0000u,
            WindowsTargetUserCertificateStoreNative.BuildOpenFlags(
                writable: true,
                createIfMissing: true));

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsTargetUserCertificateStoreNative.BuildOpenFlags(
                writable: false,
                createIfMissing: true));

        Assert.Equal(
            "installer.certificate.store_open_mode_invalid",
            exception.DiagnosticCode);
    }

    [Fact]
    public async Task MissingStoreInspectionDoesNotCreateTargetStore()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative
        {
            ExistingStoreAvailable = false,
        };
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        InstallerCertificatePresence presence = await adapter.InspectAsync(
            request,
            lease,
            CancellationToken.None);

        Assert.Equal(InstallerCertificatePresence.Missing, presence);
        Assert.Collection(
            native.Opens,
            open =>
            {
                Assert.Equal(AlternateTargetSid, open.TargetSid);
                Assert.False(open.Writable);
                Assert.False(open.CreateIfMissing);
            });
        Assert.Equal(0, native.Disposals);
    }

    [Fact]
    public async Task InspectionUsesExactRequestSidInsteadOfHelperTokenSid()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative();
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        InstallerCertificatePresence presence = await adapter.InspectAsync(
            request,
            lease,
            CancellationToken.None);

        Assert.Equal(InstallerCertificatePresence.Missing, presence);
        Assert.Collection(
            native.Opens,
            open =>
            {
                Assert.Equal(AlternateTargetSid, open.TargetSid);
                Assert.False(open.Writable);
                Assert.False(open.CreateIfMissing);
            });
        Assert.Equal(1, native.Disposals);
    }

    [Fact]
    public async Task ImportAddsExactLockedDerAndVerifiesSameTargetStore()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        byte[] expectedDer = File.ReadAllBytes(fixture.CertificatePath);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative();
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        await adapter.ImportAsync(request, lease, CancellationToken.None);

        Assert.Equal(expectedDer, native.AddedCertificate);
        Assert.Contains(
            native.Certificates,
            identity => identity.Matches(
                fixture.Manifest.PackageCertificateThumbprint,
                fixture.Manifest.CertificateSha256));
        Assert.Collection(
            native.Opens,
            open =>
            {
                Assert.Equal(AlternateTargetSid, open.TargetSid);
                Assert.True(open.Writable);
                Assert.True(open.CreateIfMissing);
            });
        Assert.True(native.EnumerationCalls >= 2);
    }

    [Fact]
    public async Task ExistingExactCertificateMakesImportIdempotent()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative(
            ExactIdentity(fixture));
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        await adapter.ImportAsync(request, lease, CancellationToken.None);

        Assert.Equal(0, native.AddCalls);
    }

    [Fact]
    public async Task BenignAddNewRaceIsAcceptedOnlyAfterExactReinspection()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative
        {
            AddFailureAfterCommit = new Win32Exception(183),
        };
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        await adapter.ImportAsync(request, lease, CancellationToken.None);

        Assert.Equal(1, native.AddCalls);
        Assert.Contains(native.Certificates, identity => identity == ExactIdentity(fixture));
    }

    [Fact]
    public async Task SameThumbprintDifferentDerBlocksImportBeforeMutation()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative(
            ConflictingIdentity(fixture));
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.ImportAsync(request, lease, CancellationToken.None));

        Assert.Equal("installer.certificate.identity_conflict", exception.DiagnosticCode);
        Assert.Equal(0, native.AddCalls);
    }

    [Fact]
    public async Task RemovalUsesReleaseIdentityWithoutCertificatePayload()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            AlternateTargetSid);
        await using var lease = new WindowsInstallerReleaseLease(
            request,
            fixture.Manifest,
            payloadRoot: null,
            lockedFiles: [],
            directoryGuards: []);
        WindowsCertificateIdentity unrelated = new(
            "0123456789ABCDEF0123456789ABCDEF01234567",
            new string('b', 64));
        var native = new RecordingCertificateStoreNative(
            unrelated,
            ExactIdentity(fixture));
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        await adapter.RemoveExactAsync(request, lease, CancellationToken.None);

        Assert.Equal(1, native.DeleteCalls);
        Assert.Equal(fixture.Manifest.PackageCertificateThumbprint, native.DeletedThumbprint);
        Assert.Equal(fixture.Manifest.CertificateSha256, native.DeletedDerSha256);
        Assert.Equal([unrelated], native.Certificates);
        Assert.Collection(
            native.Opens,
            open =>
            {
                Assert.True(open.Writable);
                Assert.False(open.CreateIfMissing);
            });
    }

    [Fact]
    public async Task MissingStoreRemovalDoesNotCreateTargetStore()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            AlternateTargetSid);
        await using var lease = new WindowsInstallerReleaseLease(
            request,
            fixture.Manifest,
            payloadRoot: null,
            lockedFiles: [],
            directoryGuards: []);
        var native = new RecordingCertificateStoreNative
        {
            ExistingStoreAvailable = false,
        };
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        await adapter.RemoveExactAsync(request, lease, CancellationToken.None);

        Assert.Equal(0, native.DeleteCalls);
        Assert.Collection(
            native.Opens,
            open =>
            {
                Assert.True(open.Writable);
                Assert.False(open.CreateIfMissing);
            });
    }

    [Fact]
    public async Task SameThumbprintDifferentDerBlocksRemovalBeforeDeletion()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative(
            ExactIdentity(fixture),
            ConflictingIdentity(fixture));
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.RemoveExactAsync(request, lease, CancellationToken.None));

        Assert.Equal("installer.certificate.identity_conflict", exception.DiagnosticCode);
        Assert.Equal(0, native.DeleteCalls);
    }

    [Fact]
    public async Task FailedImportPostconditionCannotReportSuccess()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative
        {
            IgnoreAdds = true,
        };
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.ImportAsync(request, lease, CancellationToken.None));

        Assert.Equal(
            "installer.certificate.import_verification_failed",
            exception.DiagnosticCode);
    }

    [Fact]
    public async Task FailedRemovalPostconditionCannotReportSuccess()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative(ExactIdentity(fixture))
        {
            IgnoreDeletes = true,
        };
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.RemoveExactAsync(request, lease, CancellationToken.None));

        Assert.Equal(
            "installer.certificate.removal_verification_failed",
            exception.DiagnosticCode);
    }

    [Fact]
    public async Task PreCancellationFailsBeforeOpeningTargetStore()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(
            removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(targetSid: AlternateTargetSid);
        await using var lease = fixture.Lock(request);
        var native = new RecordingCertificateStoreNative();
        var adapter = new WindowsTargetUserCertificateStoreAdapter(native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.InspectAsync(request, lease, cancellation.Token));

        Assert.Empty(native.Opens);
    }

    private static WindowsCertificateIdentity ExactIdentity(WindowsPayloadFixture fixture) =>
        new(
            fixture.Manifest.PackageCertificateThumbprint,
            fixture.Manifest.CertificateSha256);

    private static WindowsCertificateIdentity ConflictingIdentity(
        WindowsPayloadFixture fixture) =>
        new(
            fixture.Manifest.PackageCertificateThumbprint,
            fixture.Manifest.CertificateSha256 == new string('a', 64)
                ? new string('b', 64)
                : new string('a', 64));

    private sealed class RecordingCertificateStoreNative
        : IWindowsTargetUserCertificateStoreNative
    {
        internal RecordingCertificateStoreNative(
            params WindowsCertificateIdentity[] certificates)
        {
            Certificates = [.. certificates];
        }

        internal List<WindowsCertificateIdentity> Certificates { get; }

        internal List<(string TargetSid, bool Writable, bool CreateIfMissing)> Opens { get; } = [];

        internal bool ExistingStoreAvailable { get; set; } = true;

        internal int Disposals { get; set; }

        internal int EnumerationCalls { get; set; }

        internal int AddCalls { get; set; }

        internal byte[]? AddedCertificate { get; set; }

        internal Exception? AddFailureAfterCommit { get; init; }

        internal bool IgnoreAdds { get; init; }

        internal int DeleteCalls { get; set; }

        internal string? DeletedThumbprint { get; set; }

        internal string? DeletedDerSha256 { get; set; }

        internal bool IgnoreDeletes { get; init; }

        public IWindowsTargetUserCertificateStore? Open(
            string targetSid,
            bool writable,
            bool createIfMissing)
        {
            Opens.Add((targetSid, writable, createIfMissing));
            if (!ExistingStoreAvailable && !createIfMissing)
            {
                return null;
            }

            ExistingStoreAvailable = true;
            return new RecordingCertificateStore(this, writable);
        }
    }

    private sealed class RecordingCertificateStore : IWindowsTargetUserCertificateStore
    {
        private readonly RecordingCertificateStoreNative _owner;
        private readonly bool _writable;
        private bool _disposed;

        internal RecordingCertificateStore(
            RecordingCertificateStoreNative owner,
            bool writable)
        {
            _owner = owner;
            _writable = writable;
        }

        public IReadOnlyList<WindowsCertificateIdentity> EnumerateCertificateIdentities(
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _owner.EnumerationCalls++;
            return _owner.Certificates.ToArray();
        }

        public void AddEncodedCertificate(byte[] encodedCertificate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Assert.True(_writable);
            _owner.AddCalls++;
            _owner.AddedCertificate = encodedCertificate.ToArray();
            if (!_owner.IgnoreAdds)
            {
                _owner.Certificates.Add(
                    WindowsCertificateIdentity.FromEncoded(encodedCertificate));
            }

            if (_owner.AddFailureAfterCommit is not null)
            {
                throw _owner.AddFailureAfterCommit;
            }
        }

        public int DeleteExactCertificates(
            string expectedThumbprint,
            string expectedDerSha256,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Assert.True(_writable);
            cancellationToken.ThrowIfCancellationRequested();
            _owner.DeleteCalls++;
            _owner.DeletedThumbprint = expectedThumbprint;
            _owner.DeletedDerSha256 = expectedDerSha256;
            if (_owner.IgnoreDeletes)
            {
                return 0;
            }

            return _owner.Certificates.RemoveAll(identity =>
                identity.Matches(expectedThumbprint, expectedDerSha256));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _owner.Disposals++;
            _disposed = true;
        }
    }
}
