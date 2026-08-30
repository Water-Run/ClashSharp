using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Payloads;

/// <summary>One exact file embedded in the signed installer release manifest.</summary>
/// <param name="Path">Canonical lowercase path relative to the sibling payload root.</param>
/// <param name="Role">The file's unique security role.</param>
/// <param name="Length">Exact nonzero file length.</param>
/// <param name="Sha256">Exact lowercase SHA-256 of the complete file.</param>
public sealed record InstallerPayloadFileEntry(
    string Path,
    InstallerPayloadFileRole Role,
    long Length,
    string Sha256)
{
    /// <summary>Validates canonical path, role, length, and digest rules.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Role))
        {
            throw new InstallerProtocolException("installer.release.manifest_file_invalid");
        }

        InstallerManifestPathValidation.ValidateCanonicalRelativePath(Path);
        if (Length is <= 0 or > InstallerPayloadBudgets.MaximumFileBytes)
        {
            throw new InstallerProtocolException("installer.release.payload_file_size_invalid");
        }

        InstallerProtocolValidation.ValidateLowerHex256(
            Sha256,
            "installer.release.manifest_file_hash_invalid");

        bool shapeMatchesRole = Role switch
        {
            InstallerPayloadFileRole.PrimaryPackage =>
                !Path.Contains('/') && Path.EndsWith(".msix", StringComparison.Ordinal),
            InstallerPayloadFileRole.Certificate =>
                Path == "clashsharp_temporarykey.cer"
                && Length <= InstallerPayloadBudgets.MaximumCertificateBytes,
            InstallerPayloadFileRole.Provenance =>
                Path == "payload-provenance.json"
                && Length <= InstallerPayloadBudgets.MaximumProvenanceBytes,
            InstallerPayloadFileRole.DependencyPackage =>
                Path.StartsWith("dependencies/x64/", StringComparison.Ordinal)
                && Path.Length > "dependencies/x64/.msix".Length
                && Path.EndsWith(".msix", StringComparison.Ordinal)
                && Path["dependencies/x64/".Length..].IndexOf('/') < 0,
            _ => false,
        };
        if (!shapeMatchesRole)
        {
            throw new InstallerProtocolException("installer.release.manifest_file_invalid");
        }
    }

}
