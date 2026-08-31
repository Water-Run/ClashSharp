using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperSessionGuardTests
{
    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public void InstallAndRepairUseOneMonotonicSessionIncludingCommittedReplay(
        InstallerOperation operation)
    {
        InstallerTransactionSnapshot prepared = State(Journal(operation));
        InstallerMachineHelperCommand prepare = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            prepare.ToInvocation(),
            protectedState: null);

        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(prepare, protectedState: null));
        InstallerTransactionSnapshot reserved = SuccessfulState(prepare);
        Assert.Equal(reserved, CompleteSuccessfully(guard, prepare, reserved));

        InstallerMachineHelperCommand commitPackage = Command(
            InstallerMachineHelperVerb.CommitPackage,
            reserved);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(commitPackage, reserved));
        InstallerTransactionSnapshot package = SuccessfulState(commitPackage);
        Assert.Equal(
            package,
            CompleteSuccessfully(guard, commitPackage, package));

        InstallerMachineHelperCommand apply = Command(
            InstallerMachineHelperVerb.Apply,
            package);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(apply, package));
        InstallerTransactionSnapshot machine = SuccessfulState(apply);
        Assert.Equal(machine, CompleteSuccessfully(guard, apply, machine));

        InstallerMachineHelperCommand verify = Command(
            InstallerMachineHelperVerb.Verify,
            machine);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(verify, machine));
        InstallerTransactionSnapshot verified = SuccessfulState(verify);
        Assert.Equal(verified, CompleteSuccessfully(guard, verify, verified));

        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            guard.Begin(verify, verified));
        Assert.Equal(verified, CompleteSuccessfully(guard, verify, verified));
    }

    [Fact]
    public void UninstallUsesExplicitAuthorizationRemoveAndVerifyStates()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Uninstall));
        InstallerMachineHelperCommand prepare = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            prepare.ToInvocation(),
            protectedState: null);

        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(prepare, protectedState: null));
        InstallerTransactionSnapshot authorized = SuccessfulState(prepare);
        Assert.Equal(
            InstallerTransactionPhase.MachineRemovalAuthorized,
            CompleteSuccessfully(guard, prepare, authorized).Journal.Phase);

        InstallerMachineHelperCommand remove = Command(
            InstallerMachineHelperVerb.Remove,
            authorized);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(remove, authorized));
        InstallerTransactionSnapshot machine = SuccessfulState(remove);
        Assert.Equal(machine, CompleteSuccessfully(guard, remove, machine));

        InstallerMachineHelperCommand commitPackage = Command(
            InstallerMachineHelperVerb.CommitPackage,
            machine);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(commitPackage, machine));
        InstallerTransactionSnapshot package = SuccessfulState(commitPackage);
        Assert.Equal(
            package,
            CompleteSuccessfully(guard, commitPackage, package));

        InstallerMachineHelperCommand verify = Command(
            InstallerMachineHelperVerb.Verify,
            package);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(verify, package));
        InstallerTransactionSnapshot verified = SuccessfulState(verify);
        Assert.Equal(verified, CompleteSuccessfully(guard, verify, verified));

        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            guard.Begin(verify, verified));
        Assert.Equal(verified, CompleteSuccessfully(guard, verify, verified));
    }

    [Fact]
    public void VerifiedClearCompletesOnlyAgainstAbsentProtectedStateAndSupportsAckLossReplay()
    {
        InstallerTransactionSnapshot verified = State(AdvanceTo(
            Journal(InstallerOperation.Install),
            InstallerTransactionPhase.Verified));
        InstallerMachineHelperCommand clear = Command(
            InstallerMachineHelperVerb.Clear,
            verified);
        InstallerMachineHelperResult receipt = InstallerMachineHelperResult.Succeeded(
            clear,
            verified);
        var guard = new InstallerMachineHelperSessionGuard(
            clear.ToInvocation(),
            verified);

        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(clear, verified));
        Assert.Equal(verified, guard.Complete(receipt, protectedState: null));

        var replay = new InstallerMachineHelperSessionGuard(
            clear.ToInvocation(),
            protectedState: null);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            replay.Begin(clear, protectedState: null));
        Assert.Equal(verified, replay.Complete(receipt, protectedState: null));

        var uncleared = new InstallerMachineHelperSessionGuard(
            clear.ToInvocation(),
            verified);
        _ = uncleared.Begin(clear, verified);
        AssertDiagnostic(
            () => uncleared.Complete(receipt, verified),
            "installer.machine_helper.session_protected_state_mismatch");
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.PackageCommitted,
        InstallerMachineHelperVerb.Apply, InstallerMachineHelperSessionDisposition.Execute)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineReserved,
        InstallerMachineHelperVerb.CommitPackage,
        InstallerMachineHelperSessionDisposition.Execute)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.MachineCommitted,
        InstallerMachineHelperVerb.Verify, InstallerMachineHelperSessionDisposition.Execute)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted,
        InstallerMachineHelperVerb.CommitPackage,
        InstallerMachineHelperSessionDisposition.Execute)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted,
        InstallerMachineHelperVerb.Verify, InstallerMachineHelperSessionDisposition.Execute)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Verified,
        InstallerMachineHelperVerb.Verify,
        InstallerMachineHelperSessionDisposition.VerifyCommittedReplay)]
    public void RecoveryMayBootstrapOnlyAtItsExactProtectedPhase(
        InstallerOperation operation,
        InstallerTransactionPhase phase,
        InstallerMachineHelperVerb verb,
        InstallerMachineHelperSessionDisposition expectedDisposition)
    {
        InstallerTransactionSnapshot protectedState = State(
            AdvanceTo(Journal(operation), phase));
        InstallerMachineHelperCommand command = Command(verb, protectedState);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState);

        Assert.Equal(
            expectedDisposition,
            guard.Begin(command, protectedState));
    }

    [Fact]
    public void FirstCommandMustMatchTheRunAsBootstrapExactly()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand bootstrap = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        InstallerTransactionSnapshot package = State(
            AdvanceTo(
                prepared.Journal,
                InstallerTransactionPhase.PackageCommitted));
        InstallerMachineHelperCommand different = Command(
            InstallerMachineHelperVerb.Apply,
            package);
        var guard = new InstallerMachineHelperSessionGuard(
            bootstrap.ToInvocation(),
            protectedState: null);

        AssertDiagnostic(
            () => guard.Begin(different, protectedState: null),
            "installer.machine_helper.session_bootstrap_mismatch");
    }

    [Fact]
    public void CommandDoesNotAdvanceStateUntilResultAndProtectedReloadAgree()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState: null);

        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(command, protectedState: null));
        AssertDiagnostic(
            () => guard.Begin(command, protectedState: null),
            "installer.machine_helper.session_command_pending");

        InstallerTransactionSnapshot reserved = SuccessfulState(command);
        Assert.Equal(reserved, CompleteSuccessfully(guard, command, reserved));
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            guard.Begin(command, reserved));
    }

    [Fact]
    public void StableFailureLeavesProtectedStateRetryable()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState: null);
        _ = guard.Begin(command, protectedState: null);

        InstallerMachineHelperResult failure = InstallerMachineHelperResult.Failed(
            command,
            "installer.machine.prepare_failed");
        Assert.Equal(prepared, guard.Complete(failure, prepared));
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(command, prepared));
    }

    [Fact]
    public void LostResultCanReconcileAndReplayOnlyTheCommittedPostcondition()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState: null);
        _ = guard.Begin(command, protectedState: null);
        InstallerTransactionSnapshot reserved = SuccessfulState(command);

        Assert.Equal(reserved, guard.ReconcileAfterAbort(reserved));
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            guard.Begin(command, reserved));
        Assert.Equal(reserved, CompleteSuccessfully(guard, command, reserved));
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineReserved,
        InstallerMachineHelperVerb.CommitPackage)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.MachineReserved,
        InstallerMachineHelperVerb.CommitPackage)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.PackageCommitted,
        InstallerMachineHelperVerb.Apply)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.PackageCommitted,
        InstallerMachineHelperVerb.Apply)]
    [InlineData(InstallerOperation.Uninstall,
        InstallerTransactionPhase.MachineRemovalAuthorized,
        InstallerMachineHelperVerb.Remove)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted,
        InstallerMachineHelperVerb.CommitPackage)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineCommitted,
        InstallerMachineHelperVerb.Verify)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted,
        InstallerMachineHelperVerb.Verify)]
    public void EveryCommittedBoundarySupportsAckLossReconciliation(
        InstallerOperation operation,
        InstallerTransactionPhase phase,
        InstallerMachineHelperVerb verb)
    {
        InstallerTransactionSnapshot protectedState = State(
            AdvanceTo(Journal(operation), phase));
        InstallerMachineHelperCommand command = Command(verb, protectedState);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            guard.Begin(command, protectedState));
        InstallerTransactionSnapshot committed = SuccessfulState(command);

        Assert.Equal(committed, guard.ReconcileAfterAbort(committed));
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            guard.Begin(command, committed));
        Assert.Equal(committed, CompleteSuccessfully(guard, command, committed));
    }

    [Fact]
    public void AbortReconciliationRejectsUnrelatedOrSkippedProtectedState()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState: null);
        _ = guard.Begin(command, protectedState: null);
        InstallerTransactionSnapshot skipped = State(
            prepared.Journal
                .TransitionTo(InstallerTransactionPhase.MachineReserved)
                .TransitionTo(InstallerTransactionPhase.PackageCommitted));

        AssertDiagnostic(
            () => guard.ReconcileAfterAbort(skipped),
            "installer.machine_helper.session_abort_state_invalid");
    }

    [Fact]
    public void SuccessRequiresExactProtectedResultState()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState: null);
        _ = guard.Begin(command, protectedState: null);
        InstallerMachineHelperResult success = InstallerMachineHelperResult.Succeeded(
            command,
            SuccessfulState(command));

        AssertDiagnostic(
            () => guard.Complete(success, protectedState: null),
            "installer.machine_helper.session_protected_state_mismatch");
    }

    [Fact]
    public void CommittedReplayCanReportDriftWithoutRegressingProtectedState()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        InstallerTransactionSnapshot reserved = SuccessfulState(command);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            reserved);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            guard.Begin(command, reserved));

        InstallerMachineHelperResult failure =
            InstallerMachineHelperResult.PostconditionFailed(
                command,
                reserved,
                "installer.machine.replay_verification_failed");
        Assert.Equal(reserved, guard.Complete(failure, reserved));
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            guard.Begin(command, reserved));
    }

    [Fact]
    public void FirstExecutionCannotUseReplayFailureToClaimACommit()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            command.ToInvocation(),
            protectedState: null);
        _ = guard.Begin(command, protectedState: null);
        InstallerTransactionSnapshot reserved = SuccessfulState(command);
        InstallerMachineHelperResult failure =
            InstallerMachineHelperResult.PostconditionFailed(
                command,
                reserved,
                "installer.machine.replay_verification_failed");

        AssertDiagnostic(
            () => guard.Complete(failure, reserved),
            "installer.machine_helper.session_protected_state_mismatch");
    }

    [Fact]
    public void RemovalAuthorizationCannotBeInventedByRecoveryCommand()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Uninstall));
        InstallerTransactionSnapshot fabricatedAuthorization = State(
            prepared.Journal.TransitionTo(
                InstallerTransactionPhase.MachineRemovalAuthorized));
        InstallerMachineHelperCommand remove = Command(
            InstallerMachineHelperVerb.Remove,
            fabricatedAuthorization);
        var guard = new InstallerMachineHelperSessionGuard(
            remove.ToInvocation(),
            prepared);

        AssertDiagnostic(
            () => guard.Begin(remove, prepared),
            "installer.machine_helper.session_protected_state_mismatch");

        var authorizedGuard = new InstallerMachineHelperSessionGuard(
            remove.ToInvocation(),
            fabricatedAuthorization);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            authorizedGuard.Begin(remove, fabricatedAuthorization));
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public void PackageCommitCannotBeInventedByAParentCommand(
        InstallerOperation operation)
    {
        InstallerTransactionPhase protectedPhase = operation == InstallerOperation.Uninstall
            ? InstallerTransactionPhase.MachineCommitted
            : InstallerTransactionPhase.MachineReserved;
        InstallerTransactionSnapshot protectedState = State(
            AdvanceTo(Journal(operation), protectedPhase));
        InstallerTransactionSnapshot fabricatedPackage = State(
            protectedState.Journal.TransitionTo(
                InstallerTransactionPhase.PackageCommitted));
        InstallerMachineHelperVerb nextVerb = operation == InstallerOperation.Uninstall
            ? InstallerMachineHelperVerb.Verify
            : InstallerMachineHelperVerb.Apply;
        InstallerMachineHelperCommand next = Command(nextVerb, fabricatedPackage);
        var guard = new InstallerMachineHelperSessionGuard(
            next.ToInvocation(),
            protectedState);

        AssertDiagnostic(
            () => guard.Begin(next, protectedState),
            "installer.machine_helper.session_protected_state_mismatch");

        InstallerMachineHelperCommand commit = Command(
            InstallerMachineHelperVerb.CommitPackage,
            protectedState);
        var commitGuard = new InstallerMachineHelperSessionGuard(
            commit.ToInvocation(),
            protectedState);
        Assert.Equal(
            InstallerMachineHelperSessionDisposition.Execute,
            commitGuard.Begin(commit, protectedState));
    }

    [Fact]
    public void ProtectedStateCannotChangeBetweenCompletedCommands()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand prepare = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            prepare.ToInvocation(),
            protectedState: null);
        _ = guard.Begin(prepare, protectedState: null);
        InstallerTransactionSnapshot reserved = SuccessfulState(prepare);
        _ = CompleteSuccessfully(guard, prepare, reserved);

        InstallerMachineHelperCommand commitPackage = Command(
            InstallerMachineHelperVerb.CommitPackage,
            reserved);
        InstallerTransactionSnapshot fabricatedPackage =
            SuccessfulState(commitPackage);

        AssertDiagnostic(
            () => guard.Begin(commitPackage, fabricatedPackage),
            "installer.machine_helper.session_protected_state_changed");
        AssertDiagnostic(
            () => guard.Begin(commitPackage, protectedState: null),
            "installer.machine_helper.session_protected_state_missing");
    }

    [Fact]
    public void SessionRejectsTransactionSwitchIdentityChangeAndRegression()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand bootstrap = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var guard = new InstallerMachineHelperSessionGuard(
            bootstrap.ToInvocation(),
            protectedState: null);
        _ = guard.Begin(bootstrap, protectedState: null);
        InstallerTransactionSnapshot reserved = SuccessfulState(bootstrap);
        _ = CompleteSuccessfully(guard, bootstrap, reserved);

        InstallerTransactionSnapshot otherTransaction = State(
            AdvanceTo(
                prepared.Journal with { TransactionId = InstallerTestData.OtherHash },
                InstallerTransactionPhase.PackageCommitted));
        AssertDiagnostic(
            () => guard.Begin(
                Command(InstallerMachineHelperVerb.Apply, otherTransaction),
                reserved),
            "installer.machine_helper.session_transaction_mismatch");

        InstallerTransactionSnapshot changedRelease = State(
            AdvanceTo(
                prepared.Journal with { ExpectedPackageVersion = "1.2.3.5" },
                InstallerTransactionPhase.PackageCommitted));
        AssertDiagnostic(
            () => guard.Begin(
                Command(InstallerMachineHelperVerb.Apply, changedRelease),
                reserved),
            "installer.machine_helper.session_identity_mismatch");

        InstallerMachineHelperCommand commitPackage = Command(
            InstallerMachineHelperVerb.CommitPackage,
            reserved);
        _ = guard.Begin(commitPackage, reserved);
        InstallerTransactionSnapshot package = SuccessfulState(commitPackage);
        _ = CompleteSuccessfully(guard, commitPackage, package);
        InstallerMachineHelperCommand apply = Command(
            InstallerMachineHelperVerb.Apply,
            package);
        _ = guard.Begin(apply, package);
        InstallerTransactionSnapshot machine = SuccessfulState(apply);
        _ = CompleteSuccessfully(guard, apply, machine);

        AssertDiagnostic(
            () => guard.Begin(apply, package),
            "installer.machine_helper.session_journal_regressed");
    }

    [Fact]
    public void ConstructorRejectsProtectedStateFromAnotherTransaction()
    {
        InstallerTransactionSnapshot prepared = State(
            Journal(InstallerOperation.Install));
        InstallerMachineHelperCommand bootstrap = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        InstallerTransactionSnapshot other = State(
            prepared.Journal with { TransactionId = InstallerTestData.OtherHash });

        AssertDiagnostic(
            () =>
            {
                _ = new InstallerMachineHelperSessionGuard(
                    bootstrap.ToInvocation(),
                    other);
            },
            "installer.machine_helper.session_transaction_mismatch");
    }

    private static InstallerTransactionSnapshot CompleteSuccessfully(
        InstallerMachineHelperSessionGuard guard,
        InstallerMachineHelperCommand command,
        InstallerTransactionSnapshot protectedState)
    {
        InstallerMachineHelperResult result = InstallerMachineHelperResult.Succeeded(
            command,
            protectedState);
        return guard.Complete(result, protectedState);
    }

    private static InstallerMachineHelperCommand Command(
        InstallerMachineHelperVerb verb,
        InstallerTransactionSnapshot durableState)
    {
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(verb, durableState);
        return InstallerMachineHelperCommand.Create(invocation, durableState);
    }

    private static InstallerTransactionSnapshot State(
        InstallerTransactionJournal journal) =>
        InstallerTransactionSnapshot.Create(journal);

    private static InstallerTransactionSnapshot SuccessfulState(
        InstallerMachineHelperCommand command) =>
        command.GetExpectedSuccessfulState();

    private static InstallerTransactionJournal Journal(
        InstallerOperation operation) =>
        InstallerTestData.Journal(operation) with
        {
            AllowReassociation = operation == InstallerOperation.Repair,
        };

    private static InstallerTransactionJournal AdvanceTo(
        InstallerTransactionJournal journal,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionPhase[] order = journal.Operation == InstallerOperation.Uninstall
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
        return journal;
    }

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}
