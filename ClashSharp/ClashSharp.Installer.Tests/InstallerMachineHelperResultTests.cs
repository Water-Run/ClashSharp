using System.Text;
using System.Text.Json.Nodes;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperResultTests
{
    [Theory]
    [InlineData(InstallerMachineHelperVerb.Prepare, true)]
    [InlineData(InstallerMachineHelperVerb.Prepare, false)]
    [InlineData(InstallerMachineHelperVerb.CommitPackage, true)]
    [InlineData(InstallerMachineHelperVerb.CommitPackage, false)]
    [InlineData(InstallerMachineHelperVerb.Apply, true)]
    [InlineData(InstallerMachineHelperVerb.Apply, false)]
    [InlineData(InstallerMachineHelperVerb.Remove, true)]
    [InlineData(InstallerMachineHelperVerb.Remove, false)]
    [InlineData(InstallerMachineHelperVerb.Verify, true)]
    [InlineData(InstallerMachineHelperVerb.Verify, false)]
    [InlineData(InstallerMachineHelperVerb.Clear, true)]
    [InlineData(InstallerMachineHelperVerb.Clear, false)]
    public void CanonicalTerminalResultsRoundTripAndBindRequestToResultJournal(
        InstallerMachineHelperVerb verb,
        bool succeeded)
    {
        InstallerMachineHelperCommand command = Command(verb);
        InstallerTransactionSnapshot expectedState = succeeded
            ? SuccessfulState(command)
            : command.ToDurableState();
        InstallerMachineHelperResult expected = succeeded
            ? InstallerMachineHelperResult.Succeeded(command, expectedState)
            : InstallerMachineHelperResult.Failed(
                command,
                "installer.machine.apply_failed");

        byte[] bytes = InstallerMachineHelperResultCodec.Serialize(expected);
        InstallerMachineHelperResult actual = InstallerMachineHelperResultCodec.Parse(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(expectedState, actual.ValidateAgainst(command));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.DoesNotContain((byte)'\n', bytes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schema\":1,}")]
    [InlineData("{/*comment*/\"schema\":1}")]
    public void IncompleteOrNoncanonicalJsonIsRejected(string json) =>
        AssertDiagnostic(
            () => InstallerMachineHelperResultCodec.Parse(Encoding.UTF8.GetBytes(json)),
            "installer.machine_helper.result_json_invalid");

    [Fact]
    public void UnknownDuplicateAndCaseChangedPropertiesAreRejected()
    {
        string canonical = CanonicalJson();
        AssertJsonInvalid(canonical.Replace(
            "\"diagnosticCode\":",
            "\"unexpected\":true,\"diagnosticCode\":",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace(
            "\"transactionId\":",
            $"\"transactionId\":\"{InstallerTestData.TransactionId}\",\"transactionId\":",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace(
            "\"resultJournalBase64\"",
            "\"ResultJournalBase64\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void PropertyTypesAndIntegerEnumsAreRejected()
    {
        JsonObject schema = JsonNode.Parse(CanonicalJson())!.AsObject();
        schema["schema"] = "1";
        AssertJsonInvalid(schema.ToJsonString());

        JsonObject verb = JsonNode.Parse(CanonicalJson())!.AsObject();
        verb["verb"] = 0;
        AssertJsonInvalid(verb.ToJsonString());

        JsonObject resultJournal = JsonNode.Parse(CanonicalJson())!.AsObject();
        resultJournal["resultJournalBase64"] = 1;
        AssertJsonInvalid(resultJournal.ToJsonString());

        JsonObject outcome = JsonNode.Parse(CanonicalJson())!.AsObject();
        outcome["outcome"] = 0;
        AssertJsonInvalid(outcome.ToJsonString());

        JsonObject unknownOutcome = JsonNode.Parse(CanonicalJson())!.AsObject();
        unknownOutcome["outcome"] = "unknown";
        AssertJsonInvalid(unknownOutcome.ToJsonString());

        JsonObject verified = JsonNode.Parse(CanonicalJson())!.AsObject();
        verified["postconditionVerified"] = "true";
        AssertJsonInvalid(verified.ToJsonString());
    }

    [Fact]
    public void WhitespaceAndPropertyReorderingAreRejected()
    {
        string canonical = CanonicalJson();
        AssertJsonInvalid(canonical.Insert(1, " "));
        AssertJsonInvalid(canonical.Replace(
            "{\"schema\":1,\"verb\":\"apply\",",
            "{\"verb\":\"apply\",\"schema\":1,",
            StringComparison.Ordinal));
    }

    [Fact]
    public void OutcomeVerificationAndDiagnosticMustAgree()
    {
        InstallerMachineHelperCommand command = Command();
        InstallerMachineHelperResult success = InstallerMachineHelperResult.Succeeded(
            command,
            SuccessfulState(command));
        AssertDiagnostic(
            () => (success with { PostconditionVerified = false }).Validate(),
            "installer.machine_helper.result_invalid");
        AssertDiagnostic(
            () => (success with
            {
                Outcome = InstallerMachineHelperOutcome.Failed,
            }).Validate(),
            "installer.machine_helper.result_invalid");
        AssertDiagnostic(
            () => (success with
            {
                DiagnosticCode = "installer.machine.apply_failed",
            }).Validate(),
            "installer.machine_helper.result_invalid");
        AssertDiagnostic(
            () => (success with
            {
                DiagnosticCode = "installer.machine/path",
            }).Validate(),
            "installer.diagnostic_code_invalid");
    }

    [Fact]
    public void SuccessCannotClaimTheRequestPhaseOrSkipTheExpectedCommit()
    {
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare);
        InstallerTransactionSnapshot requestState = command.ToDurableState();
        InstallerTransactionJournal skipped = requestState.Journal
            .TransitionTo(InstallerTransactionPhase.MachineReserved)
            .TransitionTo(InstallerTransactionPhase.PackageCommitted);

        AssertDiagnostic(
            () => InstallerMachineHelperResult.Succeeded(command, requestState),
            "installer.machine_helper.result_mismatch");
        AssertDiagnostic(
            () => InstallerMachineHelperResult.Succeeded(
                command,
                InstallerTransactionSnapshot.Create(skipped)),
            "installer.machine_helper.result_mismatch");
    }

    [Fact]
    public void UninstallPrepareCanCommitOnlyTheExplicitRemovalAuthorization()
    {
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            InstallerOperation.Uninstall);
        InstallerTransactionSnapshot authorized = SuccessfulState(command);

        InstallerMachineHelperResult result = InstallerMachineHelperResult.Succeeded(
            command,
            authorized);

        Assert.Equal(
            InstallerTransactionPhase.MachineRemovalAuthorized,
            result.ValidateAgainst(command).Journal.Phase);
    }

    [Fact]
    public void FailureCannotClaimTheSuccessfulPhaseAndResultCannotCrossACommand()
    {
        InstallerMachineHelperCommand command = Command();
        InstallerMachineHelperResult success = InstallerMachineHelperResult.Succeeded(
            command,
            SuccessfulState(command));
        InstallerMachineHelperResult dishonestFailure = success with
        {
            Outcome = InstallerMachineHelperOutcome.Failed,
            PostconditionVerified = false,
            DiagnosticCode = "installer.machine.apply_failed",
        };

        AssertDiagnostic(
            () => dishonestFailure.ValidateAgainst(command),
            "installer.machine_helper.result_mismatch");
        AssertDiagnostic(
            () => success.ValidateAgainst(Command(
                transactionId: InstallerTestData.OtherHash)),
            "installer.machine_helper.result_mismatch");
        AssertDiagnostic(
            () => (success with { Verb = InstallerMachineHelperVerb.Verify })
                .ValidateAgainst(command),
            "installer.machine_helper.result_mismatch");
    }

    [Fact]
    public void CommittedReplayFailureCarriesTheExpectedStateWithoutClaimingVerification()
    {
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare);
        InstallerTransactionSnapshot committed = SuccessfulState(command);
        InstallerMachineHelperResult expected =
            InstallerMachineHelperResult.PostconditionFailed(
                command,
                committed,
                "installer.machine.replay_verification_failed");

        byte[] bytes = InstallerMachineHelperResultCodec.Serialize(expected);
        InstallerMachineHelperResult actual =
            InstallerMachineHelperResultCodec.Parse(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(InstallerMachineHelperOutcome.PostconditionFailed, actual.Outcome);
        Assert.False(actual.PostconditionVerified);
        Assert.Equal(committed, actual.ValidateAgainst(command));
        AssertDiagnostic(
            () => InstallerMachineHelperResult.PostconditionFailed(
                command,
                command.ToDurableState(),
                "installer.machine.replay_verification_failed"),
            "installer.machine_helper.result_invalid");
        AssertDiagnostic(
            () => InstallerMachineHelperResult.PostconditionFailed(
                command,
                committed,
                "installer.machine_helper.completed"),
            "installer.machine_helper.result_invalid");
    }

    [Fact]
    public void ResultJournalHashAndCanonicalBase64AreIndependentlyBound()
    {
        InstallerMachineHelperCommand command = Command();
        InstallerMachineHelperResult success = InstallerMachineHelperResult.Succeeded(
            command,
            SuccessfulState(command));

        AssertDiagnostic(
            () => (success with
            {
                ResultJournalContentHash = InstallerTestData.OtherHash,
            }).ToResultDurableState(),
            "installer.transaction.content_hash_mismatch");
        AssertDiagnostic(
            () => (success with
            {
                ResultJournalBase64 = string.Concat(success.ResultJournalBase64, "\n"),
            }).ToResultDurableState(),
            "installer.machine_helper.result_journal_payload_invalid");
    }

    [Fact]
    public void MaximumBoundedJournalFitsBothCommandAndResultFrames()
    {
        string maximumSid = string.Concat(
            "S-1-281474976710655-",
            string.Join("-", Enumerable.Repeat("4294967295", 15)));
        var request = new InstallerRequest(
            InstallerOperation.Install,
            maximumSid,
            AllowReassociation: false,
            "65535.65535.65535.65535",
            InstallerTestData.Hash);
        InstallerTransactionSnapshot prepared = InstallerTransactionSnapshot.Create(
            InstallerTransactionJournal.Create(request));
        InstallerMachineHelperInvocation invocation = InstallerMachineHelperInvocation.Create(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        InstallerMachineHelperCommand command = InstallerMachineHelperCommand.Create(
            invocation,
            prepared);
        InstallerTransactionSnapshot reserved = InstallerTransactionSnapshot.Create(
            prepared.Journal.TransitionTo(InstallerTransactionPhase.MachineReserved));
        InstallerMachineHelperResult result = InstallerMachineHelperResult.Succeeded(
            command,
            reserved);

        Assert.InRange(
            InstallerTransactionCodec.Serialize(prepared.Journal).Length,
            1,
            InstallerTransactionCodec.MaximumDocumentBytes);
        Assert.InRange(
            InstallerMachineHelperCommandCodec.Serialize(command).Length,
            1,
            InstallerMachineHelperCommandCodec.MaximumCommandBytes);
        Assert.InRange(
            InstallerMachineHelperResultCodec.Serialize(result).Length,
            1,
            InstallerMachineHelperResultCodec.MaximumResultBytes);
    }

    [Fact]
    public void EmptyAndOversizedResponsesAreRejectedBeforeParsing()
    {
        AssertDiagnostic(
            () => InstallerMachineHelperResultCodec.Parse([]),
            "installer.machine_helper.result_size_invalid");
        AssertDiagnostic(
            () => InstallerMachineHelperResultCodec.Parse(
                new byte[InstallerMachineHelperResultCodec.MaximumResultBytes + 1]),
            "installer.machine_helper.result_size_invalid");
    }

    private static InstallerMachineHelperCommand Command(
        InstallerMachineHelperVerb verb = InstallerMachineHelperVerb.Apply,
        InstallerOperation? requestedOperation = null,
        string? transactionId = null)
    {
        InstallerOperation operation = requestedOperation
            ?? (verb == InstallerMachineHelperVerb.Remove
                ? InstallerOperation.Uninstall
                : InstallerOperation.Install);
        InstallerTransactionJournal journal = InstallerTestData.Journal(operation);
        if (transactionId is not null)
        {
            journal = journal with { TransactionId = transactionId };
        }
        InstallerTransactionPhase[] transitions = verb switch
        {
            InstallerMachineHelperVerb.Prepare => [],
            InstallerMachineHelperVerb.CommitPackage =>
            [
                InstallerTransactionPhase.MachineReserved,
            ],
            InstallerMachineHelperVerb.Remove =>
            [
                InstallerTransactionPhase.MachineRemovalAuthorized,
            ],
            InstallerMachineHelperVerb.Apply =>
            [
                InstallerTransactionPhase.MachineReserved,
                InstallerTransactionPhase.PackageCommitted,
            ],
            InstallerMachineHelperVerb.Verify =>
            [
                InstallerTransactionPhase.MachineReserved,
                InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.MachineCommitted,
            ],
            InstallerMachineHelperVerb.Clear =>
            [
                InstallerTransactionPhase.MachineReserved,
                InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.MachineCommitted,
                InstallerTransactionPhase.Verified,
            ],
            _ => throw new InvalidOperationException(),
        };
        foreach (InstallerTransactionPhase phase in transitions)
        {
            journal = journal.TransitionTo(phase);
        }

        InstallerTransactionSnapshot state = InstallerTransactionSnapshot.Create(journal);
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(verb, state);
        return InstallerMachineHelperCommand.Create(invocation, state);
    }

    private static InstallerTransactionSnapshot SuccessfulState(
        InstallerMachineHelperCommand command)
    {
        InstallerTransactionSnapshot requestState = command.ToDurableState();
        InstallerTransactionPhase next = command.Verb switch
        {
            InstallerMachineHelperVerb.Prepare =>
                requestState.Journal.Operation == InstallerOperation.Uninstall
                    ? InstallerTransactionPhase.MachineRemovalAuthorized
                    : InstallerTransactionPhase.MachineReserved,
            InstallerMachineHelperVerb.CommitPackage =>
                InstallerTransactionPhase.PackageCommitted,
            InstallerMachineHelperVerb.Apply or InstallerMachineHelperVerb.Remove =>
                InstallerTransactionPhase.MachineCommitted,
            InstallerMachineHelperVerb.Verify => InstallerTransactionPhase.Verified,
            InstallerMachineHelperVerb.Clear => InstallerTransactionPhase.Verified,
            _ => throw new InvalidOperationException(),
        };
        return requestState.Journal.Phase == next
            ? requestState
            : InstallerTransactionSnapshot.Create(
                requestState.Journal.TransitionTo(next));
    }

    private static string CanonicalJson()
    {
        InstallerMachineHelperCommand command = Command();
        return Encoding.UTF8.GetString(
            InstallerMachineHelperResultCodec.Serialize(
                InstallerMachineHelperResult.Succeeded(
                    command,
                    SuccessfulState(command))));
    }

    private static void AssertJsonInvalid(string json) => AssertDiagnostic(
        () => InstallerMachineHelperResultCodec.Parse(Encoding.UTF8.GetBytes(json)),
        "installer.machine_helper.result_json_invalid");

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}
