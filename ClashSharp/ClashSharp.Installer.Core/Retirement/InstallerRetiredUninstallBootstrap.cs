using System.Globalization;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Retirement;

/// <summary>Identifies the separate, explicitly requested account-copy removal helper.</summary>
/// <param name="SessionId">Fresh random parent-owned pipe identity.</param>
/// <param name="InstallerPayloadSha256">Exact candidate payload bound into the helper launch.</param>
/// <param name="ParentProcessId">Unelevated parent PID to authenticate independently.</param>
public sealed record InstallerRetiredUninstallBootstrap(string SessionId, string InstallerPayloadSha256, int ParentProcessId)
{
    private const string Mode = "--retired-uninstall-helper";

    /// <summary>Creates a fresh path-free identity without starting a helper or touching state.</summary>
    public static InstallerRetiredUninstallBootstrap Create(string installerPayloadSha256, int parentProcessId)
    {
        var result = new InstallerRetiredUninstallBootstrap(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), installerPayloadSha256, parentProcessId);
        result.Validate();
        return result;
    }

    /// <summary>Validates the canonical nonce, payload digest and positive parent PID.</summary>
    public void Validate()
    {
        InstallerProtocolValidation.ValidateLowerHex256(SessionId, "installer.retired_uninstall.session_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(InstallerPayloadSha256, "installer.retired_uninstall.payload_invalid");
        if (ParentProcessId <= 0)
        {
            throw InvalidArguments();
        }
    }

    /// <summary>Builds a pipe namespace distinct from ordinary and owner-transfer helpers.</summary>
    public string BuildSessionPipeName()
    {
        Validate();
        return $"ClashSharp.Installer.RetiredUninstall.{SessionId}.{InstallerPayloadSha256}";
    }

    /// <summary>Returns the exact seven launch arguments; no user SID or path is accepted.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        Validate();
        return [Mode, "--retired-session", SessionId, "--payload-sha256", InstallerPayloadSha256,
            "--machine-parent-pid", ParentProcessId.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>Parses this mode and rejects malformed or misplaced reserved arguments.</summary>
    public static InstallerRetiredUninstallBootstrap? Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static argument => argument is null))
        {
            throw InvalidArguments();
        }
        if (arguments.Count == 0 || arguments[0] != Mode)
        {
            if (arguments.Any(static argument => argument.StartsWith("--retired-", StringComparison.OrdinalIgnoreCase)))
            {
                throw InvalidArguments();
            }
            return null;
        }
        if (arguments.Count != 7 || arguments[1] != "--retired-session" || arguments[3] != "--payload-sha256"
            || arguments[5] != "--machine-parent-pid"
            || !int.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || arguments[6] != processId.ToString(CultureInfo.InvariantCulture))
        {
            throw InvalidArguments();
        }
        var result = new InstallerRetiredUninstallBootstrap(arguments[2], arguments[4], processId);
        result.Validate();
        return result;
    }

    private static InstallerProtocolException InvalidArguments() => new("installer.retired_uninstall.arguments_invalid");
}
