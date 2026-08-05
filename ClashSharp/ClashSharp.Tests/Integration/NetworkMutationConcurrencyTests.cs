using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies network transitions are planned, journaled, serialized, verified, and compensated.</summary>
public sealed class NetworkMutationConcurrencyTests
{
    /// <summary>Verifies a successful transition commits only its verified desired state.</summary>
    [Fact]
    public async Task ApplyAsync_Success_CapturesBaselineAndReturnsVerifiedTarget()
    {
        Fixture fixture = new();
        NetworkIntent intent = NetworkIntent.ChangeMode(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 7890);

        MutationResult<NetworkTransitionResult> result = await fixture.Network.ApplyAsync(intent, CancellationToken.None);

        Assert.Equal(MutationOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.Equal(ClashSharpMode.RuleTakeover, result.Value.Mode);
        Assert.Equal("network:RuleTakeover:False:7890", result.Value.StateHash);
        Assert.Equal("network:RuleTakeover:False:7890", fixture.Adapter.Current.StateHash);
        Assert.Equal("desired:RuleTakeover:False:7890", fixture.Committer.CurrentHash);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(
            [
                "plan:RuleTakeover:network:Disabled:False:7890",
                "validate",
                "stage:RuleTakeover",
                "apply:RuleTakeover",
                "probe:network:RuleTakeover:False:7890",
                "commit:desired:RuleTakeover:False:7890",
                "verify-commit:desired:RuleTakeover:False:7890",
                "activate:RuleTakeover",
                "probe:network:RuleTakeover:False:7890",
                "verify-commit:desired:RuleTakeover:False:7890",
                "cleanup:RuleTakeover",
            ],
            fixture.Trace);
    }

    /// <summary>Verifies an apply failure restores both external network state and durable desired state.</summary>
    [Fact]
    public async Task ApplyAsync_ApplyFails_CompensatesAndKeepsBaselineAuthoritative()
    {
        Fixture fixture = new();
        fixture.Adapter.ApplyException = new InvalidOperationException("network apply failed");

        MutationResult<NetworkTransitionResult> result = await fixture.Network.ApplyAsync(
            NetworkIntent.ChangeMode(ClashSharpMode.FullTakeover, false, 7891),
            CancellationToken.None);

        Assert.Equal(MutationOutcome.Compensated, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal("network:Disabled:False:7890", fixture.Adapter.Current.StateHash);
        Assert.Equal("baseline:Disabled:False:7890", fixture.Committer.CurrentHash);
        Assert.Contains("compensate:Disabled", fixture.Trace);
        Assert.Contains("restore-commit:baseline:Disabled:False:7890", fixture.Trace);
        Assert.Contains("verify-baseline:baseline:Disabled:False:7890", fixture.Trace);
        Assert.Null(fixture.Store.Current);
    }

    /// <summary>Verifies a second transition cannot even plan against state until the first transition releases the fair gate.</summary>
    [Fact]
    public async Task ApplyAsync_ConcurrentTransitions_PlanAndApplyInSubmissionOrder()
    {
        Fixture fixture = new();
        fixture.Adapter.FirstStageEntered = CreateSignal();
        fixture.Adapter.ReleaseFirstStage = CreateSignal();

        Task<MutationResult<NetworkTransitionResult>> firstTask = fixture.Network.ApplyAsync(
            NetworkIntent.ChangeMode(ClashSharpMode.RuleTakeover, false, 7890),
            CancellationToken.None);
        await fixture.Adapter.FirstStageEntered.Task;
        Task<MutationResult<NetworkTransitionResult>> secondTask = fixture.Network.ApplyAsync(
            NetworkIntent.ChangeMode(ClashSharpMode.FullTakeover, true, 7892),
            CancellationToken.None);

        Assert.Equal(1, fixture.Adapter.PlanCount);
        Assert.Equal(1, fixture.Gate.QueuedCount);
        fixture.Adapter.ReleaseFirstStage.SetResult(null);
        MutationResult<NetworkTransitionResult>[] results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(MutationOutcome.Succeeded, result.Outcome));
        Assert.Equal(2, fixture.Adapter.PlanCount);
        Assert.Equal(
            [ClashSharpMode.RuleTakeover, ClashSharpMode.FullTakeover],
            fixture.Adapter.PlannedModes);
        Assert.Contains("plan:FullTakeover:network:RuleTakeover:False:7890", fixture.Trace);
        Assert.Equal("network:FullTakeover:True:7892", fixture.Adapter.Current.StateHash);
    }

    /// <summary>Verifies a stale or foreign mutation context cannot invoke network planning.</summary>
    [Fact]
    public async Task PlanAsync_ForeignExpiredContext_IsRejectedBeforeAdapterAccess()
    {
        Fixture fixture = new();
        FairAsyncMutationGate foreignGate = new();
        MutationContext? foreignContext = null;
        await foreignGate.ExecuteAsync(
            Guid.NewGuid(),
            (context, _) =>
            {
                foreignContext = context;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        int planCount = fixture.Adapter.PlanCount;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Network.PlanAsync(
                foreignContext!,
                NetworkIntent.ChangeMode(ClashSharpMode.Standby, false, 7890),
                CancellationToken.None));

        Assert.Equal(planCount, fixture.Adapter.PlanCount);
    }

    /// <summary>Verifies shutdown reuses a drained lease instead of attempting nested ordinary admission.</summary>
    [Fact]
    public async Task ApplyShutdownAsync_DrainedAdmission_ExecutesOneJournaledNetworkMutation()
    {
        Fixture fixture = new();
        MutationAdmissionLease lease = await fixture.Barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None);

        MutationResult<NetworkTransitionResult> result = await fixture.Network.ApplyShutdownAsync(
            NetworkIntent.Shutdown(ClashSharpMode.Disabled, false, 7890),
            lease,
            CancellationToken.None);
        lease.CommitShutdown();
        await lease.DisposeAsync();

        Assert.Equal(MutationOutcome.Succeeded, result.Outcome);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, fixture.Barrier.State);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(1, fixture.Adapter.PlanCount);
    }

