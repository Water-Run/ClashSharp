using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Binds shared exact-certificate operations to physical LocalMachine/TrustedPeople. Reads require no
/// elevation; writes require the authenticated helper's elevation and independently checked payload.
/// </summary>
internal sealed class WindowsMachineCertificateStoreAdapter : WindowsCertificateStoreAdapter
{
    private readonly IWindowsMachineHelperElevationVerifier _elevation;
    private readonly IInstallerMachineCertificateReferences _references;

    internal WindowsMachineCertificateStoreAdapter()
        : this(WindowsMachineCertificateStoreNative.Instance, WindowsMachineHelperElevationVerifier.Instance,
            new WindowsMachineCertificateReferences())
    {
    }

    internal WindowsMachineCertificateStoreAdapter(IWindowsCertificateStoreNative native,
        IWindowsMachineHelperElevationVerifier elevation, IInstallerMachineCertificateReferences references)
        : base(native)
    {
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(references);
        _elevation = elevation;
        _references = references;
    }

    public override async Task ImportAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _elevation.VerifyElevated();
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Operation is not (InstallerOperation.Install or InstallerOperation.Repair)
            || release is not WindowsInstallerReleaseLease windowsLease)
        {
            throw new InstallerProtocolException("installer.machine_certificate.import_boundary_invalid");
        }
        byte[] bytes = windowsLease.RequireFile(InstallerPayloadFileRole.Certificate).ReadAllBytes(cancellationToken);
        try
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(bytes);
            if (certificate.Subject != release.Manifest.PackageIdentity.Publisher || certificate.HasPrivateKey
                || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime()
                || certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(static extension => extension.CertificateAuthority)
                || !certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(static extension =>
                    extension.EnhancedKeyUsages.Cast<Oid>().Any(static usage => usage.Value == "1.3.6.1.5.5.7.3.3")))
            {
                throw new InstallerProtocolException("installer.machine_certificate.publisher_certificate_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        await base.ImportAsync(request, release, cancellationToken).ConfigureAwait(false);
    }

    public override async Task RemoveExactAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _elevation.VerifyElevated();
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Operation != InstallerOperation.Uninstall)
        {
            throw new InstallerProtocolException("installer.machine_certificate.remove_boundary_invalid");
        }
        if (await _references.HasReferencesAsync(release.Manifest, cancellationToken).ConfigureAwait(false))
        {
            throw new InstallerProtocolException("installer.machine_certificate.still_referenced");
        }
        await base.RemoveExactAsync(request, release, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Offers no caller-controlled store name or location and never opens Root or CurrentUser.</summary>
internal sealed class WindowsMachineCertificateStoreNative : IWindowsCertificateStoreNative
{
    // Keep ownership within the local physical store; logical stores can contain policy siblings.
    private const nint SystemRegistryWide = 13;
    private const uint LocalMachine = 0x0002_0000;
    private const uint OpenExisting = 0x0000_4000;
    private const uint ReadOnly = 0x0000_8000;
    internal static WindowsMachineCertificateStoreNative Instance { get; } = new();

    private WindowsMachineCertificateStoreNative()
    {
    }

    public IWindowsCertificateStore? Open(string targetSid, bool writable, bool createIfMissing)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        uint flags = BuildOpenFlags(writable, createIfMissing);
        SafeWindowsCertificateStoreHandle handle = CertOpenStore(SystemRegistryWide, 0, 0, flags, "TrustedPeople");
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (!createIfMissing && error is 2 or 3 or unchecked((int)0x8009_2004))
            {
                return null;
            }
            throw new Win32Exception(error);
        }
        return new WindowsCertificateStore(handle, writable);
    }

    internal static uint BuildOpenFlags(bool writable, bool createIfMissing)
    {
        if (!writable && createIfMissing)
        {
            throw new InstallerProtocolException("installer.certificate.store_open_mode_invalid");
        }
        return LocalMachine | (createIfMissing ? 0 : OpenExisting) | (writable ? 0 : ReadOnly);
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWindowsCertificateStoreHandle CertOpenStore(
        nint storeProvider, uint encodingType, nint cryptographicProvider, uint flags, string parameter);
}
