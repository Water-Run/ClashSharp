using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Files;

internal static class WindowsMsixIdentityVerifier
{
    internal static void Verify(
        InstallerReleaseManifest manifest,
        IReadOnlyList<WindowsLockedPayloadFile> lockedFiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(lockedFiles);
        manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        WindowsLockedPayloadFile primary = RequireLockedFile(
            lockedFiles,
            manifest.Files.Single(static file =>
                file.Role == InstallerPayloadFileRole.PrimaryPackage));
        using (FileStream stream = primary.OpenVerifiedReadStream())
        {
            InstallerMsixPackageVerifier.VerifyPrimary(stream, manifest, cancellationToken);
        }

        foreach (InstallerDependencyPackageIdentity dependency in manifest.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallerPayloadFileEntry entry = manifest.Files.Single(file =>
                file.Role == InstallerPayloadFileRole.DependencyPackage
                && file.Path == dependency.Path);
            WindowsLockedPayloadFile lockedFile = RequireLockedFile(lockedFiles, entry);
            using FileStream stream = lockedFile.OpenVerifiedReadStream();
            InstallerMsixPackageVerifier.VerifyDependency(stream, dependency, cancellationToken);
        }
    }

    private static WindowsLockedPayloadFile RequireLockedFile(
        IReadOnlyList<WindowsLockedPayloadFile> lockedFiles,
        InstallerPayloadFileEntry entry)
    {
        WindowsLockedPayloadFile[] matches = lockedFiles
            .Where(file => file.ManifestEntry == entry)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InstallerProtocolException("installer.release.locked_file_set_invalid");
    }
}
