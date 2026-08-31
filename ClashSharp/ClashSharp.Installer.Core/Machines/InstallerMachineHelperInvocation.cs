using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Machines;

/// <summary>Fixed privileged operation accepted by the elevated copy of the installer.</summary>
public enum InstallerMachineHelperVerb
{
    /// <summary>Reserves ownership or authorizes owner-checked removal before mutation begins.</summary>
    Prepare,

    /// <summary>Independently verifies the target-user package result and commits that phase.</summary>
    CommitPackage,

    /// <summary>Stages, swaps, configures, and verifies the exact trusted machine payload.</summary>
    Apply,

    /// <summary>Performs owner-authorized removal of the fixed machine resources.</summary>
    Remove,

    /// <summary>Independently verifies the final installed or removed state.</summary>
    Verify,

    /// <summary>Deletes only the exact verified journal and proves its absence.</summary>
    Clear,
}

/// <summary>
/// Strict path-free command line binding an elevated helper to one durable journal snapshot.
/// </summary>
/// <param name="Verb">Fixed privileged operation.</param>
/// <param name="TransactionId">Exact durable transaction identifier.</param>
/// <param name="JournalContentHash">SHA-256 of the exact durable journal bytes.</param>
public sealed record InstallerMachineHelperInvocation(
    InstallerMachineHelperVerb Verb,
    string TransactionId,
    string JournalContentHash)
{
    private const string Mode = "--machine-helper";
    private const string TransactionOption = "--transaction-id";
    private const string JournalHashOption = "--journal-hash";
    private const string PipePrefix = "ClashSharp.Installer.Elevation.";

    /// <summary>Creates an invocation only when the verb matches the supplied durable phase.</summary>
    public static InstallerMachineHelperInvocation Create(
        InstallerMachineHelperVerb verb,
        InstallerTransactionSnapshot durableState)
    {
        ArgumentNullException.ThrowIfNull(durableState);
        durableState.Validate();
        var invocation = new InstallerMachineHelperInvocation(
            verb,
            durableState.Journal.TransactionId,
            durableState.ContentHash);
        invocation.ValidateAgainst(durableState);
        return invocation;
    }

    /// <summary>
    /// Parses the exact six-argument command-binding prefix used by the process bootstrap.
    /// </summary>
    public static InstallerMachineHelperInvocation? Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static argument => argument is null))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.arguments_invalid");
        }

        if (arguments.Count == 0
            || !string.Equals(arguments[0], Mode, StringComparison.Ordinal))
        {
            if (arguments.Any(static argument =>
                argument is not null
                && argument.StartsWith("--machine-", StringComparison.Ordinal)))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.arguments_invalid");
            }

            return null;
        }

        if (arguments.Count != 6
            || !TryParseVerb(arguments[1], out InstallerMachineHelperVerb verb)
            || !string.Equals(arguments[2], TransactionOption, StringComparison.Ordinal)
            || !string.Equals(arguments[4], JournalHashOption, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.arguments_invalid");
        }

        var invocation = new InstallerMachineHelperInvocation(
            verb,
            arguments[3],
            arguments[5]);
        invocation.Validate();
        return invocation;
    }

    /// <summary>Validates the path-free grammar fields without consulting mutable state.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Verb))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.verb_invalid");
        }

        InstallerProtocolValidation.ValidateLowerHex256(
            TransactionId,
            "installer.machine_helper.transaction_id_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            JournalContentHash,
            "installer.machine_helper.journal_hash_invalid");
    }

    /// <summary>Proves the invocation names the exact journal bytes and an allowed durable phase.</summary>
    public void ValidateAgainst(InstallerTransactionSnapshot durableState)
    {
        ArgumentNullException.ThrowIfNull(durableState);
        Validate();
        durableState.Validate();
        if (!string.Equals(
                TransactionId,
                durableState.Journal.TransactionId,
                StringComparison.Ordinal)
            || !string.Equals(
                JournalContentHash,
                durableState.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.transaction_mismatch");
        }

        bool allowed = (Verb, durableState.Journal.Operation, durableState.Journal.Phase) switch
        {
            (InstallerMachineHelperVerb.Prepare,
                InstallerOperation.Install or InstallerOperation.Repair or InstallerOperation.Uninstall,
                InstallerTransactionPhase.Prepared) => true,
            (InstallerMachineHelperVerb.Apply,
                InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.PackageCommitted) => true,
            (InstallerMachineHelperVerb.CommitPackage,
                InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.MachineReserved) => true,
            (InstallerMachineHelperVerb.CommitPackage,
                InstallerOperation.Uninstall,
                InstallerTransactionPhase.MachineCommitted) => true,
            (InstallerMachineHelperVerb.Remove,
                InstallerOperation.Uninstall,
                InstallerTransactionPhase.MachineRemovalAuthorized) => true,
            (InstallerMachineHelperVerb.Verify,
                InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.MachineCommitted or InstallerTransactionPhase.Verified) =>
                true,
            (InstallerMachineHelperVerb.Verify,
                InstallerOperation.Uninstall,
                InstallerTransactionPhase.PackageCommitted or InstallerTransactionPhase.Verified) =>
                true,
            (InstallerMachineHelperVerb.Clear,
                InstallerOperation.Install or InstallerOperation.Repair or InstallerOperation.Uninstall,
                InstallerTransactionPhase.Verified) => true,
            _ => false,
        };
        if (!allowed)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.phase_invalid");
        }
    }

    /// <summary>Returns the exact six path-free arguments bound to the first helper command.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        Validate();
        return
        [
            Mode,
            VerbText(Verb),
            TransactionOption,
            TransactionId,
            JournalHashOption,
            JournalContentHash,
        ];
    }

    /// <summary>Derives one stable bounded IPC session name from the random transaction identity.</summary>
    public string BuildSessionPipeName()
    {
        Validate();
        byte[] input = Encoding.UTF8.GetBytes(string.Concat(
            "ClashSharp.Installer.Elevation\0",
            TransactionId));
        byte[] digest = SHA256.HashData(input);
        try
        {
            return string.Concat(PipePrefix, Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool TryParseVerb(
        string value,
        out InstallerMachineHelperVerb verb)
    {
        verb = value switch
        {
            "prepare" => InstallerMachineHelperVerb.Prepare,
            "commit-package" => InstallerMachineHelperVerb.CommitPackage,
            "apply" => InstallerMachineHelperVerb.Apply,
            "remove" => InstallerMachineHelperVerb.Remove,
            "verify" => InstallerMachineHelperVerb.Verify,
            "clear" => InstallerMachineHelperVerb.Clear,
            _ => (InstallerMachineHelperVerb)(-1),
        };
        return Enum.IsDefined(verb);
    }

    private static string VerbText(InstallerMachineHelperVerb verb) => verb switch
    {
        InstallerMachineHelperVerb.Prepare => "prepare",
        InstallerMachineHelperVerb.CommitPackage => "commit-package",
        InstallerMachineHelperVerb.Apply => "apply",
        InstallerMachineHelperVerb.Remove => "remove",
        InstallerMachineHelperVerb.Verify => "verify",
        InstallerMachineHelperVerb.Clear => "clear",
        _ => throw new InstallerProtocolException("installer.machine_helper.verb_invalid"),
    };
}
