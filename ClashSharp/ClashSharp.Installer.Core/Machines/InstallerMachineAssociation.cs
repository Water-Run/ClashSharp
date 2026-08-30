using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>Strict machine-owned association consumed by the app and mihomo service.</summary>
/// <param name="SchemaVersion">Association schema.</param>
/// <param name="OwnerSid">Canonical interactive owner SID.</param>
/// <param name="AuthenticationToken">Canonical random 256-bit IPC credential.</param>
public sealed record InstallerMachineAssociation(
    int SchemaVersion,
    string OwnerSid,
    string AuthenticationToken)
{
    private const string PipePrefix = "ClashSharp.Mihomo.";

    /// <summary>The only association schema understood by the current app and service.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Creates and validates an association for one exact owner.</summary>
    public static InstallerMachineAssociation Create(
        string ownerSid,
        string authenticationToken)
    {
        var association = new InstallerMachineAssociation(
            CurrentSchema,
            ownerSid,
            authenticationToken);
        association.Validate();
        return association;
    }

    /// <summary>Validates schema, owner, and credential without resolving an account.</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchema)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_schema_invalid");
        }

        InstallerProtocolValidation.ValidateTargetSid(OwnerSid);
        InstallerProtocolValidation.ValidateLowerHex256(
            AuthenticationToken,
            "installer.machine.authentication_token_invalid");
    }

    /// <summary>Generates a cryptographically random canonical IPC credential.</summary>
    public static string GenerateAuthenticationToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Derives the service pipe name shared by the installed app, service, and machine helper.
    /// </summary>
    /// <returns>A stable name that does not expose the owner SID or authentication token.</returns>
    public string BuildServicePipeName()
    {
        Validate();
        byte[] input = Encoding.UTF8.GetBytes(string.Concat(
            "ClashSharp.Mihomo.IPC\0",
            OwnerSid,
            "\0",
            AuthenticationToken));
        byte[] digest = SHA256.HashData(input);
        try
        {
            return string.Concat(
                PipePrefix,
                Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}
