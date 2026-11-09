using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// Original-installation restoration and the explicit candidate continuation of a same-owner repair:
/// fail, cancel or lose a reply at every boundary, then resume through either entry point.
/// </summary>
public sealed class InstallerRecoveryFaultTests
{
    private static readonly InstallerTransactionPhase[] RestorablePhases =
    [
        InstallerTransactionPhase.Prepared,
        InstallerTransactionPhase.MachineReserved,
        InstallerTransactionPhase.OriginalRestored,
    ];

    public static TheoryData<InstallerTransactionPhase, int, InjectedFault, bool> RestorationCases()
    {
        var data = new TheoryData<InstallerTransactionPhase, int, InjectedFault, bool>();
        foreach (InstallerTransactionPhase phase in RestorablePhases)
        {
            RestorationPorts clean = new(phase, plan: null);
            using InstallerCoordinator coordinator = clean.Coordinator();
            InstallerExecutionResult result = coordinator
                .RestoreOriginalAsync(clean.Request, null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (result.Outcome != InstallerExecutionOutcome.Succeeded)
            {
                throw new InvalidOperationException("The clean restoration must succeed.");
            }

            for (int point = 0; point < clean.Plan.Points.Count; point++)
            {
                foreach (InjectedFault fault in Enum.GetValues<InjectedFault>())
                {
                    data.Add(phase, point, fault, false);
                    data.Add(phase, point, fault, true);
                }
            }
        }

        return data;
    }

    public static TheoryData<int, InjectedFault> ContinuationCases()
    {
        var data = new TheoryData<int, InjectedFault>();
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Repair);
        FaultPlan clean = new(() => scenario.Events.Count);
        scenario.Attach(clean);
        ContinuationMachine machine = new(scenario, clean);
        using InstallerCoordinator coordinator = machine.Coordinator(scenario);
        InstallerExecutionResult result = coordinator
            .ExecuteAsync(PlainRepair(), null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (result.Outcome != InstallerExecutionOutcome.Succeeded || !clean.Points.Contains("machine.continue"))
        {
            throw new InvalidOperationException("The clean continuation run must succeed through the continuation port.");
        }

        for (int point = 0; point < clean.Points.Count; point++)
        {
            foreach (InjectedFault fault in Enum.GetValues<InjectedFault>())
            {
                data.Add(point, fault);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RestorationCases))]
    public async Task RestorationFaultAtAnyBoundaryFailsSafeAndEitherEntryPointResumes(
        InstallerTransactionPhase initial,
        int point,
        InjectedFault fault,
        bool resumeThroughExecute)
    {
        RestorationPorts ports = new(initial, (index, _) => index == point ? fault : null);
        InstallerExecutionResult faulted;
        using (InstallerCoordinator crashed = ports.Coordinator())
        {
            faulted = await crashed.RestoreOriginalAsync(ports.Request, null, ports.Plan.Cancellation.Token);
        }

        Assert.Equal(point, ports.Plan.FaultedPoint);
        if (fault == InjectedFault.CancelOnly)
        {
            Assert.True(faulted.Outcome is InstallerExecutionOutcome.Cancelled or InstallerExecutionOutcome.Succeeded);
        }
        else
        {
            Assert.NotEqual(InstallerExecutionOutcome.Succeeded, faulted.Outcome);
            string site = ports.Plan.Points[point];
            if (site != "journal.load" || faulted.DiagnosticCode is not ("installer.transaction.reload_failed" or "installer.transaction.clear_reload_failed"))
            {
                Assert.Equal(ports.Plan.ExpectedDiagnosticCode, faulted.DiagnosticCode);
            }

            Assert.All(
                ports.Events.Skip(ports.Plan.EventCountAtFault),
                static entry => Assert.True(entry is "journal.load" or "release.dispose", $"Unexpected effect '{entry}' after a failure."));
        }

        // A result never claims recovery that does not exist; once the journal was read it never hides it.
        int loadsBeforeFault = ports.Events.Take(ports.Plan.EventCountAtFault).Count(static entry => entry == "journal.load")
            - (ports.Plan.Points[point] == "journal.load" ? 1 : 0);
        if (faulted.RecoveryPending)
        {
            Assert.NotNull(ports.Current);
            Assert.Equal(ports.Current.Journal.Phase, faulted.LastDurablePhase);
        }
        else if (loadsBeforeFault >= 1)
        {
            Assert.Null(ports.Current);
        }

        // Only an OriginalRestored terminal may ever be cleared.
        if (ports.Current is null)
        {
            Assert.Contains("restore.committed", ports.Events);
        }

        ports.DisarmFaults();
        if (ports.Current is not null)
        {
            using InstallerCoordinator resumed = ports.Coordinator();
            // ExecuteAsync only continues a restoration once its terminal is durable; before that it
            // would start the candidate, which this participant must never be asked to do.
            bool viaExecute = resumeThroughExecute
                && ports.Current.Journal.Phase == InstallerTransactionPhase.OriginalRestored;
            InstallerExecutionResult recovery = viaExecute
                ? await resumed.ExecuteAsync(ports.Request, null, CancellationToken.None)
                : await resumed.RestoreOriginalAsync(ports.Request, null, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Succeeded, recovery.Outcome);
            Assert.Equal("installer.recovery.original_restored", recovery.DiagnosticCode);
            Assert.False(recovery.RecoveryPending);
        }

        Assert.Null(ports.Current);
        // The original installation is restored exactly once, however many times the run was interrupted.
        Assert.Equal(initial == InstallerTransactionPhase.OriginalRestored ? 0 : 1, ports.RestoreEffects);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, false)]
    [InlineData(InstallerOperation.Uninstall, false)]
    [InlineData(InstallerOperation.Repair, true)]
    public async Task RestorationIsOnlyOfferedForANonReassociatingRepairAndNeverReachesThePrivilegedPortOtherwise(
        InstallerOperation operation,
        bool reassociation)
    {
        RestorationPorts ports = new(InstallerTransactionPhase.Prepared, plan: null);
        InstallerRequest request = InstallerTestData.Request(operation, reassociation);
        using InstallerCoordinator coordinator = ports.Coordinator();

        InstallerExecutionResult result = await coordinator.RestoreOriginalAsync(request, null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.recovery.operation_invalid", result.DiagnosticCode);
        Assert.Equal(0, ports.RestoreEffects);
        Assert.DoesNotContain("restore", ports.Events);
        Assert.NotNull(ports.Current);
    }

    [Theory]
    [InlineData("unsupported", "installer.environment_unsupported")]
    [InlineData("running", "installer.application_running")]
    [InlineData("downgrade", "installer.package.downgrade_rejected")]
    public async Task RestorationHonoursEnvironmentGatesBeforeTouchingTheLeaseOrTheJournal(string gate, string expectedCode)
    {
        RestorationPorts ports = new(InstallerTransactionPhase.Prepared, plan: null);
        ports.Environment = gate switch
        {
            "unsupported" => new InstallerEnvironmentSnapshot(false, "1.2.3.3", false, null),
            "running" => new InstallerEnvironmentSnapshot(true, "1.2.3.3", true, null),
            _ => new InstallerEnvironmentSnapshot(true, "9.9.9.9", false, null),
        };
        using InstallerCoordinator coordinator = ports.Coordinator();

        InstallerExecutionResult result = await coordinator.RestoreOriginalAsync(ports.Request, null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal(expectedCode, result.DiagnosticCode);
        Assert.Equal(["environment"], ports.Events);
    }

    [Fact]
    public async Task RestorationWithoutThePrivilegedCapabilityOrWithAWrongPhaseIsRefusedWithoutMutation()
    {
        // A machine participant that does not implement restoration cannot be asked to restore.
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Repair);
        InstallerRequest request = PlainRepair();
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        InstallerScenario withJournal = new(journal)
        {
            Environment = scenario.Environment,
        };
        using InstallerCoordinator noCapability = withJournal.CreateCoordinator();

        InstallerExecutionResult unavailable = await noCapability.RestoreOriginalAsync(request, null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Failed, unavailable.Outcome);
        Assert.Equal("installer.recovery.capability_unavailable", unavailable.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.Prepared, withJournal.Store.Current?.Journal.Phase);
        Assert.DoesNotContain(withJournal.Events, static entry => entry.StartsWith("machine.", StringComparison.Ordinal));

        foreach (InstallerTransactionPhase phase in new[] { InstallerTransactionPhase.PackageCommitted, InstallerTransactionPhase.MachineCommitted, InstallerTransactionPhase.Verified })
        {
            RestorationPorts ports = new(InstallerTransactionPhase.Prepared, plan: null);
            InstallerTransactionJournal advanced = InstallerTransactionJournal.Create(ports.Request) with { TransactionId = InstallerTestData.TransactionId };
            foreach (InstallerTransactionPhase next in InstallerCoordinatorInterruptionTests.ExpectedChain(InstallerOperation.Repair).Skip(1))
            {
                advanced = advanced.TransitionTo(next);
                if (advanced.Phase == phase)
                {
                    break;
                }
            }

            ports.Current = InstallerTransactionSnapshot.Create(advanced);
            using InstallerCoordinator coordinator = ports.Coordinator();

            InstallerExecutionResult wrongPhase = await coordinator.RestoreOriginalAsync(ports.Request, null, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Failed, wrongPhase.Outcome);
            Assert.Equal("installer.recovery.phase_invalid", wrongPhase.DiagnosticCode);
            Assert.Equal(0, ports.RestoreEffects);
            Assert.Equal(advanced.Phase, ports.Current?.Journal.Phase);
        }
    }

    [Fact]
    public async Task ARestorationRequestForAnotherReleaseOrWithNoJournalIsRefusedAndNothingIsRestored()
    {
        RestorationPorts missing = new(InstallerTransactionPhase.Prepared, plan: null);
        missing.Current = null;
        using (InstallerCoordinator coordinator = missing.Coordinator())
        {
            InstallerExecutionResult result = await coordinator.RestoreOriginalAsync(missing.Request, null, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
            Assert.Equal("installer.recovery.transaction_missing", result.DiagnosticCode);
            Assert.False(result.RecoveryPending);
        }

        RestorationPorts foreign = new(InstallerTransactionPhase.Prepared, plan: null);
        foreign.Current = InstallerTransactionSnapshot.Create(foreign.Current!.Journal with { TargetSid = "S-1-5-21-100-200-300-1002" });
        using (InstallerCoordinator coordinator = foreign.Coordinator())
        {
            InstallerExecutionResult result = await coordinator.RestoreOriginalAsync(foreign.Request, null, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Failed, result.Outcome);
            Assert.Equal("installer.transaction.release_conflict", result.DiagnosticCode);
            Assert.True(result.RecoveryPending);
            Assert.Equal(0, foreign.RestoreEffects);
        }
    }

    [Theory]
    [MemberData(nameof(ContinuationCases))]
    public async Task CandidateContinuationFaultAtAnyBoundaryFailsSafeAndResumeConverges(int point, InjectedFault fault)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Repair);
        FaultPlan plan = FaultPlan.At(() => scenario.Events.Count, point, fault);
        scenario.Attach(plan);
        ContinuationMachine machine = new(scenario, plan);
        InstallerRequest request = PlainRepair();

        InstallerExecutionResult faulted;
        using (InstallerCoordinator crashed = machine.Coordinator(scenario))
        {
            faulted = await crashed.ExecuteAsync(request, null, plan.Cancellation.Token);
        }

        Assert.Equal(point, plan.FaultedPoint);
        if (fault != InjectedFault.CancelOnly)
        {
            Assert.NotEqual(InstallerExecutionOutcome.Succeeded, faulted.Outcome);
            Assert.All(
                scenario.Events.Skip(plan.EventCountAtFault),
                static entry => Assert.True(entry is "journal.load" or "release.dispose", $"Unexpected effect '{entry}' after a failure."));
        }

        Assert.Equal(scenario.Store.Current is not null, faulted.RecoveryPending);
        bool completed = scenario.Events.Contains("journal.clear");
        scenario.Detach();
        using InstallerCoordinator resumed = machine.Coordinator(scenario);
        InstallerExecutionResult recovery = await resumed.ExecuteAsync(request, null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, recovery.Outcome);
        Assert.Null(scenario.Store.Current);
        Assert.Equal(
            Enumerable.Repeat(InstallerCoordinatorInterruptionTests.ExpectedChain(InstallerOperation.Repair), completed ? 2 : 1).SelectMany(static chain => chain),
            scenario.Events
                .Where(static entry => entry.StartsWith("journal.save:", StringComparison.Ordinal))
                .Select(static entry => Enum.Parse<InstallerTransactionPhase>(entry["journal.save:".Length..])));
        // The candidate is never deployed before the continuation was acknowledged, in any interleaving.
        for (int index = 0; index < scenario.Events.Count; index++)
        {
            if (scenario.Events[index].StartsWith("package.apply", StringComparison.Ordinal))
            {
                Assert.Contains("continue.ack", scenario.Events.Take(index));
            }
        }
    }

