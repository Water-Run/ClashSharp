using System.Globalization;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>Path-free launch identity for a separate, explicitly confirmed ownership exchange.</summary>
/// <param name="SessionId">Fresh random identity of the parent-owned pipe.</param>
/// <param name="InstallerPayloadSha256">Candidate payload bound into the helper launch.</param>
/// <param name="ParentProcessId">Unelevated server PID to authenticate independently.</param>
public sealed record InstallerOwnerTransferBootstrap(string SessionId, string InstallerPayloadSha256, int ParentProcessId)
{
    private const string Mode = "--owner-transfer-helper";

    /// <summary>Creates a fresh session without creating any machine or user state.</summary>
    public static InstallerOwnerTransferBootstrap Create(string installerPayloadSha256, int parentProcessId)
    {
        var bootstrap = new InstallerOwnerTransferBootstrap(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), installerPayloadSha256, parentProcessId);
        bootstrap.Validate();
        return bootstrap;
    }

    /// <summary>Validates the canonical nonce, payload identity and parent PID.</summary>
    public void Validate()
    {
        InstallerProtocolValidation.ValidateLowerHex256(SessionId, "installer.owner_transfer.session_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(InstallerPayloadSha256, "installer.owner_transfer.payload_invalid");
        if (ParentProcessId <= 0)
        {
            throw InvalidArguments();
        }
    }

    /// <summary>Returns a pipe name in a namespace separate from ordinary transaction commands.</summary>
    public string BuildSessionPipeName()
    {
        Validate();
        return $"ClashSharp.Installer.OwnerTransfer.{SessionId}.{InstallerPayloadSha256}";
    }

    /// <summary>Returns the exact seven arguments; no account, profile or credential is accepted.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        Validate();
        return [Mode, "--transfer-session", SessionId, "--payload-sha256", InstallerPayloadSha256,
            "--machine-parent-pid", ParentProcessId.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>Parses this dedicated mode, rejecting misplaced or incomplete reserved arguments.</summary>
    public static InstallerOwnerTransferBootstrap? Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static value => value is null))
        {
            throw InvalidArguments();
        }
        if (arguments.Count == 0 || arguments[0] != Mode)
        {
            if (arguments.Any(static value => value.StartsWith("--owner-transfer", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("--transfer-", StringComparison.OrdinalIgnoreCase)))
            {
                throw InvalidArguments();
            }
            return null;
        }
        if (arguments.Count != 7 || arguments[1] != "--transfer-session" || arguments[3] != "--payload-sha256"
            || arguments[5] != "--machine-parent-pid"
            || !int.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || arguments[6] != processId.ToString(CultureInfo.InvariantCulture))
        {
            throw InvalidArguments();
        }
        var bootstrap = new InstallerOwnerTransferBootstrap(arguments[2], arguments[4], processId);
        bootstrap.Validate();
        return bootstrap;
    }

    private static InstallerProtocolException InvalidArguments() => new("installer.owner_transfer.arguments_invalid");
}
