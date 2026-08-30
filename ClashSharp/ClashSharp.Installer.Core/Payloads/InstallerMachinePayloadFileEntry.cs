using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Payloads;

/// <summary>One exact machine-scope file contained in the trusted primary MSIX.</summary>
/// <param name="Path">Canonical lowercase path inside the primary MSIX.</param>
/// <param name="Length">Exact nonzero uncompressed file length.</param>
/// <param name="Sha256">Exact lowercase SHA-256 of the uncompressed file.</param>
public sealed record InstallerMachinePayloadFileEntry(
    string Path,
    long Length,
    string Sha256)
{
    /// <summary>Validates the canonical path, per-file budget, and digest.</summary>
    public void Validate()
    {
        InstallerManifestPathValidation.ValidateCanonicalRelativePath(Path);
        long maximumLength = Path switch
        {
            "binaries/geodata/manifest.json" =>
                InstallerPayloadBudgets.MaximumGeoDataManifestBytes,
            _ when Path.StartsWith("binaries/geodata/", StringComparison.Ordinal) =>
                InstallerPayloadBudgets.MaximumGeoDataAssetBytes,
            _ => InstallerPayloadBudgets.MaximumFileBytes,
        };
        if (Length is <= 0 || Length > maximumLength)
        {
            throw new InstallerProtocolException(
                "installer.release.machine_file_size_invalid");
        }

        InstallerProtocolValidation.ValidateLowerHex256(
            Sha256,
            "installer.release.machine_file_hash_invalid");
    }
}