    [Theory]
    [InlineData("other_state")]
    [InlineData("advanced_state")]
    [InlineData("null_state")]
    public async Task AnUntrustworthyContinuationReceiptNeverLetsTheCandidateBeDeployed(string defect)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Repair);
        ContinuationMachine machine = new(scenario, new FaultPlan(() => 0)) { Defect = defect };
        using InstallerCoordinator coordinator = machine.Coordinator(scenario);

        InstallerExecutionResult result = await coordinator.ExecuteAsync(PlainRepair(), null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(
            defect == "null_state" ? "installer.machine_helper.result_state_missing" : "installer.machine_helper.result_mismatch",
            result.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.MachineReserved, scenario.Store.Current?.Journal.Phase);
        Assert.DoesNotContain(scenario.Events, static entry => entry.StartsWith("package.apply", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Events, static entry => entry.StartsWith("certificate.apply", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task OnlyAPlainServiceRepairUsesTheContinuationPortAndOtherOperationsNeverCallIt(InstallerOperation operation)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
        ContinuationMachine machine = new(scenario, new FaultPlan(() => 0));
        using InstallerCoordinator coordinator = machine.Coordinator(scenario);

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, machine.Continuations);

        // A repair without the transparent-proxy service owns nothing to pin, and a reassociating repair
        // replaces the original deliberately; neither goes through candidate continuation.
        foreach (InstallerRequest request in new[]
        {
            PlainRepair() with { InstallTransparentProxyService = false },
            InstallerTestData.Request(InstallerOperation.Repair, allowReassociation: true),
        })
        {
            InstallerScenario repair = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Repair);
            ContinuationMachine repairMachine = new(repair, new FaultPlan(() => 0));
            using InstallerCoordinator repairer = repairMachine.Coordinator(repair);
            Assert.Equal(InstallerExecutionOutcome.Succeeded, (await repairer.ExecuteAsync(request, null, CancellationToken.None)).Outcome);
            Assert.Equal(0, repairMachine.Continuations);
        }
    }

    private static InstallerRequest PlainRepair() => InstallerTestData.Request(InstallerOperation.Repair, allowReassociation: false);

    private sealed class ContinuationMachine(InstallerScenario scenario, FaultPlan plan) :
        IInstallerMachineMutation,
        IInstallerOriginalRestorationMutation
    {
        internal string? Defect { get; init; }

        internal int Continuations { get; private set; }

        internal InstallerCoordinator Coordinator(InstallerScenario inner) =>
            new(inner, inner, inner, inner, this, inner, inner.Store);

        public Task<InstallerTransactionSnapshot> PrepareAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableIntent,
            CancellationToken cancellationToken) =>
            ((IInstallerMachineMutation)scenario).PrepareAsync(request, release, durableIntent, cancellationToken);

        public Task<InstallerTransactionSnapshot> CommitPackageAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableIntent,
            CancellationToken cancellationToken) =>
            ((IInstallerMachineMutation)scenario).CommitPackageAsync(request, release, durableIntent, cancellationToken);

        public Task<InstallerTransactionSnapshot> ApplyAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableIntent,
            CancellationToken cancellationToken) =>
            ((IInstallerMachineMutation)scenario).ApplyAsync(request, release, durableIntent, cancellationToken);

        public async Task<InstallerTransactionSnapshot> ContinueCandidateAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot reservedState,
            CancellationToken cancellationToken)
        {
            Continuations++;
            scenario.Events.Add("machine.continue");
            await plan.Hit("machine.continue", cancellationToken);
            scenario.Events.Add("continue.ack");
            return Defect switch
            {
                "other_state" => InstallerTransactionSnapshot.Create(reservedState.Journal with { TransactionId = InstallerTestData.OtherHash }),
                "advanced_state" => InstallerTransactionSnapshot.Create(reservedState.Journal.TransitionTo(InstallerTransactionPhase.PackageCommitted)),
                "null_state" => null!,
                _ => reservedState,
            };
        }

        public Task<InstallerTransactionSnapshot> RestoreOriginalAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableState,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InstallerTransactionSnapshot> ClearOriginalRestoredAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot restoredState,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RestorationPorts :
        IInstallerEnvironment,
        IInstallerReleaseVerifier,
        IInstallerCertificateMutation,
        IInstallerPackageMutation,
        IInstallerMachineMutation,
        IInstallerFinalVerifier,
        IInstallerOriginalRestorationMutation,
        IInstallerTransactionReader
    {
        private Func<int, string, InjectedFault?>? _decide;

        internal RestorationPorts(InstallerTransactionPhase initial, Func<int, string, InjectedFault?>? plan)
        {
            _decide = plan;
            Plan = new FaultPlan(() => Events.Count, (index, name) => _decide?.Invoke(index, name));
            InstallerTransactionJournal journal = InstallerTransactionJournal.Create(Request) with
            {
                TransactionId = InstallerTestData.TransactionId,
            };
            journal = initial switch
            {
                InstallerTransactionPhase.Prepared => journal,
                InstallerTransactionPhase.MachineReserved => journal.TransitionTo(InstallerTransactionPhase.MachineReserved),
                _ => journal.TransitionTo(InstallerTransactionPhase.OriginalRestored),
            };
            Current = InstallerTransactionSnapshot.Create(journal);
        }

        internal InstallerRequest Request { get; } = PlainRepair();

        internal FaultPlan Plan { get; }

        internal List<string> Events { get; } = [];

        internal InstallerTransactionSnapshot? Current { get; set; }

        internal InstallerEnvironmentSnapshot Environment { get; set; } = new(true, "1.2.3.3", false, null);

        internal int RestoreEffects { get; private set; }

        internal void DisarmFaults() => _decide = null;

        internal InstallerCoordinator Coordinator() => new(this, this, this, this, this, this, this);

        public async Task<InstallerEnvironmentSnapshot> InspectAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            Events.Add("environment");
            await Plan.Hit("environment", cancellationToken);
            return Environment;
        }

        public Task<IInstallerReleaseLease> VerifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            Events.Add("release.verify");
            return Task.FromResult<IInstallerReleaseLease>(InstallerTestData.Lease(
                InstallerTestData.Release(),
                async (_, token) =>
                {
                    Events.Add("release.reverify");
                    await Plan.Hit("release.reverify", token);
                },
                () =>
                {
                    Events.Add("release.dispose");
                    return ValueTask.CompletedTask;
                }));
        }

        public async Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            Events.Add("journal.load");
            await Plan.Hit("journal.load", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Current;
        }

        public async Task<InstallerTransactionSnapshot> RestoreOriginalAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableState,
            CancellationToken cancellationToken)
        {
            Events.Add("restore");
            cancellationToken.ThrowIfCancellationRequested();
            await Plan.Hit("restore.before", cancellationToken);
            if (durableState.Journal.Phase != InstallerTransactionPhase.OriginalRestored)
            {
                RestoreEffects++;
            }

            Current = InstallerTransactionSnapshot.Create(
                durableState.Journal.TransitionTo(InstallerTransactionPhase.OriginalRestored));
            Events.Add("restore.committed");
            await Plan.Hit("restore.after", cancellationToken);
            return Current;
        }

        public async Task<InstallerTransactionSnapshot> ClearOriginalRestoredAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot restoredState,
            CancellationToken cancellationToken)
        {
            Events.Add("clear");
            Assert.Equal(InstallerTransactionPhase.OriginalRestored, restoredState.Journal.Phase);
            await Plan.Hit("clear.before", cancellationToken);
            Current = null;
            await Plan.Hit("clear.after", cancellationToken);
            return restoredState;
        }

        public Task<InstallerTransactionSnapshot> ContinueCandidateAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot reservedState,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Not part of restoration.");

        public Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Restoration must never deploy the candidate.");

        Task IInstallerCertificateMutation.ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Restoration must never change the certificate store.");

        public Task<InstallerTransactionSnapshot> PrepareAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableIntent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Restoration must not start a candidate transaction.");

        public Task<InstallerTransactionSnapshot> CommitPackageAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableIntent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Restoration must not commit a candidate package.");

        Task<InstallerTransactionSnapshot> IInstallerMachineMutation.ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableIntent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Restoration must not apply the candidate machine payload.");

        public Task<InstallerTransactionSnapshot> VerifyAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableState, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Candidate verification cannot prove preservation.");

        public Task<InstallerClearReceipt> ClearVerifiedAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot verifiedState, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Restoration clears only its own terminal.");
    }
}
