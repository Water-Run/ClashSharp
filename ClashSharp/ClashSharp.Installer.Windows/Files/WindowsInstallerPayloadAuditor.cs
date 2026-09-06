using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Files;

/// <summary>Checks payload integrity without composing installation, elevation, or Windows mutation.</summary>
public sealed class WindowsInstallerPayloadAuditor
{
    private readonly InstallerReleaseManifest _manifest;
    private readonly string _payloadRoot;

    /// <summary>Validates immutable audit inputs without opening the payload.</summary>
    /// <param name="embeddedManifestBytes">Manifest bytes embedded in the executable being audited.</param>
    /// <param name="payloadRoot">Fully qualified local directory containing the sibling payload.</param>
    public WindowsInstallerPayloadAuditor(
        ReadOnlyMemory<byte> embeddedManifestBytes,
        string payloadRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        if (!Path.IsPathFullyQualified(payloadRoot))
        {
            throw new InstallerProtocolException("installer.release.payload_path_invalid");
        }

        string fullPayloadRoot = Path.GetFullPath(payloadRoot);
        if (fullPayloadRoot.Length < 3
            || !char.IsAsciiLetter(fullPayloadRoot[0])
            || fullPayloadRoot[1] != ':'
            || fullPayloadRoot[2] != Path.DirectorySeparatorChar)
        {
            throw new InstallerProtocolException("installer.release.payload_path_invalid");
        }

        _manifest = InstallerReleaseManifestCodec.Parse(embeddedManifestBytes.Span);
        _payloadRoot = fullPayloadRoot;
    }

    /// <summary>
    /// Pins and verifies exact payload bytes, identities, and directory shape, then releases all handles.
    /// This result does not assert executable trust, platform eligibility, or permission to install.
    /// </summary>
    /// <param name="cancellationToken">Cancels pending file inspection.</param>
    /// <returns>Bounded integrity evidence with no paths or user identity.</returns>
    public async Task<InstallerPayloadAuditResult> AuditAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new InstallerRequest(
            InstallerOperation.Install,
            WindowsInstallerCurrentUser.GetSid(),
            AllowReassociation: false,
            _manifest.ExpectedPackageVersion,
            _manifest.InstallerPayloadSha256);

        return await Task.Run(async () =>
        {
            await using WindowsInstallerReleaseLease lease = WindowsInstallerPayloadLocker.Lock(
                request,
                _manifest,
                _payloadRoot,
                cancellationToken);
            await lease.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
            return new InstallerPayloadAuditResult(
                _manifest.ExpectedPackageVersion,
                _manifest.InstallerPayloadSha256,
                _manifest.Files.Count,
                _manifest.Files.Sum(static file => file.Length),
                _manifest.MachineFiles.Count);
        }, cancellationToken).ConfigureAwait(false);
    }
}