    /// <summary>Verifies an ordinary caller can reuse its admission lease without nested admission.</summary>
    [Fact]
    public async Task ApplyAdmittedAsync_OrdinaryAdmission_ExecutesOneJournaledNetworkMutation()
    {
        Fixture fixture = new();
        await using MutationAdmissionLease lease = await fixture.Barrier.AcquireOrdinaryAsync(
            CancellationToken.None);

        MutationResult<NetworkTransitionResult> result = await fixture.Network.ApplyAdmittedAsync(
            NetworkIntent.ChangeMode(ClashSharpMode.RuleTakeover, false, 7890),
            lease,
            CancellationToken.None);

        Assert.Equal(MutationOutcome.Succeeded, result.Outcome);
        Assert.Equal(MutationAdmissionState.Open, fixture.Barrier.State);
        Assert.Null(fixture.Store.Current);
        Assert.Equal(1, fixture.Adapter.PlanCount);
    }

    /// <summary>Verifies committed shutdown can become terminal while preserving its forward-recovery journal.</summary>
    [Fact]
    public async Task ApplyShutdownAsync_CommittedCleanupFails_TerminalShutdownPreservesJournal()
    {
        Fixture fixture = new();
        fixture.Adapter.CleanupException = new InvalidOperationException("cleanup failed");
        MutationAdmissionLease lease = await fixture.Barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None);

        MutationResult<NetworkTransitionResult> result = await fixture.Network.ApplyShutdownAsync(
            NetworkIntent.Shutdown(ClashSharpMode.Standby, false, 7890),
            lease,
            CancellationToken.None);
        lease.CommitShutdown();
        await lease.DisposeAsync();

