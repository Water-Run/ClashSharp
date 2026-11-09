using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// Crash/cancel/fail at every effect boundary an operation reaches (enumerated from a clean run, not
/// hand-picked), then resume with a fresh coordinator against the surviving durable journal.
/// </summary>
public sealed class InstallerCoordinatorInterruptionTests
{
    private const int Seed = 20261109;

    private static readonly InstallerOperation[] Operations =
        [InstallerOperation.Install, InstallerOperation.Repair, InstallerOperation.Uninstall];

    private static readonly InjectedFault[] Faults = Enum.GetValues<InjectedFault>();

    public static TheoryData<InstallerOperation, int, InjectedFault> SingleFaultCases()
    {
        var data = new TheoryData<InstallerOperation, int, InjectedFault>();
        foreach (InstallerOperation operation in Operations)
        {
            int points = CleanRunPointCount(operation);
            for (int point = 0; point < points; point++)
            {
                foreach (InjectedFault fault in Faults)
                {
                    data.Add(operation, point, fault);
                }
            }
        }

        return data;
    }

    public static TheoryData<InstallerOperation, int> RandomCases()
    {
        var data = new TheoryData<InstallerOperation, int>();
        foreach (InstallerOperation operation in Operations)
        {
            for (int seed = 0; seed < 40; seed++)
            {
                data.Add(operation, seed);
            }
        }

        return data;
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task CleanRunReachesEveryBoundaryOfTheOperationChainInOrder(InstallerOperation operation)
    {
        InstallerScenario scenario = NewScenario(operation);
        FaultPlan plan = new(() => scenario.Events.Count);
        scenario.Attach(plan);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation), progress: null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(ExpectedChain(operation), SavedPhases(scenario));
        Assert.Null(scenario.Store.Current);
        Assert.NotEmpty(plan.Points);
    }

    [Theory]
    [MemberData(nameof(SingleFaultCases))]
    public async Task FaultAtAnyEffectBoundaryFailsSafeAndResumeConverges(
        InstallerOperation operation,
        int point,
        InjectedFault fault)
    {
        InstallerScenario scenario = NewScenario(operation);
        FaultPlan plan = FaultPlan.At(() => scenario.Events.Count, point, fault);
        scenario.Attach(plan);
        InstallerRequest request = RequestFor(operation);

        InstallerExecutionResult faulted;
        using (InstallerCoordinator crashed = scenario.CreateCoordinator())
        {
            faulted = await crashed.ExecuteAsync(request, progress: null, plan.Cancellation.Token);
        }

        Assert.Equal(point, plan.FaultedPoint);
        AssertFaultedRunIsSafe(scenario, plan, faulted);

        scenario.Detach();
        int eventsBeforeResume = scenario.Events.Count;
        // A fault after the terminal clear lost only the acknowledgement: the transaction is complete.
        bool alreadyCompleted = scenario.Events.Contains("journal.clear");
        InstallerTransactionPhase? phaseBeforeResume = scenario.Store.Current?.Journal.Phase;
        using InstallerCoordinator resumed = scenario.CreateCoordinator();
        InstallerExecutionResult recovery = await resumed.ExecuteAsync(
            request, progress: null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, recovery.Outcome);
        Assert.Equal("installer.completed", recovery.DiagnosticCode);
        Assert.False(recovery.RecoveryPending);
        Assert.Null(scenario.Store.Current);
        Assert.Equal(
            alreadyCompleted
                ? ExpectedChain(operation).Concat(ExpectedChain(operation))
                : ExpectedChain(operation),
            SavedPhases(scenario));
        AssertEffectsRanAtLeastOnceAndReplayedAtMostOnce(scenario, operation);
        if (phaseBeforeResume is { } resumedFrom)
        {
            // Roll-forward only: nothing at or before the durable phase is committed again.
            foreach (string entry in scenario.Events.Skip(eventsBeforeResume)
                         .Where(static entry => entry.StartsWith("journal.save:", StringComparison.Ordinal)))
            {
                InstallerTransactionPhase saved = Enum.Parse<InstallerTransactionPhase>(entry["journal.save:".Length..]);
                Assert.True(Rank(operation, saved) > Rank(operation, resumedFrom));
            }
        }
    }

