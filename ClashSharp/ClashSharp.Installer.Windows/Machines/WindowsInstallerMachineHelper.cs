using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Composes the authenticated elevated helper from the exact manifest bytes embedded in the
/// signed Installer executable.
/// </summary>
public static class WindowsInstallerMachineHelper
{
    /// <summary>Runs the dedicated authenticated ownership exchange and its ordinary continuation.</summary>
    /// <param name="bootstrap">Dedicated launch nonce, candidate digest and parent PID.</param>
    /// <param name="installerExecutablePath">Current signed installer executable.</param>
    /// <param name="embeddedManifestBytes">Exact embedded manifest bytes.</param>
    /// <param name="cancellationToken">Cancels the owned session and drains its work.</param>
    public static Task RunOwnerTransferAsync(InstallerOwnerTransferBootstrap bootstrap, string installerExecutablePath,
        ReadOnlyMemory<byte> embeddedManifestBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerExecutablePath);
        bootstrap.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = embeddedManifestBytes.ToArray();
        InstallerReleaseManifest manifest = InstallerReleaseManifestCodec.Parse(bytes);
        var backend = new WindowsMachineHelperMachineBackend();
        var host = new WindowsOwnerTransferHost(installerExecutablePath, WindowsMachineHelperElevationVerifier.Instance,
            new WindowsInstallerExecutableTrustVerifier(manifest), new WindowsMachineHelperParentProcessVerifier(),
            new WindowsOwnerTransferTransportFactory(),
            new WindowsOwnerTransferOfferSource(new WindowsInstallerAuthorityLock(),
                new WindowsInstallerReleaseVerifier(bytes, installerExecutablePath), backend,
                () => new WindowsOwnerTransferInspection(backend)),
            WindowsOwnerTransferAuthorityFactory.CreateDefault(bytes, installerExecutablePath), WindowsMachineHelperHostLimits.Default);
        return host.RunAsync(bootstrap, cancellationToken);
    }

    /// <summary>Runs one bounded helper session without creating the WPF application.</summary>
    /// <param name="bootstrap">The exact first command and expected unelevated parent PID.</param>
    /// <param name="installerExecutablePath">The current signed Installer executable path.</param>
    /// <param name="embeddedManifestBytes">The exact embedded release-manifest bytes.</param>
    /// <param name="cancellationToken">Cancels before or during the bounded helper session.</param>
    public static Task RunAsync(
        InstallerMachineHelperBootstrap bootstrap,
        string installerExecutablePath,
        ReadOnlyMemory<byte> embeddedManifestBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerExecutablePath);
        bootstrap.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        // The operation executor is created only after endpoint/SID authentication. Snapshot now
        // so that delayed composition cannot observe caller-owned bytes different from trust setup.
        byte[] manifestBytes = embeddedManifestBytes.ToArray();
        InstallerReleaseManifest manifest = InstallerReleaseManifestCodec.Parse(manifestBytes);
        WindowsMachineHelperHost host = WindowsMachineHelperHost.CreateDefault(
            installerExecutablePath,
            manifest,
            manifestBytes);
        return host.RunAsync(bootstrap, cancellationToken);
    }
}
