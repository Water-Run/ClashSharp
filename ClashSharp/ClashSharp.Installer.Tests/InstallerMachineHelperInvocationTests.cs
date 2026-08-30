using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperInvocationTests
{
    [Theory]
    [InlineData(
        InstallerMachineHelperVerb.Prepare,
        InstallerOperation.Install,
        InstallerTransactionPhase.Prepared)]
    [InlineData(
        InstallerMachineHelperVerb.Prepare,
        InstallerOperation.Repair,
        InstallerTransactionPhase.Prepared)]
    [InlineData(
        InstallerMachineHelperVerb.Prepare,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.Prepared)]
    [InlineData(
        InstallerMachineHelperVerb.Apply,
        InstallerOperation.Install,
        InstallerTransactionPhase.PackageCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.Apply,
        InstallerOperation.Repair,
        InstallerTransactionPhase.PackageCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.CommitPackage,
        InstallerOperation.Install,
        InstallerTransactionPhase.MachineReserved)]
    [InlineData(
        InstallerMachineHelperVerb.CommitPackage,
        InstallerOperation.Repair,
        InstallerTransactionPhase.MachineReserved)]
    [InlineData(
        InstallerMachineHelperVerb.CommitPackage,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.MachineCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.Remove,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.MachineRemovalAuthorized)]
    [InlineData(
        InstallerMachineHelperVerb.Verify,
        InstallerOperation.Install,
        InstallerTransactionPhase.MachineCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.Verify,
        InstallerOperation.Repair,
        InstallerTransactionPhase.Verified)]
    [InlineData(
        InstallerMachineHelperVerb.Verify,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.PackageCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.Verify,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.Verified)]
    public void ExactInvocationRoundTripsAndMatchesOnlyItsDurableState(
        InstallerMachineHelperVerb verb,
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionSnapshot state = Snapshot(operation, phase);
        InstallerMachineHelperInvocation expected =
            InstallerMachineHelperInvocation.Create(verb, state);

        InstallerMachineHelperInvocation? actual =
            InstallerMachineHelperInvocation.Parse(expected.ToArguments());

        Assert.Equal(expected, actual);
        actual!.ValidateAgainst(state);
        Assert.Equal(6, actual.ToArguments().Count);
        Assert.DoesNotContain(actual.ToArguments(), static argument =>
            argument.Contains('/') || argument.Contains('\\'));
    }

    [Fact]
    public void OrdinaryUiArgumentsDoNotEnterTheHelperBoundary()
    {
        Assert.Null(InstallerMachineHelperInvocation.Parse([]));
        Assert.Null(InstallerMachineHelperInvocation.Parse(["--help"]));
        Assert.Null(InstallerMachineHelperInvocation.Parse(["settings"]));
    }

    [Fact]
    public void MachinePrefixedUnknownMalformedReorderedAndPathArgumentsAreRejected()
    {
        InstallerMachineHelperInvocation valid = InstallerMachineHelperInvocation.Create(
            InstallerMachineHelperVerb.Apply,
            Snapshot(InstallerOperation.Install, InstallerTransactionPhase.PackageCommitted));
        IReadOnlyList<string> arguments = valid.ToArguments();
        string[][] invalid =
        [
            ["--machine-commit"],
            ["--help", "--machine-helper"],
            ["--machine-helper"],
            ["--machine-helper", "unknown", .. arguments.Skip(2)],
            [arguments[0], arguments[1], arguments[4], arguments[5], arguments[2], arguments[3]],
            [.. arguments, @"C:\arbitrary\payload.msix"],
        ];

        foreach (string[] candidate in invalid)
        {
            AssertDiagnostic(
                () => InstallerMachineHelperInvocation.Parse(candidate),
                "installer.machine_helper.arguments_invalid");
        }

        AssertDiagnostic(
            () => InstallerMachineHelperInvocation.Parse([null!]),
            "installer.machine_helper.arguments_invalid");
        AssertDiagnostic(
            () => InstallerMachineHelperInvocation.Parse(["--help", null!]),
            "installer.machine_helper.arguments_invalid");
    }

    [Fact]
    public void TransactionAndJournalHashesAreCanonicalAndExact()
    {
        InstallerTransactionSnapshot state = Snapshot(
            InstallerOperation.Install,
            InstallerTransactionPhase.PackageCommitted);
        InstallerMachineHelperInvocation valid = InstallerMachineHelperInvocation.Create(
            InstallerMachineHelperVerb.Apply,
            state);

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

        InstallerTransactionSnapshot differentHash = state with
        {
            ContentHash = InstallerTestData.OtherHash,
        };
        AssertDiagnostic(
            () => valid.ValidateAgainst(differentHash),
            "installer.transaction.content_hash_mismatch");
    }

    [Theory]
    [InlineData(
        InstallerMachineHelperVerb.Prepare,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.MachineRemovalAuthorized)]
    [InlineData(
        InstallerMachineHelperVerb.Prepare,
        InstallerOperation.Install,
        InstallerTransactionPhase.MachineReserved)]
    [InlineData(
        InstallerMachineHelperVerb.Apply,
        InstallerOperation.Install,
        InstallerTransactionPhase.Prepared)]
    [InlineData(
        InstallerMachineHelperVerb.Apply,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.Prepared)]
    [InlineData(
        InstallerMachineHelperVerb.CommitPackage,
        InstallerOperation.Install,
        InstallerTransactionPhase.Prepared)]
    [InlineData(
        InstallerMachineHelperVerb.CommitPackage,
        InstallerOperation.Install,
        InstallerTransactionPhase.PackageCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.CommitPackage,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.MachineRemovalAuthorized)]
    [InlineData(
        InstallerMachineHelperVerb.Remove,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.Prepared)]
    [InlineData(
        InstallerMachineHelperVerb.Remove,
        InstallerOperation.Install,
        InstallerTransactionPhase.PackageCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.Verify,
        InstallerOperation.Install,
        InstallerTransactionPhase.PackageCommitted)]
    [InlineData(
        InstallerMachineHelperVerb.Verify,
        InstallerOperation.Uninstall,
        InstallerTransactionPhase.MachineCommitted)]
    public void VerbCannotCrossItsOperationSpecificDurablePhase(
        InstallerMachineHelperVerb verb,
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionSnapshot state = Snapshot(operation, phase);
        var invocation = new InstallerMachineHelperInvocation(
            verb,
            state.Journal.TransactionId,
            state.ContentHash);

        AssertDiagnostic(
            () => invocation.ValidateAgainst(state),
            "installer.machine_helper.phase_invalid");
    }

    [Fact]
    public void PipeSessionIsStableAcrossPhasesButUniqueToTheRandomTransaction()
    {
        InstallerTransactionSnapshot state = Snapshot(
            InstallerOperation.Install,
            InstallerTransactionPhase.PackageCommitted);
        InstallerMachineHelperInvocation apply = InstallerMachineHelperInvocation.Create(
            InstallerMachineHelperVerb.Apply,
            state);
        string first = apply.BuildSessionPipeName();

        Assert.Equal(first, apply.BuildSessionPipeName());
        Assert.Matches("^ClashSharp\\.Installer\\.Elevation\\.[0-9a-f]{32}$", first);
        Assert.Equal(
            first,
            (apply with { Verb = InstallerMachineHelperVerb.Verify }).BuildSessionPipeName());
        Assert.Equal(
            first,
            (apply with
            {
                JournalContentHash = InstallerTestData.OtherHash,
            }).BuildSessionPipeName());
        Assert.NotEqual(
            first,
            (apply with { TransactionId = InstallerTestData.OtherHash }).BuildSessionPipeName());
    }

    private static InstallerTransactionSnapshot Snapshot(
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal(operation);
        InstallerTransactionPhase[] order = operation == InstallerOperation.Uninstall
            ?
            [
                InstallerTransactionPhase.Prepared,
                InstallerTransactionPhase.MachineRemovalAuthorized,
                InstallerTransactionPhase.MachineCommitted,
                InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.Verified,
            ]
            :
            [
                InstallerTransactionPhase.Prepared,
                InstallerTransactionPhase.MachineReserved,
                InstallerTransactionPhase.PackageCommitted,
                InstallerTransactionPhase.MachineCommitted,
                InstallerTransactionPhase.Verified,
            ];
        foreach (InstallerTransactionPhase next in order.Skip(1))
        {
            if (journal.Phase == phase)
            {
                break;
            }

            journal = journal.TransitionTo(next);
        }

        Assert.Equal(phase, journal.Phase);
        return InstallerTransactionSnapshot.Create(journal);
    }

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}