    [Theory]
    [MemberData(nameof(RandomCases))]
    public async Task RepeatedRandomInterruptionsNeverRegressDurableStateAndAlwaysConverge(
        InstallerOperation operation,
        int seed)
    {
        Random random = new(Seed + seed);
        InstallerScenario scenario = NewScenario(operation);
        InstallerRequest request = RequestFor(operation);
        string? transactionId = null;
        int lastGeneration = 0;
        int attempts = random.Next(1, 6);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            FaultPlan plan = new(
                () => scenario.Events.Count,
                (_, _) => random.NextDouble() < 0.07 ? Faults[random.Next(Faults.Length)] : null);
            scenario.Attach(plan);
            using InstallerCoordinator coordinator = scenario.CreateCoordinator();
            InstallerExecutionResult result = await coordinator.ExecuteAsync(
                request, progress: null, plan.Cancellation.Token);

            if (result.RecoveryPending)
            {
                Assert.NotNull(scenario.Store.Current);
            }

            if (result.LastDurablePhase is { } reported && scenario.Store.Current is { } current)
            {
                Assert.True(
                    reported == current.Journal.Phase,
                    "A result may only claim the phase that is actually durable.");
            }

            InstallerTransactionSnapshot? snapshot = scenario.Store.Current;
            if (snapshot is not null)
            {
                transactionId ??= snapshot.Journal.TransactionId;
                Assert.Equal(transactionId, snapshot.Journal.TransactionId);
                Assert.True(snapshot.Journal.Generation >= lastGeneration);
                lastGeneration = snapshot.Journal.Generation;
            }
            else
            {
                // Absence is only legal before a journal exists or after a Verified transaction cleared.
                transactionId = null;
                lastGeneration = 0;
            }
        }

        scenario.Detach();
        using InstallerCoordinator final = scenario.CreateCoordinator();
        InstallerExecutionResult recovered = await final.ExecuteAsync(
            request, progress: null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, recovered.Outcome);
        Assert.Null(scenario.Store.Current);
        // No transaction is ever abandoned or re-committed: every transaction contributes exactly one chain.
        int completed = scenario.Events.Count(static entry => entry == "journal.clear");
        Assert.True(completed >= 1);
        Assert.Equal(
            Enumerable.Repeat(ExpectedChain(operation), completed).SelectMany(static chain => chain),
            SavedPhases(scenario));
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task RepeatingACompletedOperationStartsAnIndependentTransaction(InstallerOperation operation)
    {
        InstallerScenario scenario = NewScenario(operation);
        InstallerRequest request = RequestFor(operation);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult first = await coordinator.ExecuteAsync(request, null, CancellationToken.None);
        InstallerExecutionResult second = await coordinator.ExecuteAsync(request, null, CancellationToken.None);
        InstallerExecutionResult third = await coordinator.ExecuteAsync(request, null, CancellationToken.None);

        Assert.All(new[] { first, second, third }, static result =>
        {
            Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
            Assert.False(result.RecoveryPending);
        });
        Assert.Null(scenario.Store.Current);
        string[] ids = scenario.MachinePreparationIntents
            .Select(static intent => intent.Journal.TransactionId)
            .ToArray();
        Assert.Equal(3, ids.Length);
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, SavedPhases(scenario).Count(static phase => phase == InstallerTransactionPhase.Verified));
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task ProgressIsMonotonicAndAProgressObserverCanNeverAbortTheTransaction(InstallerOperation operation)
    {
        InstallerScenario reference = NewScenario(operation);
        RecordingProgress recorder = new(throwFrom: int.MaxValue);
        using (InstallerCoordinator coordinator = reference.CreateCoordinator())
        {
            Assert.Equal(
                InstallerExecutionOutcome.Succeeded,
                (await coordinator.ExecuteAsync(RequestFor(operation), recorder, CancellationToken.None)).Outcome);
        }

        int[] percents = recorder.Reports.Select(static report => report.Percent).ToArray();
        Assert.NotEmpty(percents);
        Assert.Equal(percents.Order().ToArray(), percents);
        Assert.Equal(100, percents[^1]);
        Assert.All(recorder.Reports, static report => Assert.False(string.IsNullOrWhiteSpace(report.MessageKey)));

        for (int throwFrom = 0; throwFrom < percents.Length; throwFrom++)
        {
            InstallerScenario scenario = NewScenario(operation);
            RecordingProgress hostile = new(throwFrom);
            using InstallerCoordinator coordinator = scenario.CreateCoordinator();
            InstallerExecutionResult result = await coordinator.ExecuteAsync(
                RequestFor(operation), hostile, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
            Assert.Null(scenario.Store.Current);
            Assert.Equal(percents.Length, hostile.Reports.Count);
        }
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task CancellationAfterCommittedSuccessIsNotReportedAsCancelled(InstallerOperation operation)
    {
        InstallerScenario scenario = NewScenario(operation);
        using CancellationTokenSource cancellation = new();
        scenario.FinalClearResponseAction = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation), null, cancellation.Token);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Null(scenario.Store.Current);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task PreCancelledTokenDoesNoWorkAndCreatesNoDurableState(InstallerOperation operation)
    {
        InstallerScenario scenario = NewScenario(operation);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            RequestFor(operation), null, cancellation.Token);

        Assert.Equal(InstallerExecutionOutcome.Cancelled, result.Outcome);
        Assert.Equal("installer.cancelled", result.DiagnosticCode);
        Assert.False(result.RecoveryPending);
        Assert.Null(result.LastDurablePhase);
        Assert.Null(scenario.Store.Current);
        Assert.Empty(scenario.Events);
    }

