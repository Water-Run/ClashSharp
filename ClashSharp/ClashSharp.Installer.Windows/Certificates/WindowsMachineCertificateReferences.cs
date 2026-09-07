using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Machines;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Conservatively retains publisher trust for any all-user or provisioned package with the same
/// publisher identifier, including a different product or signing certificate from that publisher.
/// </summary>
internal sealed class WindowsMachineCertificateReferences : IInstallerMachineCertificateReferences
{
    public Task<bool> HasReferencesAsync(InstallerReleaseManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        WindowsMachineHelperElevationVerifier.Instance.VerifyElevated();
        try
        {
            var manager = new PackageManager();
            return Task.FromResult(ContainsPublisher(manager.FindPackages(), manifest, cancellationToken)
                || ContainsPublisher(manager.FindProvisionedPackages(), manifest, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            throw new InstallerProtocolException("installer.machine_certificate.reference_query_failed", exception);
        }
    }

    private static bool ContainsPublisher(IEnumerable<Package> packages, InstallerReleaseManifest manifest, CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (Package package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 16384)
            {
                throw new InstallerProtocolException("installer.machine_certificate.reference_inventory_exceeded");
            }
            if (string.Equals(package.Id.PublisherId, manifest.PackageIdentity.PublisherId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }
}
