using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// Owner transfer: fail, cancel or lose an acknowledgement at every native step and every private-journal
/// read, write and delete; durable evidence must stay valid and monotone and a restart must converge.
/// </summary>
public sealed class InstallerOwnerTransferInterruptionTests
{
    private static readonly InjectedFault[] StepFaults =
        [InjectedFault.Protocol, InjectedFault.StateUncertain, InjectedFault.CancelAndThrow, InjectedFault.CancelOnly];

    private static readonly InjectedFault[] StorageFaults =
        [InjectedFault.Io, InjectedFault.StateUncertain, InjectedFault.CancelAndThrow, InjectedFault.Protocol, InjectedFault.CancelOnly];

    public static TheoryData<InstallerOwnerTransferPhase, int, InjectedFault> Cases()
    {
        var data = new TheoryData<InstallerOwnerTransferPhase, int, InjectedFault>();
        foreach (InstallerOwnerTransferPhase initial in Enum.GetValues<InstallerOwnerTransferPhase>())
        {
            World clean = new(initial, plan: null);
            clean.RunToCompletionAsync().GetAwaiter().GetResult();
            for (int point = 0; point < clean.Plan.Points.Count; point++)
            {
                bool storage = clean.Plan.Points[point].StartsWith("persistence.", StringComparison.Ordinal);
                foreach (InjectedFault fault in storage ? StorageFaults : StepFaults)
                {
                    data.Add(initial, point, fault);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FaultAtAnyStepOrStorageBoundaryKeepsEvidenceValidAndARestartConverges(
        InstallerOwnerTransferPhase initial,
        int point,
        InjectedFault fault)
    {
        World world = new(initial, plan: (index, _) => index == point ? fault : null);
        InstallerOwnerTransferPhase before = world.PersistedPhase!.Value;

        Exception? failure = await Record.ExceptionAsync(() => world.RunAsync(world.Cancellation.Token));

        Assert.True(
            failure is null or InstallerProtocolException or InstallerStateUncertainException or OperationCanceledException,
            $"Undeclared {failure?.GetType().FullName}: {failure?.Message}");
        Assert.NotNull(world.Plan.FaultedPoint);
        // Whatever happened, the private evidence is either gone (after a Verified clear) or valid and monotone.
        InstallerOwnerTransferSnapshot? durable = await world.LoadFreshAsync();
        if (durable is null)
        {
            Assert.Equal(InstallerOwnerTransferPhase.Verified, world.LastPhaseSeenBeforeDelete);
        }
        else
        {
            Assert.True(durable.Journal.HasSameIdentity(world.Original));
            Assert.True(durable.Journal.Phase >= before);
        }

        // Native steps only ever ran for the immediate successor of the durable phase.
        Assert.All(world.StepViolations, violation => Assert.Fail(violation));

        // A restart (fresh objects, same files) always completes and hands back the pending continuation.
        world.DisarmFaults();
        InstallerTransactionSnapshot continuation = durable is null
            ? InstallerTransactionSnapshot.Create(world.Original.Continuation)
            : await world.RestartAsync();

        Assert.Equal(InstallerTransactionSnapshot.Create(world.Original.Continuation), continuation);
        Assert.Null(await world.LoadFreshAsync());
        Assert.All(world.MutationCounts.Values, count => Assert.InRange(count, 1, 2));
        Assert.Equal(InstallerTransactionPhase.Prepared, continuation.Journal.Phase);
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.StartupBlocked)]
    [InlineData(InstallerOwnerTransferPhase.PreviousServiceRemoved)]
    [InlineData(InstallerOwnerTransferPhase.MachineAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.AssociationTransferred)]
    [InlineData(InstallerOwnerTransferPhase.CertificateStateTransferred)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.Verified)]
    public async Task ResumingARunningTransferAndAChangedOrMissingJournalAreRefusedWithoutMutation(
        InstallerOwnerTransferPhase initial)
    {
        World world = new(initial, plan: null);
        InstallerOwnerTransferSnapshot expected = (await world.LoadFreshAsync())!;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Gate = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        Task<InstallerTransactionSnapshot> running = world.RunAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TestTimeouts.Completion);

        InstallerProtocolException concurrent = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            world.Coordinator.ResumeAsync(expected, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.concurrent_execution", concurrent.DiagnosticCode);
        release.SetResult();
        await running;

        // Journal gone: a second resume of the same snapshot is refused and mutates nothing.
        int applied = world.MutationCounts.Values.Sum();
        InstallerProtocolException missing = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            world.RestartAsync(expected));
        Assert.Equal("installer.owner_transfer.state_missing", missing.DiagnosticCode);
        Assert.Equal(applied, world.MutationCounts.Values.Sum());
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.AssociationTransferred)]
    public async Task AJournalThatChangedSinceInspectionIsRefusedBeforeAnyNativeWork(InstallerOwnerTransferPhase initial)
    {
        World world = new(initial, plan: null);
        InstallerOwnerTransferSnapshot inspected = (await world.LoadFreshAsync())!;
        InstallerOwnerTransferSnapshot advanced = inspected.Journal.Phase == InstallerOwnerTransferPhase.Verified
            ? inspected
            : await world.Store.SaveAsync(
                inspected.Journal.TransitionTo(inspected.Journal.Phase + 1), inspected.ContentHash, CancellationToken.None);

        InstallerProtocolException changed = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            world.RestartAsync(inspected));