    [Fact]
    public async Task DisposedCoordinatorRefusesWorkAndDisposeIsIdempotent()
    {
        InstallerScenario scenario = NewScenario(InstallerOperation.Install);
        InstallerCoordinator coordinator = scenario.CreateCoordinator();
        coordinator.Dispose();
        coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.ExecuteAsync(RequestFor(InstallerOperation.Install), null, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.RestoreOriginalAsync(RequestFor(InstallerOperation.Repair), null, CancellationToken.None));
        Assert.Empty(scenario.Events);
    }

    [Fact]
    public async Task ConcurrentExecutionIsRejectedWithoutTouchingAnyEffectAndTheGateIsReleased()
    {
        InstallerScenario scenario = NewScenario(InstallerOperation.Install);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PackageAction = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        Task<InstallerExecutionResult> running = coordinator.ExecuteAsync(
            RequestFor(InstallerOperation.Install), null, CancellationToken.None);
        await entered.Task.WaitAsync(TestTimeouts.Completion);
        int eventsWhileRunning = scenario.Events.Count;

        foreach (InstallerOperation other in Operations)
        {
            InstallerExecutionResult rejected = await coordinator.ExecuteAsync(
                RequestFor(other), null, CancellationToken.None);
            Assert.Equal(InstallerExecutionOutcome.Blocked, rejected.Outcome);
            Assert.Equal("installer.concurrent_action_rejected", rejected.DiagnosticCode);
            Assert.False(rejected.RecoveryPending);
        }

        Assert.Equal(eventsWhileRunning, scenario.Events.Count);
        release.SetResult();
        Assert.Equal(InstallerExecutionOutcome.Succeeded, (await running).Outcome);

        InstallerExecutionResult next = await coordinator.ExecuteAsync(
            RequestFor(InstallerOperation.Install), null, CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, next.Outcome);
    }

    [Fact]
    public async Task GateIsReleasedAfterEveryTerminalOutcomeSoARetryIsAdmitted()
    {
        InstallerScenario scenario = NewScenario(InstallerOperation.Install);
        int failuresLeft = 3;
        scenario.PackageAction = _ => failuresLeft-- > 0
            ? Task.FromException(new InstallerProtocolException("installer.test.transient"))
            : Task.CompletedTask;
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        InstallerRequest request = RequestFor(InstallerOperation.Install);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            InstallerExecutionResult result = await coordinator.ExecuteAsync(request, null, CancellationToken.None);
            Assert.Equal(InstallerExecutionOutcome.Failed, result.Outcome);
            Assert.Equal("installer.test.transient", result.DiagnosticCode);
            Assert.Equal(InstallerTransactionPhase.MachineReserved, result.LastDurablePhase);
            Assert.True(result.RecoveryPending);
        }

