using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerExecutableTrustVerifierTests
{
    private const string ExpectedSigner = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task VerifiedLeaseBlocksWriteAndRenameUntilDisposed()
    {
        using var fixture = new InstallerExecutableFixture();
        var authenticode = new RecordingAuthenticodeVerifier(ExpectedSigner);
        var verifier = new WindowsInstallerExecutableTrustVerifier(
            ExpectedSigner,
            authenticode);

        IWindowsInstallerExecutableTrustLease lease = await verifier.VerifyAsync(
            fixture.ExecutablePath,
            CancellationToken.None);

        Assert.Equal(fixture.ExecutablePath, lease.ExecutablePath);
        Assert.Equal(fixture.ExecutablePath, authenticode.ExecutablePath);
        Assert.True(authenticode.ObservedOpenHandle);
        Assert.Throws<IOException>(() =>
            File.Open(
                fixture.ExecutablePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite).Dispose());
        Assert.Throws<IOException>(() =>
            File.Move(fixture.ExecutablePath, fixture.ExecutablePath + ".moved"));

        lease.Dispose();
        using FileStream writable = File.Open(
            fixture.ExecutablePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task SignerMismatchFailsClosedAndReleasesTheFile()
    {
        using var fixture = new InstallerExecutableFixture();
        var verifier = new WindowsInstallerExecutableTrustVerifier(
            ExpectedSigner,
            new RecordingAuthenticodeVerifier(
                "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"));

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => verifier.VerifyAsync(
                fixture.ExecutablePath,
                CancellationToken.None));

        Assert.Equal("installer.elevation.signer_mismatch", exception.DiagnosticCode);
        using FileStream writable = File.Open(
            fixture.ExecutablePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task EmbeddedManifestIsTheSignerIdentitySource()
    {
        using var payload = new WindowsPayloadFixture();
        File.WriteAllBytes(payload.ExecutablePath, [1, 2, 3, 4]);
        var authenticode = new RecordingAuthenticodeVerifier(
            payload.Manifest.AuthenticodeCertificateThumbprint);
        var verifier = new WindowsInstallerExecutableTrustVerifier(
            payload.Manifest,
            authenticode);

        using IWindowsInstallerExecutableTrustLease lease = await verifier.VerifyAsync(
            payload.ExecutablePath,
            CancellationToken.None);

        Assert.Equal(payload.ExecutablePath, lease.ExecutablePath);
    }

    [Theory]
    [InlineData("ClashSharp.Installer.exe")]
    [InlineData("C:\\Temp\\Another.exe")]
    public async Task NoncanonicalExecutablePathIsRejected(string executablePath)
    {
        var authenticode = new RecordingAuthenticodeVerifier(ExpectedSigner);
        var verifier = new WindowsInstallerExecutableTrustVerifier(
            ExpectedSigner,
            authenticode);

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => verifier.VerifyAsync(
                executablePath,
                CancellationToken.None));

        Assert.Equal("installer.elevation.executable_path_invalid", exception.DiagnosticCode);
        Assert.Null(authenticode.ExecutablePath);
    }

    [Fact]
    public async Task CancellationBeforeVerificationDoesNotOpenTheFile()
    {
        using var fixture = new InstallerExecutableFixture();
        var authenticode = new RecordingAuthenticodeVerifier(ExpectedSigner);
        var verifier = new WindowsInstallerExecutableTrustVerifier(
            ExpectedSigner,
            authenticode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(
            fixture.ExecutablePath,
            cancellation.Token));

        Assert.Null(authenticode.ExecutablePath);
    }

    private sealed class RecordingAuthenticodeVerifier : IWindowsAuthenticodeVerifier
    {
        private readonly string _signerThumbprint;

        internal RecordingAuthenticodeVerifier(string signerThumbprint)
        {
            _signerThumbprint = signerThumbprint;
        }

        internal string? ExecutablePath { get; private set; }

        internal bool ObservedOpenHandle { get; private set; }

        public string VerifyTrustedSigner(
            string executablePath,
            SafeFileHandle lockedExecutableHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutablePath = executablePath;
            ObservedOpenHandle = !lockedExecutableHandle.IsClosed
                && !lockedExecutableHandle.IsInvalid;
            return _signerThumbprint;
        }
    }

    private sealed class InstallerExecutableFixture : IDisposable
    {
        private readonly string _root;

        internal InstallerExecutableFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "ClashSharp-Installer-Trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ExecutablePath = Path.Combine(_root, "ClashSharp.Installer.exe");
            File.WriteAllBytes(ExecutablePath, [1, 2, 3, 4]);
        }

        internal string ExecutablePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
