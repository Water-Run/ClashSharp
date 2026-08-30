namespace ClashSharp.Installer.Contracts;

/// <summary>Binds one installer request to an exact user, release, and payload.</summary>
public sealed record InstallerRequest(
    InstallerOperation Operation,
    string TargetSid,
    bool AllowReassociation,
    string ExpectedPackageVersion,
    string InstallerPayloadSha256)
{
    /// <summary>Validates every request field using the canonical transaction rules.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Operation))
        {
            throw new InstallerProtocolException("installer.request.operation_invalid");
        }

        InstallerProtocolValidation.ValidateTargetSid(TargetSid);
        InstallerProtocolValidation.ValidatePackageVersion(ExpectedPackageVersion);
        InstallerProtocolValidation.ValidateLowerHex256(
            InstallerPayloadSha256,
            "installer.request.payload_hash_invalid");

        if (Operation != InstallerOperation.Repair && AllowReassociation)
        {
            throw new InstallerProtocolException("installer.request.reassociation_invalid");
        }
    }
}