        Assert.Equal(MutationOutcome.CommittedRecoveryRequired, result.Outcome);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, fixture.Barrier.State);
        Assert.NotNull(fixture.Store.Current);
        Assert.True(fixture.Store.Current.Journal.HasCommitMarker);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Adapter = new FakeNetworkStateAdapter(Trace);
            Committer = new FakeNetworkStateCommitter(Trace);
            Store = new MemoryMutationJournalStore();
            NetworkMutationRecoveryPlanResolver resolver = new(Adapter, Committer);
            Mutations = new ApplicationMutationCoordinator(
                Barrier,
                Gate,
                Store,
                resolver,
                new MutationDeadlines(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
            Network = new NetworkStateCoordinator(Mutations, Adapter, Committer);
        }

        public List<string> Trace { get; } = [];

        public MutationAdmissionBarrier Barrier { get; } = new();

        public FairAsyncMutationGate Gate { get; } = new();

        public MemoryMutationJournalStore Store { get; }

        public FakeNetworkStateAdapter Adapter { get; }

        public FakeNetworkStateCommitter Committer { get; }

        public ApplicationMutationCoordinator Mutations { get; }

        public NetworkStateCoordinator Network { get; }
    }

    private sealed class FakeNetworkStateAdapter(List<string> trace) : INetworkStateAdapter
    {
        private NetworkPlan? _lastPlan;
        private int _stageCount;

        public NetworkStateSnapshot Current { get; private set; } = CreateState(ClashSharpMode.Disabled, false, 7890);

        public Exception? ApplyException { get; set; }

        public Exception? CleanupException { get; set; }

        public TaskCompletionSource<object?>? FirstStageEntered { get; set; }

        public TaskCompletionSource<object?>? ReleaseFirstStage { get; set; }

        public int PlanCount { get; private set; }

        public List<ClashSharpMode> PlannedModes { get; } = [];

        public Task<NetworkPlan> PlanAsync(NetworkIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanCount++;
            PlannedModes.Add(intent.Mode);
            trace.Add($"plan:{intent.Mode}:{Current.StateHash}");
            NetworkStateSnapshot desired = CreateState(intent.Mode, intent.TransparentProxyEnabled, intent.MixedPort);
            _lastPlan = new NetworkPlan(
                intent,
                Current,
                desired,
                $"baseline:{Current.Mode}:{Current.TransparentProxyEnabled}:{Current.MixedPort}",
                $"desired:{intent.Mode}:{intent.TransparentProxyEnabled}:{intent.MixedPort}",
                $"restore:{Current.StateHash}");
            return Task.FromResult(_lastPlan);
        }

        public Task<NetworkPlan> RestorePlanAsync(MutationJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_lastPlan ?? throw new InvalidOperationException("No network plan is available."));
        }

        public Task ValidateAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("validate");
            return Task.CompletedTask;
        }

        public async Task StageAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"stage:{plan.Intent.Mode}");
            if (Interlocked.Increment(ref _stageCount) == 1 && FirstStageEntered is not null && ReleaseFirstStage is not null)
            {
                FirstStageEntered.SetResult(null);
                await ReleaseFirstStage.Task.WaitAsync(cancellationToken);
            }
        }

        public Task ApplyAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"apply:{plan.Intent.Mode}");
            if (ApplyException is not null)
            {
                Current = plan.Desired with { StateHash = "network:partial" };
                return Task.FromException(ApplyException);
            }

            Current = plan.Desired;
            return Task.CompletedTask;
        }

        public Task<NetworkStateSnapshot> ProbeAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"probe:{Current.StateHash}");
            return Task.FromResult(Current);
        }

        public Task CompensateAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"compensate:{plan.Baseline.Mode}");
            Current = plan.Baseline;
            return Task.CompletedTask;
        }

        public Task ActivateAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"activate:{plan.Intent.Mode}");
            return Task.CompletedTask;
        }

        public Task CleanupAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"cleanup:{plan.Intent.Mode}");
            return CleanupException is null
                ? Task.CompletedTask
                : Task.FromException(CleanupException);
        }

        private static NetworkStateSnapshot CreateState(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort)
        {
            bool coreRunning = mode != ClashSharpMode.Disabled;
            bool systemProxyEnabled = mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover
                && !transparentProxyEnabled;
            return new NetworkStateSnapshot(
                mode,
                coreRunning,
                systemProxyEnabled,
                transparentProxyEnabled,
                mixedPort,
                $"network:{mode}:{transparentProxyEnabled}:{mixedPort}");
        }
    }

    private sealed class FakeNetworkStateCommitter(List<string> trace) : INetworkStateCommitter
    {
        public string CurrentHash { get; private set; } = "baseline:Disabled:False:7890";

        public Task PromoteDesiredAsync(
            NetworkPlan plan,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(admissionLease);
            cancellationToken.ThrowIfCancellationRequested();
            CurrentHash = plan.DesiredHash;
            trace.Add($"commit:{CurrentHash}");
            return Task.CompletedTask;
        }

        public Task RestoreBaselineAsync(
            NetworkPlan plan,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(admissionLease);
            cancellationToken.ThrowIfCancellationRequested();
            CurrentHash = plan.BaselineHash;
            trace.Add($"restore-commit:{CurrentHash}");
            return Task.CompletedTask;
        }

        public Task VerifyDesiredAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"verify-commit:{CurrentHash}");
            return CurrentHash == plan.DesiredHash
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("desired durable state not observed"));
        }

        public Task VerifyBaselineAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add($"verify-baseline:{CurrentHash}");
            return CurrentHash == plan.BaselineHash
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("baseline durable state not observed"));
        }
    }

    private sealed class MemoryMutationJournalStore : IMutationJournalStore
    {
        public MutationJournalSnapshot? Current { get; private set; }

        public Task<MutationJournalSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task<MutationJournalSnapshot> SaveAsync(
            MutationJournal journal,
            string? expectedCurrentHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Current?.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new MutationJournalStoreException(MutationJournalStoreError.ConcurrencyConflict, "hash mismatch");
            }

            Current = new MutationJournalSnapshot(journal, $"hash-{journal.Generation}");
            return Task.FromResult(Current);
        }

        public Task DeleteAsync(Guid operationId, string expectedCurrentHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(operationId, Current!.Journal.OperationId);
            Assert.Equal(expectedCurrentHash, Current.ContentHash);
            Current = null;
            return Task.CompletedTask;
        }
    }

    private static TaskCompletionSource<object?> CreateSignal()
    {
        return new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
