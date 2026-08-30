using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Files;

/// <summary>
/// Establishes one immutable sibling-payload lease from a manifest compiled into the signed EXE.
/// </summary>
public sealed class WindowsInstallerReleaseVerifier : IInstallerReleaseVerifier
{
    private readonly InstallerReleaseManifest _embeddedManifest;
    private readonly string _payloadRoot;

    /// <summary>Creates a verifier over signed, compile-time embedded manifest bytes.</summary>
    /// <param name="embeddedManifestBytes">
    /// Manifest bytes obtained from a generated resource in the signed installer, never a sidecar.
    /// </param>
    public WindowsInstallerReleaseVerifier(ReadOnlyMemory<byte> embeddedManifestBytes)
        : this(embeddedManifestBytes, Environment.ProcessPath)
    {
    }

    internal WindowsInstallerReleaseVerifier(
        ReadOnlyMemory<byte> embeddedManifestBytes,
        string? executablePath)
    {
        if (embeddedManifestBytes.IsEmpty)
        {
            throw new InstallerProtocolException("installer.release.manifest_missing");
        }

        string fullExecutablePath = executablePath is { } processPath
            ? Path.GetFullPath(processPath)
            : throw new InstallerProtocolException("installer.release.executable_path_invalid");
        string? executableDirectory = Path.GetDirectoryName(fullExecutablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory)
            || !string.Equals(
                Path.GetFileName(fullExecutablePath),
                "ClashSharp.Installer.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException("installer.release.executable_path_invalid");
        }

        _embeddedManifest = InstallerReleaseManifestCodec.Parse(embeddedManifestBytes.Span);
        _payloadRoot = Path.Combine(executableDirectory, "payload");
    }

    /// <inheritdoc />
    public async Task<IInstallerReleaseLease> VerifyAsync(
        InstallerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                request.ExpectedPackageVersion,
                _embeddedManifest.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                _embeddedManifest.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.release.identity_mismatch");
        }

        if (request.Operation == InstallerOperation.Uninstall)
        {
            IInstallerReleaseLease uninstallLease = new WindowsInstallerReleaseLease(
                request,
                _embeddedManifest,
                payloadRoot: null,
                lockedFiles: [],
                directoryGuards: []);
            return uninstallLease;
        }

        return await Task.Run(
                () => WindowsInstallerPayloadLocker.Lock(
                    request,
                    _embeddedManifest,
                    _payloadRoot,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