        InstallerExecutionResult recovered = await coordinator.ExecuteAsync(request, null, CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, recovered.Outcome);
        Assert.Equal(ExpectedChain(InstallerOperation.Install), SavedPhases(scenario));
        Assert.Equal(4, scenario.Events.Count(static entry => entry == "package.apply:Install"));
    }

    internal static InstallerScenario NewScenario(
        InstallerOperation operation,
        InstallerTransactionJournal? initialJournal = null) =>
        new(initialJournal)
        {
            Environment = new InstallerEnvironmentSnapshot(
                IsSupported: true,
                InstalledPackageVersion: operation == InstallerOperation.Repair ? InstallerTestData.Version : null,
                IsApplicationRunning: false,
                BlockingDiagnosticCode: null),
        };

    internal static InstallerRequest RequestFor(InstallerOperation operation) =>
        InstallerTestData.Request(operation, allowReassociation: operation == InstallerOperation.Repair);

    internal static InstallerTransactionPhase[] ExpectedChain(InstallerOperation operation) =>
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

    private static int Rank(InstallerOperation operation, InstallerTransactionPhase phase) =>
        Array.IndexOf(ExpectedChain(operation), phase);

    private static InstallerTransactionPhase[] SavedPhases(InstallerScenario scenario) => scenario.Events
        .Where(static entry => entry.StartsWith("journal.save:", StringComparison.Ordinal))
        .Select(static entry => Enum.Parse<InstallerTransactionPhase>(entry["journal.save:".Length..]))
        .ToArray();

    private static int CleanRunPointCount(InstallerOperation operation)
    {
        InstallerScenario scenario = NewScenario(operation);
        FaultPlan plan = new(() => scenario.Events.Count);
        scenario.Attach(plan);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();
        InstallerExecutionResult result = coordinator
            .ExecuteAsync(RequestFor(operation), null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (result.Outcome != InstallerExecutionOutcome.Succeeded)
        {
            throw new InvalidOperationException("The clean reference run must succeed.");
        }

        return plan.Points.Count;
    }

    private static void AssertFaultedRunIsSafe(
        InstallerScenario scenario,
        FaultPlan plan,
        InstallerExecutionResult faulted)
    {
        InstallerTransactionSnapshot? durable = scenario.Store.Current;
        if (plan.AppliedFault == InjectedFault.CancelOnly)
        {
            // The operation may legitimately finish before it observes a late cancellation.
            Assert.True(faulted.Outcome is InstallerExecutionOutcome.Cancelled or InstallerExecutionOutcome.Succeeded);
            if (faulted.Outcome == InstallerExecutionOutcome.Succeeded)
            {
                Assert.Null(durable);
            }
        }
        else
        {
            Assert.NotEqual(InstallerExecutionOutcome.Succeeded, faulted.Outcome);
            Assert.NotEqual("installer.completed", faulted.DiagnosticCode);
            string point = plan.Points[plan.FaultedPoint!.Value];
            if (point == "journal.load"
                && faulted.DiagnosticCode is "installer.transaction.reload_failed"
                    or "installer.transaction.clear_reload_failed")
            {
                // A protected-journal reload failure is classified by the coordinator itself.
                Assert.Equal(InstallerExecutionOutcome.Uncertain, faulted.Outcome);
            }
            else
            {
                Assert.Equal(plan.ExpectedDiagnosticCode, faulted.DiagnosticCode);
            }

            // Sanitized: the raw platform exception text never reaches the result.
            Assert.DoesNotContain("secret", faulted.DiagnosticCode, StringComparison.Ordinal);

            // A failure path only observes: it never performs another mutation after the fault.
            Assert.All(
                scenario.Events.Skip(plan.EventCountAtFault),
                static entry => Assert.True(
                    entry is "journal.load" or "release.dispose",
                    $"Unexpected effect '{entry}' after a failure."));
        }

        // The result never claims progress that is not durable, nor hides progress that is.
        Assert.Equal(durable is not null, faulted.RecoveryPending);
        if (faulted.RecoveryPending)
        {
            Assert.Equal(durable?.Journal.Phase, faulted.LastDurablePhase);
        }

        string[] saved = scenario.Events
            .Where(static entry => entry.StartsWith("journal.save:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(saved.Length, saved.Distinct(StringComparer.Ordinal).Count());
        int cleared = scenario.Events.IndexOf("journal.clear");
        if (cleared >= 0)
        {
            Assert.True(scenario.Events.IndexOf("journal.save:Verified") is >= 0 and var verified && verified < cleared);
        }
    }

    private static void AssertEffectsRanAtLeastOnceAndReplayedAtMostOnce(
        InstallerScenario scenario,
        InstallerOperation operation)
    {
        string[] effects =
        [
            $"certificate.apply:{operation}",
            $"package.apply:{operation}",
            $"machine.apply:{operation}",
            $"machine.commit_package:{operation}",
        ];
        foreach (string effect in effects.Where(effect => scenario.Events.Contains(effect)))
        {
            int count = scenario.Events.Count(entry => entry == effect);
            Assert.InRange(count, 1, 2);
        }

        Assert.Contains($"package.apply:{operation}", scenario.Events);
        Assert.Contains($"certificate.apply:{operation}", scenario.Events);
    }

    private sealed class RecordingProgress : IProgress<InstallerProgress>
    {
        private readonly int _throwFrom;

        internal RecordingProgress(int throwFrom) => _throwFrom = throwFrom;

        internal List<InstallerProgress> Reports { get; } = [];

        public void Report(InstallerProgress value)
        {
            Reports.Add(value);
            if (Reports.Count - 1 >= _throwFrom)
            {
                throw new InvalidOperationException("hostile-presentation-observer");
            }
        }
    }
}
