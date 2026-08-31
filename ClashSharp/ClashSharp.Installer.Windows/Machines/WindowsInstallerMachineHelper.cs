using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Composes the authenticated elevated helper from the exact manifest bytes embedded in the
/// signed Installer executable.
/// </summary>
public static class WindowsInstallerMachineHelper
{
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
