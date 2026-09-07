using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>Public candidate identity; the helper derives the account from its authenticated parent.</summary>
/// <param name="SessionId">Exact dedicated launch nonce.</param>
/// <param name="Operation">Install or Repair for a new transfer; recovery retains its durable operation.</param>
/// <param name="ExpectedPackageVersion">Exact package version of the candidate.</param>
/// <param name="InstallerPayloadSha256">Exact candidate payload digest.</param>
public sealed record InstallerOwnerTransferRequest(string SessionId, InstallerOperation Operation,
    string ExpectedPackageVersion, string InstallerPayloadSha256)
{
    /// <summary>Creates an ordinary request for the independently authenticated account.</summary>
    public InstallerRequest ForAuthenticatedAccount(string targetSid)
    {
        Validate();
        var request = new InstallerRequest(Operation, targetSid, false, ExpectedPackageVersion, InstallerPayloadSha256);
        request.Validate();
        return request;
    }

    /// <summary>Validates public fields without treating a wire account as an authority source.</summary>
    public void Validate()
    {
        InstallerProtocolValidation.ValidateLowerHex256(SessionId, "installer.owner_transfer.session_invalid");
        if (Operation is not (InstallerOperation.Install or InstallerOperation.Repair))
        {
            throw new InstallerProtocolException("installer.owner_transfer.operation_invalid");
        }
        InstallerProtocolValidation.ValidatePackageVersion(ExpectedPackageVersion);
        InstallerProtocolValidation.ValidateLowerHex256(InstallerPayloadSha256, "installer.owner_transfer.payload_invalid");
    }

    /// <summary>Requires the request to belong to this exact process launch.</summary>
    public void ValidateAgainst(InstallerOwnerTransferBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        Validate();
        if (SessionId != bootstrap.SessionId || InstallerPayloadSha256 != bootstrap.InstallerPayloadSha256)
        {
            throw new InstallerProtocolException("installer.owner_transfer.bootstrap_mismatch");
        }
    }
}
