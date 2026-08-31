using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Machines;

/// <summary>One strict phase command sent through an authenticated elevated-helper session.</summary>
/// <param name="Schema">Command schema.</param>
/// <param name="Verb">Fixed privileged verb.</param>
/// <param name="TransactionId">Exact random transaction identity.</param>
/// <param name="JournalContentHash">SHA-256 of the exact durable journal bytes.</param>
/// <param name="JournalBase64">Canonical durable journal bytes carried without a filesystem path.</param>
public sealed record InstallerMachineHelperCommand(
    int Schema,
    InstallerMachineHelperVerb Verb,
    string TransactionId,
    string JournalContentHash,
    string JournalBase64)
{
    /// <summary>The only currently supported helper command schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Creates a wire command from one already-validated phase invocation.</summary>
    public static InstallerMachineHelperCommand Create(
        InstallerMachineHelperInvocation invocation,
        InstallerTransactionSnapshot durableState)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(durableState);
        durableState.Validate();
        invocation.ValidateAgainst(durableState);
        byte[] journalBytes = InstallerTransactionCodec.Serialize(durableState.Journal);
        return new(
            CurrentSchema,
            invocation.Verb,
            invocation.TransactionId,
            invocation.JournalContentHash,
            Convert.ToBase64String(journalBytes));
    }

    /// <summary>Validates schema, invocation fields, and exact journal-byte binding.</summary>
    public void Validate()
        => _ = ToDurableState();

    /// <summary>Returns the validated domain invocation carried by this command.</summary>
    public InstallerMachineHelperInvocation ToInvocation()
    {
        if (Schema != CurrentSchema)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.command_invalid");
        }

        var invocation = new InstallerMachineHelperInvocation(
            Verb,
            TransactionId,
            JournalContentHash);
        invocation.Validate();
        return invocation;
    }

    /// <summary>Returns the canonical journal snapshot proven by the carried bytes and digest.</summary>
    public InstallerTransactionSnapshot ToDurableState()
    {
        InstallerMachineHelperInvocation invocation = ToInvocation();
        if (string.IsNullOrWhiteSpace(JournalBase64))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.journal_payload_invalid");
        }

        byte[] journalBytes;
        try
        {
            journalBytes = Convert.FromBase64String(JournalBase64);
        }
        catch (FormatException exception)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.journal_payload_invalid",
                exception);
        }

        if (!string.Equals(
                JournalBase64,
                Convert.ToBase64String(journalBytes),
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.journal_payload_invalid");
        }

        InstallerTransactionJournal journal = InstallerTransactionCodec.Parse(journalBytes);
        var durableState = new InstallerTransactionSnapshot(journal, JournalContentHash);
        durableState.Validate();
        invocation.ValidateAgainst(durableState);
        return durableState;
    }

    /// <summary>Returns the only journal state a successful execution may durably commit.</summary>
    public InstallerTransactionSnapshot GetExpectedSuccessfulState()
    {
        InstallerTransactionSnapshot requestState = ToDurableState();
        InstallerTransactionJournal journal = requestState.Journal;
        InstallerTransactionPhase next = Verb switch
        {
            InstallerMachineHelperVerb.Prepare => journal.Operation == InstallerOperation.Uninstall
                ? InstallerTransactionPhase.MachineRemovalAuthorized
                : InstallerTransactionPhase.MachineReserved,
            InstallerMachineHelperVerb.CommitPackage =>
                InstallerTransactionPhase.PackageCommitted,
            InstallerMachineHelperVerb.Apply or InstallerMachineHelperVerb.Remove =>
                InstallerTransactionPhase.MachineCommitted,
            InstallerMachineHelperVerb.Verify => InstallerTransactionPhase.Verified,
            InstallerMachineHelperVerb.Clear => InstallerTransactionPhase.Verified,
            _ => throw new InstallerProtocolException(
                "installer.machine_helper.verb_invalid"),
        };
        InstallerTransactionJournal committed = journal.Phase == next
            ? journal
            : journal.TransitionTo(next);
        return InstallerTransactionSnapshot.Create(committed);
    }
}
