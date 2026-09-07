using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Exercises the real Windows trust provider against the installed SDK's signed host. These tests
/// neither execute the copied image nor change certificate stores or machine trust policy.
/// </summary>
public sealed class WindowsAuthenticodeVerifierTests
{
    [Fact]
    public void TrustedEmbeddedSignatureReturnsTheVerifiedSigner()
    {
        using var fixture = new SignedExecutableFixture();
        using SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryFile(
            fixture.ExecutablePath);

        string thumbprint = WindowsAuthenticodeVerifier.Instance.VerifyTrustedSigner(
            fixture.ExecutablePath,
            handle,
            CancellationToken.None);

        Assert.Matches("^[0-9A-F]{40}$", thumbprint);
    }

    [Fact]
    public async Task NativeVerificationKeepsTheExactImageLockedUntilLeaseDisposal()
    {
        using var fixture = new SignedExecutableFixture();
        string thumbprint = fixture.ReadTrustedSigner();
        var verifier = new WindowsInstallerExecutableTrustVerifier(thumbprint);

        using (IWindowsInstallerExecutableTrustLease lease = await verifier.VerifyAsync(
            fixture.ExecutablePath,
            CancellationToken.None))
        {
            Assert.Equal(fixture.ExecutablePath, lease.ExecutablePath);
            Assert.Throws<IOException>(() => File.OpenWrite(fixture.ExecutablePath).Dispose());
            Assert.Throws<IOException>(() => File.Move(
                fixture.ExecutablePath,
                fixture.ExecutablePath + ".moved"));
        }

        using FileStream writable = File.OpenWrite(fixture.ExecutablePath);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task TrustedSignatureFromAnotherPinnedSignerIsRejected()
    {
        using var fixture = new SignedExecutableFixture();
        string thumbprint = fixture.ReadTrustedSigner();
        string mismatchedThumbprint = string.Concat(
            thumbprint[0] == 'A' ? "B" : "A",
            thumbprint.AsSpan(1));
        var verifier = new WindowsInstallerExecutableTrustVerifier(mismatchedThumbprint);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.ExecutablePath, CancellationToken.None));

        Assert.Equal("installer.elevation.signer_mismatch", exception.DiagnosticCode);
        using FileStream writable = File.OpenWrite(fixture.ExecutablePath);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task ModifiedImageWithItsOriginalSignatureIsRejected()
    {
        using var fixture = new SignedExecutableFixture();
        string thumbprint = fixture.ReadTrustedSigner();
        fixture.ModifyCodeSection();
        var verifier = new WindowsInstallerExecutableTrustVerifier(thumbprint);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.ExecutablePath, CancellationToken.None));

        Assert.Equal("installer.elevation.authenticode_invalid", exception.DiagnosticCode);
        using FileStream writable = File.OpenWrite(fixture.ExecutablePath);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task UnsignedImageIsRejectedAndReleased()
    {
        using var fixture = new SignedExecutableFixture();
        File.WriteAllBytes(fixture.ExecutablePath, [1, 2, 3, 4]);
        var verifier = new WindowsInstallerExecutableTrustVerifier(new string('A', 40));

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.ExecutablePath, CancellationToken.None));

        Assert.Equal("installer.elevation.authenticode_invalid", exception.DiagnosticCode);
        using FileStream writable = File.OpenWrite(fixture.ExecutablePath);
        Assert.True(writable.CanWrite);
    }

    private sealed class SignedExecutableFixture : IDisposable
    {
        private readonly string _root;

        internal SignedExecutableFixture()
        {
            string dotnetHost = Path.GetFullPath(Path.Combine(
                RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            Assert.True(File.Exists(dotnetHost), "The installed Windows SDK host is required.");
            _root = Path.Combine(Path.GetTempPath(), "ClashSharp-Native-Trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ExecutablePath = Path.Combine(_root, InstallerArtifactNames.PublishedExecutable);
            File.Copy(dotnetHost, ExecutablePath);
        }

        internal string ExecutablePath { get; }

        internal string ReadTrustedSigner()
        {
            using SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryFile(ExecutablePath);
            return WindowsAuthenticodeVerifier.Instance.VerifyTrustedSigner(
                ExecutablePath, handle, CancellationToken.None);
        }

        internal void ModifyCodeSection()
        {
            int codeOffset;
            using (var reader = new PEReader(File.OpenRead(ExecutablePath)))
            {
                SectionHeader section = reader.PEHeaders.SectionHeaders.Single(
                    static section => section.Name == ".text");
                Assert.True(section.SizeOfRawData > 0);
                codeOffset = section.PointerToRawData;
            }

            using FileStream image = File.Open(ExecutablePath, FileMode.Open, FileAccess.ReadWrite);
            image.Position = codeOffset;
            int original = image.ReadByte();
            Assert.InRange(original, 0, 255);
            image.Position = codeOffset;
            image.WriteByte((byte)(original ^ 1));
            image.Flush(flushToDisk: true);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
