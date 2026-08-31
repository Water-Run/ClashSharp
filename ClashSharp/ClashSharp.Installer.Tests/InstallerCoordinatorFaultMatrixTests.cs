using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>Exercises every durable cut-point without relying on timing or source-text assertions.</summary>
public sealed class InstallerCoordinatorFaultMatrixTests
{
    [Theory]
    [InlineData(InstallerOperation.Install, "machine_prepare", InstallerTransactionPhase.Prepared)]
    [InlineData(InstallerOperation.Install, "certificate", InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, "package", InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, "package_commit", InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, "machine", InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Repair, "machine_prepare", InstallerTransactionPhase.Prepared)]
    [InlineData(InstallerOperation.Repair, "certificate", InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Repair, "package", InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Repair, "package_commit", InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Repair, "machine", InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Uninstall, "machine_prepare", InstallerTransactionPhase.Prepared)]
    [InlineData(InstallerOperation.Uninstall, "machine", InstallerTransactionPhase.MachineRemovalAuthorized)]
    [InlineData(InstallerOperation.Uninstall, "package", InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, "package_commit", InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, "certificate", InstallerTransactionPhase.PackageCommitted)]
    public async Task MutationFailureRetainsTheLastProvenPhase(
        InstallerOperation operation,
        string failingMutation,
        InstallerTransactionPhase expectedPhase)
    {
        InstallerScenario scenario = ScenarioFor(operation);
        Func<CancellationToken, Task> failure = static _ =>
            throw new InstallerProtocolException("installer.test.cut_point");
        switch (failingMutation)
        {
            case "machine_prepare":
                scenario.MachinePrepareAction = failure;
                break;
            case "certificate":
                scenario.CertificateAction = failure;
                break;
            case "package":
                scenario.PackageAction = failure;
                break;
            case "package_commit":
                scenario.PackageCommitAction = failure;
                break;
            case "machine":
                scenario.MachineAction = failure;
                break;
            default:
                throw new InvalidOperationException("Unknown mutation cut-point.");
        }

        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Failed, result.Outcome);
        Assert.Equal("installer.test.cut_point", result.DiagnosticCode);
        Assert.Equal(expectedPhase, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
        Assert.Equal(expectedPhase, scenario.Store.Current?.Journal.Phase);
        Assert.DoesNotContain("journal.clear", scenario.Events);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, "machine_prepare", InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, "package_commit", InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Install, "machine", InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Install, "final", InstallerTransactionPhase.Verified)]
    [InlineData(InstallerOperation.Uninstall, "machine_prepare", InstallerTransactionPhase.MachineRemovalAuthorized)]
    [InlineData(InstallerOperation.Uninstall, "machine", InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, "package_commit", InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Uninstall, "final", InstallerTransactionPhase.Verified)]
    public async Task ParentNeverAdvancesWithoutTheExactHelperCommittedJournal(
        InstallerOperation operation,
        string staleBoundary,
        InstallerTransactionPhase expectedPhase)
    {
        InstallerScenario scenario = ScenarioFor(operation);
        switch (staleBoundary)
        {
            case "machine_prepare":
                scenario.MachinePrepareResultFactory = static state => state;
                break;
            case "machine":
                scenario.MachineResultFactory = static state => state;
                break;
            case "package_commit":
                scenario.PackageCommitResultFactory = static state => state;
                break;
            case "final":
                scenario.FinalResultFactory = static state => state;
                break;
            default:
                throw new InvalidOperationException("Unknown helper result boundary.");
        }

        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Failed, result.Outcome);
        Assert.Equal("installer.machine_helper.result_mismatch", result.DiagnosticCode);
        Assert.Equal(expectedPhase, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
        Assert.Equal(expectedPhase, scenario.Store.Current?.Journal.Phase);
        Assert.DoesNotContain("journal.clear", scenario.Events);
    }

    [Fact]
    public void CoordinatorDependsOnlyOnTheReadOnlyProtectedTransactionView()
    {
        Type coordinator = typeof(InstallerCoordinator);
        Type[] constructorDependencies = Assert.Single(coordinator.GetConstructors())
            .GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IInstallerTransactionReader), constructorDependencies);
        Assert.DoesNotContain(typeof(IInstallerTransactionStore), constructorDependencies);
        Assert.DoesNotContain(
            coordinator.GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic),
            static field => field.FieldType == typeof(IInstallerTransactionStore));
    }

    [Fact]
    public async Task HelperCommitSurvivesLostResponseAndIsRecoveredFromProtectedState()
    {
        InstallerScenario scenario = new()
        {
            MachineResponseAction = static _ => throw new InstallerStateUncertainException(
                "installer.elevation.response_lost"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Uncertain, result.Outcome);
        Assert.Equal("installer.elevation.response_lost", result.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.MachineCommitted, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
        Assert.Equal(
            InstallerTransactionPhase.MachineCommitted,
            scenario.Store.Current?.Journal.Phase);
        Assert.DoesNotContain("final.verify", scenario.Events);
    }

    [Fact]
    public async Task ClearResponseLossUsesSuccessfulNullReloadWithoutResurrectingVerifiedFallback()
    {
        InstallerScenario scenario = new()
        {
            FinalClearResponseAction = static _ => throw new InstallerStateUncertainException(
                "installer.elevation.clear_response_lost"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Uncertain, result.Outcome);
        Assert.Equal("installer.elevation.clear_response_lost", result.DiagnosticCode);
        Assert.Null(result.LastDurablePhase);
        Assert.False(result.RecoveryPending);
        Assert.Null(scenario.Store.Current);
        Assert.Contains("journal.clear", scenario.Events);
    }

    [Fact]
    public async Task ProtectedReloadFailureFallsBackOnlyToTheValidatedHelperResponse()
    {
        int loadCount = 0;
        InstallerScenario scenario = new();
        scenario.Store.LoadAction = _ => ++loadCount == 1
            ? Task.FromResult<InstallerTransactionSnapshot?>(null)
            : Task.FromException<InstallerTransactionSnapshot?>(
                new IOException("Injected protected-store read failure."));
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Uncertain, result.Outcome);
        Assert.Equal("installer.transaction.reload_failed", result.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.MachineReserved, result.LastDurablePhase);
        Assert.True(result.RecoveryPending);
        Assert.Equal(3, loadCount);
        Assert.Equal(
            InstallerTransactionPhase.MachineReserved,
            scenario.Store.Current?.Journal.Phase);
        Assert.DoesNotContain("certificate.apply:Install", scenario.Events);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Prepared, true, true, true, true, true)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineReserved, false, true, true, true, true)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.PackageCommitted, false, false, false, false, true)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineCommitted, false, false, false, false, false)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Verified, false, false, false, false, false)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.Prepared, true, true, true, true, true)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.MachineReserved, false, true, true, true, true)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.PackageCommitted, false, false, false, false, true)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.MachineCommitted, false, false, false, false, false)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.Verified, false, false, false, false, false)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.Prepared, true, true, true, true, true)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineRemovalAuthorized, false, true, true, true, true)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted, false, true, true, true, false)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted, false, true, false, false, false)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.Verified, false, false, false, false, false)]
    public async Task ResumeReplaysOnlyWorkAfterTheDurablePhase(
        InstallerOperation operation,
        InstallerTransactionPhase initialPhase,
        bool expectMachinePreparation,
        bool expectCertificateMutation,
        bool expectPackageMutation,
        bool expectPackageCommit,
        bool expectMachineMutation)
    {
        InstallerScenario scenario = ScenarioFor(operation, JournalAt(operation, initialPhase));
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            expectMachinePreparation,
            scenario.Events.Contains($"machine.prepare:{operation}"));
        Assert.Equal(
            expectCertificateMutation,
            scenario.Events.Contains($"certificate.apply:{operation}"));
        Assert.Equal(
            expectPackageMutation,
            scenario.Events.Contains($"package.apply:{operation}"));
        Assert.Equal(
            expectPackageCommit,
            scenario.Events.Contains($"machine.commit_package:{operation}"));
        Assert.Equal(
            expectMachineMutation,
            scenario.Events.Contains($"machine.apply:{operation}"));
        Assert.Null(scenario.Store.Current);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, 1, null)]
    [InlineData(InstallerOperation.Install, 2, InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, 3, InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, 4, InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Install, 5, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Install, 6, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Install, 7, InstallerTransactionPhase.Verified)]
    [InlineData(InstallerOperation.Uninstall, 1, null)]
    [InlineData(InstallerOperation.Uninstall, 2, InstallerTransactionPhase.MachineRemovalAuthorized)]
    [InlineData(InstallerOperation.Uninstall, 3, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, 4, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, 5, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Uninstall, 6, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Uninstall, 7, InstallerTransactionPhase.Verified)]
    public async Task ReleaseReverificationFailureNeverClaimsALaterPhase(
        InstallerOperation operation,
        int failingCall,
        InstallerTransactionPhase? expectedPhase)
    {
        int call = 0;
        InstallerScenario scenario = ScenarioFor(operation);
        scenario.ReleaseReverifyAction = _ =>
        {
            call++;
            return call == failingCall
                ? Task.FromException(new InstallerProtocolException("installer.release.changed"))
                : Task.CompletedTask;
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation),
            progress: null,
            CancellationToken.None);

        Assert.Equal(
            expectedPhase is null
                ? InstallerExecutionOutcome.Blocked
                : InstallerExecutionOutcome.Failed,
            result.Outcome);
        Assert.Equal("installer.release.changed", result.DiagnosticCode);
        Assert.Equal(expectedPhase, result.LastDurablePhase);
        Assert.Equal(expectedPhase is not null, result.RecoveryPending);
        Assert.Equal(expectedPhase, scenario.Store.Current?.Journal.Phase);
        Assert.DoesNotContain("journal.clear", scenario.Events);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted)]
    public async Task FirstFinalVerificationFailureDoesNotPersistVerified(
        InstallerOperation operation,
        InstallerTransactionPhase expectedPhase)
    {
        InstallerScenario scenario = ScenarioFor(operation);
        scenario.FinalVerifyAction = static _ =>
            throw new InstallerProtocolException("installer.final_state.invalid");
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(expectedPhase, result.LastDurablePhase);
        Assert.Equal(expectedPhase, scenario.Store.Current?.Journal.Phase);
        Assert.DoesNotContain("journal.save:Verified", scenario.Events);
    }

    [Fact]
    public async Task UnsupportedInstallTargetStillAllowsRecoveryUninstall()
    {
        InstallerScenario scenario = new()
        {
            Environment = new InstallerEnvironmentSnapshot(
                IsSupported: false,
                InstalledPackageVersion: null,
                IsApplicationRunning: false,
                BlockingDiagnosticCode: "installer.environment_unsupported"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(InstallerOperation.Uninstall),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Contains("machine.apply:Uninstall", scenario.Events);
        Assert.Contains("package.apply:Uninstall", scenario.Events);
        Assert.Contains("certificate.apply:Uninstall", scenario.Events);
    }

    [Fact]
    public async Task InvalidRequestDoesNotInspectEnvironmentOrTransactionState()
    {
        InstallerScenario scenario = new();
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        InstallerRequest invalid = new(
            InstallerOperation.Install,
            "S-1-5-18",
            AllowReassociation: false,
            InstallerTestData.Version,
            InstallerTestData.Hash);

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            invalid,
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.request.target_sid_invalid", result.DiagnosticCode);
        Assert.False(result.RecoveryPending);
        Assert.Empty(scenario.Events);
    }

    [Fact]
    public async Task UnexpectedPreflightFailureIsSanitizedWithoutCreatingRecoveryState()
    {
        InstallerScenario scenario = new()
        {
            EnvironmentAction = static _ =>
                throw new IOException("secret-user-path-and-token"),
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerTestData.Request(),
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.unexpected_failure", result.DiagnosticCode);
        Assert.False(result.RecoveryPending);
        Assert.Equal(["environment.inspect"], scenario.Events);
        Assert.DoesNotContain("secret-user-path-and-token", result.DiagnosticCode, StringComparison.Ordinal);
    }

    private static InstallerScenario ScenarioFor(
        InstallerOperation operation,
        InstallerTransactionJournal? initialJournal = null) =>
        new(initialJournal)
        {
            Environment = new InstallerEnvironmentSnapshot(
                IsSupported: true,
                InstalledPackageVersion: operation == InstallerOperation.Repair
                    ? InstallerTestData.Version
                    : null,
                IsApplicationRunning: false,
                BlockingDiagnosticCode: null),
        };

    private static InstallerRequest RequestFor(InstallerOperation operation) =>
        InstallerTestData.Request(
            operation,
            allowReassociation: operation == InstallerOperation.Repair);

    private static InstallerTransactionJournal JournalAt(
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(RequestFor(operation));
        foreach (InstallerTransactionPhase next in OrderFor(operation).Skip(1))
        {
            if (journal.Phase == phase)
            {
                return journal;
            }

            journal = journal.TransitionTo(next);
        }

        if (journal.Phase != phase)
        {
            throw new InvalidOperationException("The requested durable phase is invalid.");
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
