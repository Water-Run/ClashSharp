using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerTransactionJournalTests
{
    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public void InstallAndRepairReserveMachineBeforePackageThenCommitMachine(
        InstallerOperation operation)
    {
        InstallerTransactionJournal prepared = InstallerTestData.Journal(operation);
        InstallerTransactionJournal reserved = prepared.TransitionTo(
            InstallerTransactionPhase.MachineReserved);
        InstallerTransactionJournal package = reserved.TransitionTo(
            InstallerTransactionPhase.PackageCommitted);
        InstallerTransactionJournal machine = package.TransitionTo(InstallerTransactionPhase.MachineCommitted);
        InstallerTransactionJournal verified = machine.TransitionTo(InstallerTransactionPhase.Verified);

        Assert.Equal(2, reserved.Generation);
        Assert.Equal(3, package.Generation);
        Assert.Equal(4, machine.Generation);
        Assert.Equal(5, verified.Generation);
        Assert.Same(verified, verified.TransitionTo(InstallerTransactionPhase.Verified));
    }

    [Fact]
    public void UninstallAuthorizesRemovalBeforeMachineThenPackageOrder()
    {
        InstallerTransactionJournal prepared = InstallerTestData.Journal(InstallerOperation.Uninstall);
        InstallerTransactionJournal authorized = prepared.TransitionTo(
            InstallerTransactionPhase.MachineRemovalAuthorized);
        InstallerTransactionJournal machine = authorized.TransitionTo(
            InstallerTransactionPhase.MachineCommitted);
        InstallerTransactionJournal package = machine.TransitionTo(InstallerTransactionPhase.PackageCommitted);
        InstallerTransactionJournal verified = package.TransitionTo(InstallerTransactionPhase.Verified);

        Assert.Equal(2, authorized.Generation);
        Assert.Equal(3, machine.Generation);
        Assert.Equal(4, package.Generation);
        Assert.Equal(5, verified.Generation);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Verified)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.Verified)]
    public void PhaseSkipsAreRejected(
        InstallerOperation operation,
        InstallerTransactionPhase next)
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal(operation);
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            () => journal.TransitionTo(next));
        Assert.Equal("installer.transaction.phase_transition_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void EveryOperationPhasePairAllowsOnlyReplayOrItsSingleNextCommit()
    {
        InstallerTransactionPhase[] allPhases = Enum.GetValues<InstallerTransactionPhase>();
        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
        {
            InstallerTransactionPhase[] order = OrderFor(operation);
            for (int currentIndex = 0; currentIndex < order.Length; currentIndex++)
            {
                InstallerTransactionJournal current = JournalAt(operation, order[currentIndex]);
                foreach (InstallerTransactionPhase next in allPhases)
                {
                    bool allowed = next == current.Phase
                        || currentIndex + 1 < order.Length && next == order[currentIndex + 1];
                    if (allowed)
                    {
                        InstallerTransactionJournal advanced = current.TransitionTo(next);
                        Assert.Equal(next, advanced.Phase);
                        Assert.Equal(
                            current.Generation + (next == current.Phase ? 0 : 1),
                            advanced.Generation);
                    }
                    else
                    {
                        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
                            () => current.TransitionTo(next));
                        Assert.Equal(
                            "installer.transaction.phase_transition_invalid",
                            exception.DiagnosticCode);
                    }
                }
            }
        }
    }

    [Fact]
    public void ResumeIdentityRequiresEveryImmutableField()
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal();

        Assert.True(journal.Matches(InstallerTestData.Request()));
        Assert.False(journal.Matches(InstallerTestData.Request(hash: InstallerTestData.OtherHash)));
        Assert.False(journal.Matches(InstallerTestData.Request(version: "1.2.3.5")));
        Assert.False(journal.Matches(InstallerTestData.Request(InstallerOperation.Uninstall)));
        Assert.False(journal.Matches(new InstallerRequest(
            InstallerOperation.Install,
            "S-1-5-21-100-200-300-1002",
            AllowReassociation: false,
            InstallerTestData.Version,
            InstallerTestData.Hash)));

        InstallerTransactionJournal repair = InstallerTestData.Journal(InstallerOperation.Repair);
        Assert.False(repair.Matches(InstallerTestData.Request(InstallerOperation.Repair)));
    }

    [Fact]
    public void PhaseAndGenerationMustDescribeTheSameCommit()
    {
        InstallerTransactionJournal invalid = InstallerTestData.Journal(
            phase: InstallerTransactionPhase.PackageCommitted,
            generation: 2);
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(invalid.Validate);
        Assert.Equal("installer.transaction.generation_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void CreateGeneratesCanonicalRandomTransactionIdentifiers()
    {
        InstallerTransactionJournal first = InstallerTransactionJournal.Create(InstallerTestData.Request());
        InstallerTransactionJournal second = InstallerTransactionJournal.Create(InstallerTestData.Request());

        InstallerProtocolValidation.ValidateLowerHex256(first.TransactionId, "invalid");
        Assert.NotEqual(first.TransactionId, second.TransactionId);
    }

    [Fact]
    public void DurableSnapshotRequiresAValidatedJournalAndCanonicalContentHash()
    {
        InstallerTransactionSnapshot valid = InstallerTransactionSnapshot.Create(
            InstallerTestData.Journal());
        valid.Validate();

        var missingJournal = new InstallerTransactionSnapshot(
            null!,
            InstallerTestData.Hash);
        InstallerProtocolException missing = Assert.Throws<InstallerProtocolException>(
            missingJournal.Validate);
        Assert.Equal("installer.transaction.snapshot_invalid", missing.DiagnosticCode);

        var invalidHash = valid with
        {
            ContentHash = InstallerTestData.Hash.ToUpperInvariant(),
        };
        InstallerProtocolException hash = Assert.Throws<InstallerProtocolException>(
            invalidHash.Validate);
        Assert.Equal("installer.transaction.content_hash_invalid", hash.DiagnosticCode);

        InstallerProtocolException mismatch = Assert.Throws<InstallerProtocolException>(
            (valid with { ContentHash = InstallerTestData.OtherHash }).Validate);
        Assert.Equal("installer.transaction.content_hash_mismatch", mismatch.DiagnosticCode);
    }

    private static InstallerTransactionJournal JournalAt(
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        InstallerRequest request = InstallerTestData.Request(
            operation,
            allowReassociation: operation == InstallerOperation.Repair);
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        foreach (InstallerTransactionPhase next in OrderFor(operation).Skip(1))
        {
            if (journal.Phase == phase)
            {
                return journal;
            }

            journal = journal.TransitionTo(next);
        }

        return journal;
    }

    private static InstallerTransactionPhase[] OrderFor(InstallerOperation operation) =>
        operation == InstallerOperation.Uninstall
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
}
