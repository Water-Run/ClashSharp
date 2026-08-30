using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerCoordinatorTests
{
    [Fact]
    public async Task InstallReservesMachineBeforePackageThenCommitsAndVerifiesBeforeClear()
    {
        InstallerScenario scenario = new();
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.False(result.RecoveryPending);
        Assert.Equal(
            [
                "environment.inspect",
                "release.verify",
                "journal.load",
                "journal.save:Prepared",
                "release.reverify",
                "machine.prepare:Install",
                "journal.save:MachineReserved",
                "release.reverify",
                "certificate.apply:Install",
                "release.reverify",
                "package.apply:Install",
                "release.reverify",
                "machine.commit_package:Install",
                "journal.save:PackageCommitted",
                "release.reverify",
                "machine.apply:Install",
                "journal.save:MachineCommitted",
                "release.reverify",
                "final.verify",
                "journal.save:Verified",
                "release.reverify",
                "final.verify",
                "journal.clear",
                "release.dispose",
            ],
            scenario.Events);
        InstallerTransactionSnapshot preparationIntent =
            Assert.Single(scenario.MachinePreparationIntents);
        Assert.Equal(InstallerTransactionPhase.Prepared, preparationIntent.Journal.Phase);
        Assert.True(preparationIntent.Journal.Matches(InstallerTestData.Request()));
        InstallerTransactionSnapshot machineIntent = Assert.Single(scenario.MachineIntents);
        Assert.Equal(InstallerTransactionPhase.PackageCommitted, machineIntent.Journal.Phase);
        Assert.True(machineIntent.Journal.Matches(InstallerTestData.Request()));
        InstallerTransactionSnapshot packageCommitIntent =
            Assert.Single(scenario.PackageCommitIntents);
        Assert.Equal(
            InstallerTransactionPhase.MachineReserved,
            packageCommitIntent.Journal.Phase);
        Assert.True(packageCommitIntent.Journal.Matches(InstallerTestData.Request()));
        Assert.Equal(
            [InstallerTransactionPhase.MachineCommitted, InstallerTransactionPhase.Verified],
            scenario.FinalStates.Select(static state => state.Journal.Phase));
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task UninstallRemainsAvailableWithoutPackagePayloadAndReversesOrder()
    {
        InstallerScenario scenario = new()
        {
            Release = InstallerTestData.Release(
                packagePayloadAvailable: false,
                certificatePayloadAvailable: false),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(InstallerOperation.Uninstall),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(
            scenario.Events.IndexOf("machine.apply:Uninstall")
            < scenario.Events.IndexOf("package.apply:Uninstall"));
        Assert.True(
            scenario.Events.IndexOf("package.apply:Uninstall")
            < scenario.Events.IndexOf("certificate.apply:Uninstall"));
        InstallerTransactionSnapshot removalAuthorization =
            Assert.Single(scenario.MachinePreparationIntents);
        Assert.Equal(
            InstallerTransactionPhase.Prepared,
            removalAuthorization.Journal.Phase);
        InstallerTransactionSnapshot machineIntent = Assert.Single(scenario.MachineIntents);
        Assert.Equal(
            InstallerTransactionPhase.MachineRemovalAuthorized,
            machineIntent.Journal.Phase);
        Assert.True(machineIntent.Journal.Matches(
            InstallerTestData.Request(InstallerOperation.Uninstall)));
        InstallerTransactionSnapshot packageCommitIntent =
            Assert.Single(scenario.PackageCommitIntents);
        Assert.Equal(
            InstallerTransactionPhase.MachineCommitted,
            packageCommitIntent.Journal.Phase);
        Assert.Equal(
            [InstallerTransactionPhase.PackageCommitted, InstallerTransactionPhase.Verified],
            scenario.FinalStates.Select(static state => state.Journal.Phase));
        Assert.Empty(scenario.LastReleaseLease?.LockedFiles ?? []);
    }

    [Fact]
    public async Task ManifestIdentityMismatchIsRejectedBeforeDurableIntent()
    {
        VerifiedInstallerRelease differentRelease = InstallerTestData.Release(
            installerHash: InstallerTestData.OtherHash);
        InstallerScenario scenario = new()
        {
            ReleaseManifest = InstallerTestData.Manifest(differentRelease),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.release.manifest_identity_mismatch", result.DiagnosticCode);
        Assert.DoesNotContain("journal.load", scenario.Events);
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task MissingLockedFileIsRejectedBeforeDurableIntent()
    {
        InstallerScenario scenario = new()
        {
            LockedFiles = [],
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.release.locked_file_set_invalid", result.DiagnosticCode);
        Assert.DoesNotContain("journal.load", scenario.Events);
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task InstallFailsClosedWhenPackagePayloadIsMissing()
    {
        InstallerScenario scenario = new()
        {
            Release = InstallerTestData.Release(packagePayloadAvailable: false),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.release.package_payload_missing", result.DiagnosticCode);
        Assert.DoesNotContain("journal.save:Prepared", scenario.Events);
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task InstallFailsClosedWhenCertificatePayloadIsMissing()
    {
        InstallerScenario scenario = new()
        {
            Release = InstallerTestData.Release(certificatePayloadAvailable: false),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.release.certificate_payload_missing", result.DiagnosticCode);
        Assert.DoesNotContain("journal.save:Prepared", scenario.Events);
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task DowngradeIsRejectedBeforeReleaseOrMutation()
    {
        InstallerScenario scenario = new()
        {
            Environment = new InstallerEnvironmentSnapshot(true, "2.0.0.0", false, null),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(version: "1.9.9.9"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.package.downgrade_rejected", result.DiagnosticCode);
        Assert.Equal(["environment.inspect"], scenario.Events);
    }

    [Fact]
    public async Task RepairRequiresAnExistingPackage()
    {
        InstallerScenario scenario = new();
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(InstallerOperation.Repair),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.package.repair_requires_installation", result.DiagnosticCode);
    }

    [Fact]
    public async Task ExactRepairRecoveryCanRestoreAPackageThatBecameMissing()
    {
        InstallerTransactionJournal reserved = InstallerTestData
            .Journal(InstallerOperation.Repair)
            .TransitionTo(InstallerTransactionPhase.MachineReserved);
        InstallerScenario scenario = new(reserved);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        InstallerRequest request = InstallerTestData.Request(
            InstallerOperation.Repair,
            allowReassociation: true);

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            request,
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Contains("certificate.apply:Repair", scenario.Events);
        Assert.Contains("package.apply:Repair", scenario.Events);
        Assert.DoesNotContain("machine.prepare:Repair", scenario.Events);
    }

    [Fact]
    public async Task MutationFailureRetainsLastDurablePhaseForRollForward()
    {
        InstallerScenario scenario = new()
        {
            MachineAction = static _ => throw new InstallerProtocolException("installer.machine.failed"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(InstallerTransactionPhase.PackageCommitted, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
        Assert.Equal(InstallerTransactionPhase.PackageCommitted, scenario.Store.Current?.Journal.Phase);
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task UnconfirmedElevatedHelperStateIsReportedAsUncertain()
    {
        InstallerScenario scenario = new()
        {
            MachineAction = static _ => throw new InstallerStateUncertainException(
                "installer.elevation.termination_unconfirmed"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Uncertain, result.Outcome);
        Assert.Equal("installer.elevation.termination_unconfirmed", result.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.PackageCommitted, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
    }

    [Fact]
    public async Task UacCancellationIsReportedAsCancelledAndKeepsDurableRecovery()
    {
        InstallerScenario scenario = new()
        {
            MachineAction = static _ => throw new InstallerUserCancelledException(
                "installer.elevation.user_cancelled"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Cancelled, result.Outcome);
        Assert.Equal("installer.elevation.user_cancelled", result.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.PackageCommitted, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
    }

    [Fact]
    public async Task UacCancellationDuringMachineReservationCannotMutateUserState()
    {
        InstallerScenario scenario = new()
        {
            MachinePrepareAction = static _ => throw new InstallerUserCancelledException(
                "installer.elevation.user_cancelled"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Cancelled, result.Outcome);
        Assert.Equal(InstallerTransactionPhase.Prepared, result.LastDurablePhase);
        Assert.DoesNotContain("certificate.apply:Install", scenario.Events);
        Assert.DoesNotContain("package.apply:Install", scenario.Events);
        Assert.DoesNotContain("machine.apply:Install", scenario.Events);
        Assert.Equal(InstallerTransactionPhase.Prepared, scenario.Store.Current?.Journal.Phase);
    }

    [Fact]
    public async Task UacCancellationDuringRemovalAuthorizationCannotDeleteAnything()
    {
        InstallerScenario scenario = new()
        {
            MachinePrepareAction = static _ => throw new InstallerUserCancelledException(
                "installer.elevation.user_cancelled"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(InstallerOperation.Uninstall),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Cancelled, result.Outcome);
        Assert.Equal("installer.elevation.user_cancelled", result.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.Prepared, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
        Assert.DoesNotContain("machine.apply:Uninstall", scenario.Events);
        Assert.DoesNotContain("package.apply:Uninstall", scenario.Events);
        Assert.DoesNotContain("certificate.apply:Uninstall", scenario.Events);
        Assert.Equal(InstallerTransactionPhase.Prepared, scenario.Store.Current?.Journal.Phase);
    }

    [Fact]
    public async Task CancellationAfterPackageSideEffectKeepsMachineReservationForReplay()
    {
        using CancellationTokenSource cancellation = new();
        InstallerScenario scenario = new()
        {
            PackageAction = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            cancellation.Token);

        Assert.Equal(InstallerExecutionOutcome.Cancelled, result.Outcome);
        Assert.True(result.RecoveryPending);
        Assert.Equal(InstallerTransactionPhase.MachineReserved, scenario.Store.Current?.Journal.Phase);
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task ExactReleaseResumeSkipsAlreadyCommittedPackage()
    {
        InstallerTransactionJournal packageCommitted = InstallerTestData.Journal()
            .TransitionTo(InstallerTransactionPhase.MachineReserved)
            .TransitionTo(InstallerTransactionPhase.PackageCommitted);
        InstallerScenario scenario = new(packageCommitted);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain("certificate.apply:Install", scenario.Events);
        Assert.DoesNotContain("package.apply:Install", scenario.Events);
        Assert.DoesNotContain("machine.prepare:Install", scenario.Events);
        Assert.Contains("machine.apply:Install", scenario.Events);
    }

    [Fact]
    public async Task DifferentReleaseCannotTakeOverPendingTransaction()
    {
        InstallerScenario scenario = new(InstallerTestData.Journal())
        {
            Release = InstallerTestData.Release(installerHash: InstallerTestData.OtherHash),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(hash: InstallerTestData.OtherHash),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.transaction.release_conflict", result.DiagnosticCode);
        Assert.True(result.RecoveryPending);
        Assert.DoesNotContain("package.apply:Install", scenario.Events);
        Assert.True(scenario.LastReleaseLease?.IsDisposed);
    }

    [Fact]
    public async Task VerifiedResumeRechecksFinalStateOnceBeforeExactClear()
    {
        InstallerTransactionJournal verified = InstallerTestData.Journal()
            .TransitionTo(InstallerTransactionPhase.MachineReserved)
            .TransitionTo(InstallerTransactionPhase.PackageCommitted)
            .TransitionTo(InstallerTransactionPhase.MachineCommitted)
            .TransitionTo(InstallerTransactionPhase.Verified);
        InstallerScenario scenario = new(verified);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, scenario.Events.Count(static value => value == "final.verify"));
        Assert.Null(scenario.Store.Current);
    }

    [Fact]
    public async Task ConcurrentActionIsRejectedWithoutWaiting()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<InstallerEnvironmentSnapshot> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        InstallerScenario scenario = new()
        {
            EnvironmentAction = _ =>
            {
                entered.SetResult();
                return release.Task;
            },
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        Task<InstallerExecutionResult> first = coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);
        await entered.Task;

        InstallerExecutionResult second = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);
        release.SetResult(scenario.Environment);
        InstallerExecutionResult completedFirst = await first;

        Assert.Equal(InstallerExecutionOutcome.Blocked, second.Outcome);
        Assert.Equal("installer.concurrent_action_rejected", second.DiagnosticCode);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, completedFirst.Outcome);
    }

    [Fact]
    public async Task ThrowingProgressObserverCannotAbortDurableWork()
    {
        InstallerScenario scenario = new();
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            new ThrowingProgress(),
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
    }

    private sealed class ThrowingProgress : IProgress<InstallerProgress>
    {
        public void Report(InstallerProgress value) => throw new InvalidOperationException("UI fault");
    }
}
