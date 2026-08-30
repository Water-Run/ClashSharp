using System.Text;
using System.Text.Json.Nodes;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperCommandTests
{
    [Theory]
    [InlineData(InstallerMachineHelperVerb.Prepare)]
    [InlineData(InstallerMachineHelperVerb.CommitPackage)]
    [InlineData(InstallerMachineHelperVerb.Apply)]
    [InlineData(InstallerMachineHelperVerb.Remove)]
    [InlineData(InstallerMachineHelperVerb.Verify)]
    public void EveryVerbRoundTripsAsAnExactJournalBoundCommand(
        InstallerMachineHelperVerb verb)
    {
        InstallerTransactionSnapshot durableState = DurableState(verb);
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(verb, durableState);
        InstallerMachineHelperCommand expected =
            InstallerMachineHelperCommand.Create(invocation, durableState);

        byte[] bytes = InstallerMachineHelperCommandCodec.Serialize(expected);
        InstallerMachineHelperCommand actual =
            InstallerMachineHelperCommandCodec.Parse(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(invocation, actual.ToInvocation());
        Assert.Equal(durableState, actual.ToDurableState());
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
            () => InstallerMachineHelperCommandCodec.Parse(Encoding.UTF8.GetBytes(json)),
            "installer.machine_helper.command_json_invalid");

    [Fact]
    public void UnknownDuplicateCaseChangedAndWrongTypePropertiesAreRejected()
    {
        string canonical = CanonicalJson();
        AssertJsonInvalid(canonical.Replace(
            "\"verb\":",
            "\"unexpected\":true,\"verb\":",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace(
            "\"transactionId\":",
            $"\"transactionId\":\"{InstallerTestData.TransactionId}\",\"transactionId\":",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace("\"verb\"", "\"Verb\"", StringComparison.Ordinal));

        JsonObject schema = JsonNode.Parse(canonical)!.AsObject();
        schema["schema"] = "1";
        AssertJsonInvalid(schema.ToJsonString());

        JsonObject verb = JsonNode.Parse(canonical)!.AsObject();
        verb["verb"] = 0;
        AssertJsonInvalid(verb.ToJsonString());

        JsonObject unknownVerb = JsonNode.Parse(canonical)!.AsObject();
        unknownVerb["verb"] = "unknown";
        AssertJsonInvalid(unknownVerb.ToJsonString());

        JsonObject journal = JsonNode.Parse(canonical)!.AsObject();
        journal["journalBase64"] = false;
        AssertJsonInvalid(journal.ToJsonString());
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
    public void InvalidSchemaAndInvocationFieldsFailClosed()
    {
        InstallerTransactionSnapshot durableState = DurableState();
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Apply,
                durableState);
        InstallerMachineHelperCommand valid =
            InstallerMachineHelperCommand.Create(invocation, durableState);
        AssertDiagnostic(
            () => (valid with { Schema = 2 }).Validate(),
            "installer.machine_helper.command_invalid");
        AssertDiagnostic(
            () => (valid with
            {
                TransactionId = valid.TransactionId.ToUpperInvariant(),
            }).Validate(),
            "installer.machine_helper.transaction_id_invalid");
        AssertDiagnostic(
            () => (valid with
            {
                JournalContentHash = valid.JournalContentHash.ToUpperInvariant(),
            }).Validate(),
            "installer.machine_helper.journal_hash_invalid");
    }

    [Fact]
    public void JournalBytesMustBeCanonicalAndMatchBothInvocationFields()
    {
        InstallerTransactionSnapshot durableState = DurableState();
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Apply,
                durableState);
        InstallerMachineHelperCommand valid =
            InstallerMachineHelperCommand.Create(invocation, durableState);

        AssertDiagnostic(
            () => (valid with { JournalBase64 = "not base64" }).Validate(),
            "installer.machine_helper.journal_payload_invalid");
        AssertDiagnostic(
            () => (valid with { JournalBase64 = $" {valid.JournalBase64}" }).Validate(),
            "installer.machine_helper.journal_payload_invalid");

        byte[] journalBytes = Convert.FromBase64String(valid.JournalBase64);
        journalBytes[^1] ^= 1;
        Assert.Throws<InstallerProtocolException>(() =>
            (valid with { JournalBase64 = Convert.ToBase64String(journalBytes) }).Validate());

        AssertDiagnostic(
            () => (valid with
            {
                JournalContentHash = InstallerTestData.OtherHash,
            }).Validate(),
            "installer.transaction.content_hash_mismatch");
    }

    [Fact]
    public void EmptyAndOversizedCommandsAreRejectedBeforeParsing()
    {
        AssertDiagnostic(
            () => InstallerMachineHelperCommandCodec.Parse([]),
            "installer.machine_helper.command_size_invalid");
        AssertDiagnostic(
            () => InstallerMachineHelperCommandCodec.Parse(
                new byte[InstallerMachineHelperCommandCodec.MaximumCommandBytes + 1]),
            "installer.machine_helper.command_size_invalid");
    }

    private static InstallerTransactionSnapshot DurableState(
        InstallerMachineHelperVerb verb = InstallerMachineHelperVerb.Apply)
    {
        InstallerOperation operation = verb == InstallerMachineHelperVerb.Remove
            ? InstallerOperation.Uninstall
            : InstallerOperation.Install;
        InstallerTransactionJournal journal = InstallerTestData.Journal(operation);
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
            _ => throw new InvalidOperationException(),
        };
        foreach (InstallerTransactionPhase phase in transitions)
        {
            journal = journal.TransitionTo(phase);
        }

        return InstallerTransactionSnapshot.Create(journal);
    }

    private static string CanonicalJson() => Encoding.UTF8.GetString(
        InstallerMachineHelperCommandCodec.Serialize(
            CreateCommand()));

    private static InstallerMachineHelperCommand CreateCommand()
    {
        InstallerTransactionSnapshot durableState = DurableState();
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(
                InstallerMachineHelperVerb.Apply,
                durableState);
        return InstallerMachineHelperCommand.Create(invocation, durableState);
    }

    private static void AssertJsonInvalid(string json) => AssertDiagnostic(
        () => InstallerMachineHelperCommandCodec.Parse(Encoding.UTF8.GetBytes(json)),
        "installer.machine_helper.command_json_invalid");

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}