        Assert.Equal("installer.owner_transfer.state_changed", changed.DiagnosticCode);
        Assert.Empty(world.MutationCounts);
        Assert.Equal(advanced, await world.LoadFreshAsync());
    }

    private sealed class World
    {
        private readonly FakePersistence _persistence;
        private readonly FakeExecutor _executor;
        private Func<int, string, InjectedFault?>? _decide;
        private readonly InstallerOwnerTransferPhase _initial;

        internal World(InstallerOwnerTransferPhase initial, Func<int, string, InjectedFault?>? plan)
        {
            _initial = initial;
            _decide = plan;
            Plan = new FaultPlan(() => 0, (index, name) => _decide?.Invoke(index, name));
            _persistence = new FakePersistence(this);
            _executor = new FakeExecutor(this);
            Original = InstallerOwnerTransferJournalTests.AtPhase(initial);
            _persistence.Seed(InstallerOwnerTransferCodec.Serialize(Original));
            Store = new InstallerOwnerTransferStore(_persistence);
            Coordinator = new InstallerOwnerTransferCoordinator(Store, _executor);
        }

        internal FaultPlan Plan { get; }

        internal InstallerOwnerTransferJournal Original { get; }

        internal InstallerOwnerTransferStore Store { get; private set; }

        internal InstallerOwnerTransferCoordinator Coordinator { get; private set; }

        internal CancellationTokenSource Cancellation => Plan.Cancellation;

        internal Func<Task>? Gate { get; set; }

        internal List<string> StepViolations { get; } = [];

        internal Dictionary<InstallerOwnerTransferPhase, int> MutationCounts { get; } = [];

        internal InstallerOwnerTransferPhase? LastPhaseSeenBeforeDelete { get; set; }

        internal InstallerOwnerTransferPhase? PersistedPhase => _persistence.Content is { } bytes
            ? InstallerOwnerTransferCodec.Parse(bytes).Phase
            : null;

        internal void DisarmFaults() => _decide = null;

        internal async Task<InstallerOwnerTransferSnapshot?> LoadFreshAsync() =>
            await new InstallerOwnerTransferStore(_persistence.Unfaulted()).LoadAsync(CancellationToken.None);

        internal Task<InstallerTransactionSnapshot> RunAsync(CancellationToken cancellationToken) =>
            RunWithAsync(Coordinator, expected: null, cancellationToken);

        internal Task<InstallerTransactionSnapshot> RestartAsync(InstallerOwnerTransferSnapshot? expected = null)
        {
            Store = new InstallerOwnerTransferStore(_persistence);
            Coordinator = new InstallerOwnerTransferCoordinator(Store, _executor);
            return RunWithAsync(Coordinator, expected, CancellationToken.None);
        }

        internal async Task RunToCompletionAsync() => _ = await RunAsync(CancellationToken.None);

        private async Task<InstallerTransactionSnapshot> RunWithAsync(
            InstallerOwnerTransferCoordinator coordinator,
            InstallerOwnerTransferSnapshot? expected,
            CancellationToken cancellationToken)
        {
            InstallerOwnerTransferSnapshot snapshot = expected
                ?? await new InstallerOwnerTransferStore(_persistence.Unfaulted()).LoadAsync(CancellationToken.None)
                ?? throw new InstallerProtocolException("installer.owner_transfer.state_missing");
            return await coordinator.ResumeAsync(snapshot, cancellationToken);
        }
    }

    private sealed class FakePersistence(World world) : IInstallerOwnerTransferPersistence
    {
        private byte[]? _content;

        internal byte[]? Content => _content;

        internal void Seed(byte[] bytes) => _content = bytes;

        /// <summary>A view over the same bytes whose operations never inject faults or record points.</summary>
        internal IInstallerOwnerTransferPersistence Unfaulted() => new PlainView(this);

        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            if (world.Gate is { } gate)
            {
                await gate();
            }

            await world.Plan.Hit("persistence.read", cancellationToken);
            return _content is null ? null : (byte[])_content.Clone();
        }

        public async Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            await world.Plan.Hit("persistence.write.before", cancellationToken);
            _content = bytes.ToArray();
            await world.Plan.Hit("persistence.write.after", cancellationToken);
        }

        public async Task DeleteAsync(CancellationToken cancellationToken)
        {
            await world.Plan.Hit("persistence.delete.before", cancellationToken);
            // Deletion is only legal for a Verified journal; anything else is a contract violation.
            world.LastPhaseSeenBeforeDelete = _content is null
                ? null
                : InstallerOwnerTransferCodec.Parse(_content).Phase;
            Assert.Equal(InstallerOwnerTransferPhase.Verified, world.LastPhaseSeenBeforeDelete);
            _content = null;
            await world.Plan.Hit("persistence.delete.after", cancellationToken);
        }

        private sealed class PlainView(FakePersistence owner) : IInstallerOwnerTransferPersistence
        {
            public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) =>
                Task.FromResult(owner._content is null ? null : (byte[]?)owner._content.Clone());

            public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }

    private sealed class FakeExecutor(World world) : IInstallerOwnerTransferPhaseExecutor
    {
        public async Task ApplyAndVerifyAsync(
            InstallerOwnerTransferJournal current,
            InstallerOwnerTransferPhase nextPhase,
            CancellationToken cancellationToken)
        {
            if (nextPhase != current.Phase + 1)
            {
                world.StepViolations.Add($"Step {nextPhase} was requested from {current.Phase}.");
            }

            if (nextPhase is < InstallerOwnerTransferPhase.StartupBlocked or > InstallerOwnerTransferPhase.InstallerAccessTransferred)
            {
                world.StepViolations.Add($"Phase {nextPhase} is not a native mutation boundary.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await world.Plan.Hit($"step.{nextPhase}.before", cancellationToken);
            world.MutationCounts[nextPhase] = world.MutationCounts.GetValueOrDefault(nextPhase) + 1;
            await world.Plan.Hit($"step.{nextPhase}.after", cancellationToken);
        }

        public async Task VerifyCompletedAsync(InstallerOwnerTransferJournal current, CancellationToken cancellationToken)
        {
            if (current.Phase is not (InstallerOwnerTransferPhase.InstallerAccessTransferred or InstallerOwnerTransferPhase.Verified))
            {
                world.StepViolations.Add($"Completion was verified at {current.Phase}.");
            }

            await world.Plan.Hit("verify", cancellationToken);
        }
    }
}
