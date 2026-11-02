using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInternalCandidateAuthenticodeTests
{
    [Fact]
    public async Task PinnedInternalCandidateWorksWithoutInstallingItsRoot()
    {
        using var fixture = new UntrustedSignedFixture();
        string[] rootsBefore = ReadRoots();
        var verifier = fixture.Verifier(allowInternal: true);

        using (IWindowsInstallerExecutableTrustLease lease = await verifier.VerifyAsync(fixture.Path, CancellationToken.None))
        {
            Assert.Equal(fixture.Path, lease.ExecutablePath);
            Assert.Throws<IOException>(() => File.OpenWrite(fixture.Path).Dispose());
        }

        Assert.DoesNotContain(fixture.Thumbprint, rootsBefore);
        Assert.DoesNotContain(fixture.Thumbprint, ReadRoots());
    }

    [Fact]
    public async Task NormalReleasePolicyStillRequiresSystemTrust()
    {
        using var fixture = new UntrustedSignedFixture();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(allowInternal: false).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    [Fact]
    public async Task InternalPolicyRejectsAValidSignatureFromAnotherPinnedIdentity()
    {
        using var fixture = new UntrustedSignedFixture();
        string different = (fixture.Thumbprint[0] == 'A' ? "B" : "A") + fixture.Thumbprint[1..];
        var verifier = new WindowsInstallerExecutableTrustVerifier(different, new WindowsAuthenticodeVerifier(true));

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.signer_mismatch", failure.DiagnosticCode);
    }

    [Fact]
    public async Task UntrustedRootCannotMaskModifiedExecutableContents()
    {
        using var fixture = new UntrustedSignedFixture();
        fixture.ModifyCode();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(allowInternal: true).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.authenticode_invalid", failure.DiagnosticCode);
    }

    [Fact]
    public async Task InternalPolicyDoesNotAdmitAnArbitrarySelfSignedPublisher()
    {
        using var fixture = new UntrustedSignedFixture("CN=Other Untrusted Publisher");

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => fixture.Verifier(allowInternal: true).VerifyAsync(fixture.Path, CancellationToken.None));

        Assert.Equal("installer.elevation.certificate_untrusted", failure.DiagnosticCode);
    }

    private static string[] ReadRoots()
    {
        List<string> roots = [];
        foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            roots.AddRange(store.Certificates.Select(static certificate => certificate.Thumbprint));
        }
        return roots.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class UntrustedSignedFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClashSharp-Internal-Trust-" + Guid.NewGuid().ToString("N"));

        internal UntrustedSignedFixture(string? subject = null)
        {
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "Setup.exe");
            string host = System.IO.Path.GetFullPath(System.IO.Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            File.Copy(host, Path);
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest(subject ?? "CN=ClashSharp Internal Candidate " + Guid.NewGuid().ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            Thumbprint = certificate.Thumbprint;
            string pfx = System.IO.Path.Combine(_root, "owned-test-key.pfx");
            string password = Guid.NewGuid().ToString("N");
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
            try { Sign(pfx, password); }
            catch { Directory.Delete(_root, recursive: true); throw; }
            finally { if (File.Exists(pfx)) { File.Delete(pfx); } }
        }

        internal string Path { get; }
        internal string Thumbprint { get; }

        internal WindowsInstallerExecutableTrustVerifier Verifier(bool allowInternal) =>
            new(Thumbprint, new WindowsAuthenticodeVerifier(allowInternal));

        internal void ModifyCode()
        {
            int offset;
            using (var reader = new PEReader(File.OpenRead(Path)))
            {
                offset = reader.PEHeaders.SectionHeaders.Single(static section => section.Name == ".text").PointerToRawData;
            }
            using FileStream image = File.Open(Path, FileMode.Open, FileAccess.ReadWrite);
            image.Position = offset;
            int original = image.ReadByte();
            image.Position = offset;
            image.WriteByte((byte)(original ^ 1));
            image.Flush(flushToDisk: true);
        }

        private void Sign(string pfx, string password)
        {
            string sdk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
            string tool = Directory.GetDirectories(sdk).OrderDescending(StringComparer.Ordinal)
                .Select(static directory => System.IO.Path.Combine(directory, "x64", "signtool.exe")).First(File.Exists);
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "sign", "/fd", "SHA256", "/f", pfx, "/p", password, Path })
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Owned SignTool did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(5_000), "Owned SignTool did not exit.");
            }
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            Assert.Equal(0, process.ExitCode);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
