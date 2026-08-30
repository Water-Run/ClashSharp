using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Machines;

/// <summary>Terminal outcome explicitly reported by a still-connected elevated helper.</summary>
public enum InstallerMachineHelperOutcome
{
    /// <summary>The helper independently verified the requested verb's postcondition.</summary>
    Succeeded,

    /// <summary>The helper terminated after reporting a stable deterministic failure.</summary>
    Failed,

    /// <summary>A previously committed state no longer satisfies its independently checked postcondition.</summary>
    PostconditionFailed,
}

/// <summary>Bounded helper response binding a request journal to helper-committed journal bytes.</summary>
/// <param name="Schema">Response schema.</param>
/// <param name="Verb">Executed fixed helper verb.</param>
/// <param name="TransactionId">Exact durable transaction identifier.</param>
/// <param name="JournalContentHash">Exact request journal SHA-256.</param>
/// <param name="ResultJournalContentHash">SHA-256 of the helper's resulting journal bytes.</param>
/// <param name="ResultJournalBase64">Canonical helper-result journal bytes encoded as Base64.</param>
/// <param name="Outcome">Reported terminal outcome.</param>
/// <param name="PostconditionVerified">Whether the helper independently verified the verb postcondition.</param>
/// <param name="DiagnosticCode">Stable non-localized result code.</param>
public sealed record InstallerMachineHelperResult(
    int Schema,
    InstallerMachineHelperVerb Verb,
    string TransactionId,
    string JournalContentHash,
    string ResultJournalContentHash,
    string ResultJournalBase64,
    InstallerMachineHelperOutcome Outcome,
    bool PostconditionVerified,
    string DiagnosticCode)
{
    /// <summary>The only currently supported helper response schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Creates a successful response for the exact state the helper durably committed.</summary>
    public static InstallerMachineHelperResult Succeeded(
        InstallerMachineHelperCommand command,
        InstallerTransactionSnapshot committedState)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(committedState);
        command.Validate();
        committedState.Validate();
        InstallerMachineHelperInvocation invocation = command.ToInvocation();
        var result = new InstallerMachineHelperResult(
            CurrentSchema,
            invocation.Verb,
            invocation.TransactionId,
            invocation.JournalContentHash,
            committedState.ContentHash,
            EncodeJournal(committedState),
            InstallerMachineHelperOutcome.Succeeded,
            PostconditionVerified: true,
            "installer.machine_helper.completed");
        _ = result.ValidateAgainst(command);
        return result;
    }

    /// <summary>Creates a deterministic failed response that cannot claim a phase advance.</summary>
    public static InstallerMachineHelperResult Failed(
        InstallerMachineHelperCommand command,
        string diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(command);
        InstallerTransactionSnapshot requestState = command.ToDurableState();
        InstallerProtocolValidation.ValidateDiagnosticCode(diagnosticCode);
        if (string.Equals(
                diagnosticCode,
                "installer.machine_helper.completed",
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_invalid");
        }

        InstallerMachineHelperInvocation invocation = command.ToInvocation();
        var result = new InstallerMachineHelperResult(
            CurrentSchema,
            invocation.Verb,
            invocation.TransactionId,
            invocation.JournalContentHash,
            requestState.ContentHash,
            EncodeJournal(requestState),
            InstallerMachineHelperOutcome.Failed,
            PostconditionVerified: false,
            diagnosticCode);
        _ = result.ValidateAgainst(command);
        return result;
    }

    /// <summary>Creates a stable failed replay without regressing the already-committed journal.</summary>
    public static InstallerMachineHelperResult PostconditionFailed(
        InstallerMachineHelperCommand command,
        InstallerTransactionSnapshot committedState,
        string diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(committedState);
        InstallerTransactionSnapshot expected = command.GetExpectedSuccessfulState();
        committedState.Validate();
        InstallerProtocolValidation.ValidateDiagnosticCode(diagnosticCode);
        if (committedState != expected
            || string.Equals(
                diagnosticCode,
                "installer.machine_helper.completed",
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_invalid");
        }

        InstallerMachineHelperInvocation invocation = command.ToInvocation();
        var result = new InstallerMachineHelperResult(
            CurrentSchema,
            invocation.Verb,
            invocation.TransactionId,
            invocation.JournalContentHash,
            committedState.ContentHash,
            EncodeJournal(committedState),
            InstallerMachineHelperOutcome.PostconditionFailed,
            PostconditionVerified: false,
            diagnosticCode);
        _ = result.ValidateAgainst(command);
        return result;
    }

    /// <summary>Validates schema, immutable bindings, resulting journal, outcome, and diagnostic.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema || !Enum.IsDefined(Outcome))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_invalid");
        }

        var invocation = new InstallerMachineHelperInvocation(
            Verb,
            TransactionId,
            JournalContentHash);
        invocation.Validate();
        InstallerTransactionSnapshot resultState = ToResultDurableState();
        if (!string.Equals(
                resultState.Journal.TransactionId,
                TransactionId,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_invalid");
        }

        InstallerProtocolValidation.ValidateDiagnosticCode(DiagnosticCode);
        bool validOutcome = Outcome switch
        {
            InstallerMachineHelperOutcome.Succeeded => PostconditionVerified
                && string.Equals(
                    DiagnosticCode,
                    "installer.machine_helper.completed",
                    StringComparison.Ordinal),
            InstallerMachineHelperOutcome.Failed => !PostconditionVerified
                && !string.Equals(
                    DiagnosticCode,
                    "installer.machine_helper.completed",
                    StringComparison.Ordinal),
            InstallerMachineHelperOutcome.PostconditionFailed => !PostconditionVerified
                && !string.Equals(
                    DiagnosticCode,
                    "installer.machine_helper.completed",
                    StringComparison.Ordinal),
            _ => false,
        };
        if (!validOutcome)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_invalid");
        }
    }

    /// <summary>
    /// Validates this response against the full request and returns the helper-authoritative state.
    /// </summary>
    public InstallerTransactionSnapshot ValidateAgainst(
        InstallerMachineHelperCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate();
        InstallerTransactionSnapshot requestState = command.ToDurableState();
        InstallerMachineHelperInvocation invocation = command.ToInvocation();
        if (Verb != invocation.Verb
            || !string.Equals(TransactionId, invocation.TransactionId, StringComparison.Ordinal)
            || !string.Equals(
                JournalContentHash,
                invocation.JournalContentHash,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_mismatch");
        }

        InstallerTransactionSnapshot resultState = ToResultDurableState();
        InstallerTransactionSnapshot expected = Outcome switch
        {
            InstallerMachineHelperOutcome.Succeeded =>
                command.GetExpectedSuccessfulState(),
            InstallerMachineHelperOutcome.Failed => requestState,
            InstallerMachineHelperOutcome.PostconditionFailed =>
                command.GetExpectedSuccessfulState(),
            _ => throw new InstallerProtocolException(
                "installer.machine_helper.result_invalid"),
        };
        if (resultState != expected)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_mismatch");
        }

        return resultState;
    }

    /// <summary>Decodes and validates the canonical journal carried by this result.</summary>
    public InstallerTransactionSnapshot ToResultDurableState()
    {
        InstallerProtocolValidation.ValidateLowerHex256(
            ResultJournalContentHash,
            "installer.machine_helper.result_journal_hash_invalid");
        if (string.IsNullOrWhiteSpace(ResultJournalBase64))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_journal_payload_invalid");
        }

        byte[] journalBytes;
        try
        {
            journalBytes = Convert.FromBase64String(ResultJournalBase64);
        }
        catch (FormatException exception)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_journal_payload_invalid",
                exception);
        }

        if (!string.Equals(
                ResultJournalBase64,
                Convert.ToBase64String(journalBytes),
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_journal_payload_invalid");
        }

        InstallerTransactionJournal journal = InstallerTransactionCodec.Parse(journalBytes);
        var durableState = new InstallerTransactionSnapshot(
            journal,
            ResultJournalContentHash);
        durableState.Validate();
        return durableState;
    }

    private static string EncodeJournal(InstallerTransactionSnapshot durableState)
    {
        byte[] bytes = InstallerTransactionCodec.Serialize(durableState.Journal);
        var canonical = new InstallerTransactionSnapshot(
            durableState.Journal,
            durableState.ContentHash);
        canonical.Validate();
        return Convert.ToBase64String(bytes);
    }
}
