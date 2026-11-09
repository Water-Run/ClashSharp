using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Injects transient faults at every effect boundary (and at every pair of boundaries) of the settings import and
/// reset coordinators. The checked contract: the retained receipt reaches exactly one decision, that decision matches
/// the durable authority, runtime participants converge to the durable authority, and unrecoverable states are
/// reported with the typed recovery exception instead of being masked.
/// </summary>
public sealed class SettingsTransactionFaultMatrixTests
{
    private static readonly SettingsRuntimeSnapshot Baseline = new(
        AppLanguage.English, AppThemeMode.Dark, AppAccentColorMode.Custom, "#FF123456",
        true, false, 30, ClashSharpMode.RuleTakeover, "baseline-profile", false, 7890);

    private static readonly SettingsRuntimeSnapshot Target = new(
        AppLanguage.AutoDetect, AppThemeMode.FollowSystem, AppAccentColorMode.FollowSystem, "#FF0078D4",
        false, true, 10, ClashSharpMode.Disabled, string.Empty, true, 10000);

    private static readonly string[] AllParticipants = ["language", "theme", "accent", "startup", "sampling", "network"];

    /// <summary>Verifies the clean import trace and that every single fault satisfies the transaction contract.</summary>
    [Fact]
    public async Task Import_SingleFaultAtEveryEffectBoundary_KeepsTheTransactionContract()
    {
        World clean = new();
        await RunImportAsync(clean);
        Assert.Equal(
            ["begin", "invalidate", .. AllParticipants, "commit", "dispose"],
            clean.EffectTrace);
        Assert.Equal(Target, clean.Durable);
        Assert.All(AllParticipants, participant => Assert.Equal("target", clean.Applied[participant]));

        for (int fault = 1; fault <= clean.EffectCount; fault++)
        {
            foreach (Func<Exception> kind in FaultKinds)
            {
                World world = new() { Faults = [fault] };
                Exception injected = kind();
                world.FaultFactory = () => injected;

                Exception? failure = await Capture(() => RunImportAsync(world));

                string stage = clean.EffectTrace[fault - 1];
                AssertContract(world, failure, scopeParticipants: AllParticipants);
                switch (stage)
                {
                    case "commit":
                        Assert.Null(failure);
                        Assert.Equal(2, world.CommitCalls);
                        break;
                    case "dispose":
                        Assert.Same(injected, failure);
                        Assert.Equal(Target, world.Durable);
                        break;
                    case "begin":
                        Assert.Same(injected, failure);
                        Assert.Equal(Baseline, world.Durable);
                        Assert.Equal(0, world.DisposeCalls);
                        break;
                    default:
                        Assert.Same(injected, failure);
                        Assert.Equal(Baseline, world.Durable);
                        Assert.Equal(1, world.RollbackCalls);
                        Assert.All(AllParticipants, participant => Assert.Equal("baseline", world.Applied[participant]));
                        break;
                }
            }
        }
    }

    /// <summary>Verifies every ordered pair of faults across the forward and recovery paths never yields silent divergence.</summary>
    [Fact]
    public async Task Import_PairedFaultsAcrossForwardAndRecoveryPaths_NeverDivergeSilently()
    {
        World clean = new();
        await RunImportAsync(clean);
        int forward = clean.EffectCount;
        int total = forward + 1 + 2 + AllParticipants.Length + 2;
        int recoveryReports = 0;
        int successes = 0;

        for (int first = 1; first <= forward; first++)
        {
            for (int second = first + 1; second <= total; second++)
            {
                World world = new() { Faults = [first, second] };
                Exception injected = new IOException("pair");
                world.FaultFactory = () => injected;

                Exception? failure = await Capture(() => RunImportAsync(world));

                AssertContract(world, failure, AllParticipants);
                recoveryReports += failure is SettingsImportRecoveryException ? 1 : 0;
                successes += failure is null ? 1 : 0;
            }
        }

        Assert.True(recoveryReports > 0, "faults during recovery must be reported with the typed recovery exception");
        Assert.True(successes > 0, "a retried commit tolerates one fault");
    }

    /// <summary>Verifies cancelling the caller token at any boundary cannot interrupt the retained decision.</summary>
    [Fact]
    public async Task Import_CancellationAtEveryBoundary_StillReachesAConsistentDecision()
    {
        World clean = new();
        await RunImportAsync(clean);

        for (int cancelAt = 1; cancelAt <= clean.EffectCount; cancelAt++)
        {
            using CancellationTokenSource cancellation = new();
            World world = new() { CancelAt = cancelAt, Cancellation = cancellation };

            Exception? failure = await Capture(() => new SettingsImportCoordinator().ExecuteAsync(world, "package.zip", cancellation.Token));

            AssertContract(world, failure, AllParticipants);
            if (failure is null)
            {
                Assert.Equal(Target, world.Durable);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
                Assert.Equal(Baseline, world.Durable);
            }
        }
    }

    /// <summary>Verifies the clean reset trace of every accepted scope and the single-fault contract for each.</summary>
    /// <param name="scope">Reset scope.</param>
    [Theory]
    [InlineData(SettingsResetScope.All)]
    [InlineData(SettingsResetScope.Startup)]
    [InlineData(SettingsResetScope.Proxy)]
    [InlineData(SettingsResetScope.TransparentProxy)]
    public async Task Reset_SingleFaultAtEveryEffectBoundary_KeepsTheTransactionContract(SettingsResetScope scope)
    {
        World clean = new();
        await RunResetAsync(clean, scope);
        string[] participants = ParticipantsOf(scope);
        Assert.Equal(["begin", .. participants, "report", "commit", "dispose"], clean.EffectTrace);

        for (int fault = 1; fault <= clean.EffectCount; fault++)
        {
            foreach (Func<Exception> kind in FaultKinds)
            {
                World world = new() { Faults = [fault] };
                Exception injected = kind();
                world.FaultFactory = () => injected;

                Exception? failure = await Capture(() => RunResetAsync(world, scope));

                string stage = clean.EffectTrace[fault - 1];
                AssertContract(world, failure, participants);
                switch (stage)
                {
                    case "commit":
                        Assert.Null(failure);
                        Assert.Equal(2, world.CommitCalls);
                        Assert.Equal(Target, world.Durable);
                        break;
                    case "report":
                        Assert.Same(injected, failure);
                        Assert.Equal(Target, world.Durable);
                        Assert.Equal(1, world.CommitCalls);
                        break;
                    case "dispose":
                        Assert.Same(injected, failure);
                        Assert.Equal(Target, world.Durable);
                        break;
                    case "begin":
                        Assert.Same(injected, failure);
                        Assert.Equal(Baseline, world.Durable);
                        Assert.Equal(0, world.DisposeCalls);
                        Assert.Equal(
                            (Baseline, true),
                            Assert.Single(world.Published));
                        break;
                    default:
                        Assert.Same(injected, failure);
                        Assert.Equal(Baseline, world.Durable);
                        Assert.Equal(1, world.RollbackCalls);
                        Assert.Equal(Baseline, world.Published[^1].Snapshot);
                        Assert.True(world.Published[^1].Failed);
                        break;
                }
            }
        }
    }

    /// <summary>Verifies every pair of faults for the full-scope reset is either converged or reported as recovery-required.</summary>
    [Fact]
    public async Task Reset_PairedFaultsAcrossForwardAndRecoveryPaths_NeverDivergeSilently()
    {
        World clean = new();
        await RunResetAsync(clean, SettingsResetScope.All);
        int forward = clean.EffectCount;
        int total = forward + 3 + AllParticipants.Length + 3;
        int recoveryReports = 0;

        for (int first = 1; first <= forward; first++)
        {
            for (int second = first + 1; second <= total; second++)
            {
                World world = new() { Faults = [first, second] };
                Exception injected = new IOException("pair");
                world.FaultFactory = () => injected;

                Exception? failure = await Capture(() => RunResetAsync(world, SettingsResetScope.All));

                AssertContract(world, failure, AllParticipants);
                recoveryReports += failure is SettingsResetRecoveryException ? 1 : 0;
            }
        }

        Assert.True(recoveryReports > 0);
    }

    /// <summary>Verifies every declared reset scope value and combination is accepted or rejected before any effect.</summary>
    [Fact]
    public async Task Reset_EveryDeclaredScopeIsAcceptedOrRejectedWithoutSideEffects()
    {
        SettingsResetScope[] accepted =
        [
            SettingsResetScope.All, SettingsResetScope.Startup, SettingsResetScope.Proxy, SettingsResetScope.TransparentProxy,
        ];
        List<SettingsResetScope> universe = [.. Enum.GetValues<SettingsResetScope>(), SettingsResetScope.Startup | SettingsResetScope.Proxy,
            SettingsResetScope.Proxy | SettingsResetScope.TransparentProxy, (SettingsResetScope)(-1), (SettingsResetScope)(1 << 20)];

        foreach (SettingsResetScope scope in universe)
        {
            World world = new();
            Exception? failure = await Capture(() => RunResetAsync(world, scope));

            if (accepted.Contains(scope))
            {
                Assert.Null(failure);
                continue;
            }

            Assert.IsType<ArgumentOutOfRangeException>(failure);
            Assert.Equal(0, world.EffectCount);
            Assert.Equal(Baseline, world.Durable);
        }
    }

    /// <summary>Verifies an unreadable authority during a partially failed begin surfaces recovery instead of a masked failure.</summary>
    [Fact]
    public async Task Reset_PartialBeginMutation_ConvergesRuntimeToTheRemainingAuthority()
    {
        World world = new() { Faults = [1], MutateBeforeBeginFault = true };
        Exception injected = new IOException("begin");
        world.FaultFactory = () => injected;

        Exception? failure = await Capture(() => RunResetAsync(world, SettingsResetScope.All));

        Assert.Same(injected, failure);
        Assert.Equal(Target, world.Durable);
        Assert.All(AllParticipants, participant => Assert.Equal("target", world.Applied[participant]));
        Assert.Equal((Target, true), Assert.Single(world.Published));
    }

    /// <summary>Verifies the export coordinator holds exclusive admission for exactly the export and always reopens it.</summary>
    [Fact]
    public async Task Export_HoldsExclusiveAdmissionOnlyForTheExportAndAlwaysReopens()
    {
        MutationAdmissionBarrier barrier = new();
        SettingsExportCoordinator coordinator = new(barrier);
        MutationAdmissionState? during = null;
        bool ordinaryRejected = false;
        bool leaseExclusive = false;

        await coordinator.ExecuteAsync(
            (lease, _) =>
            {
                during = barrier.State;
                leaseExclusive = lease.IsExclusive;
                ordinaryRejected = Assert.Throws<MutationAdmissionRejectedException>(() => barrier.AcquireOrdinary()) is not null;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(MutationAdmissionState.Closing, during);
        Assert.True(leaseExclusive);
        Assert.True(ordinaryRejected);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(_ => throw new InvalidOperationException("export failed"), CancellationToken.None));
        Assert.Equal("export failed", failure.Message);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
        using MutationAdmissionLease ordinary = barrier.AcquireOrdinary();
        Assert.False(ordinary.IsExclusive);
    }

    /// <summary>Verifies an export waiting for ordinary work can be cancelled without leaving admission closed.</summary>
    [Fact]
    public async Task Export_CancelledWhileDraining_ReopensAdmissionAndNeverRunsTheExport()
    {
        MutationAdmissionBarrier barrier = new();
        SettingsExportCoordinator coordinator = new(barrier);
        using MutationAdmissionLease ordinary = barrier.AcquireOrdinary();
        using CancellationTokenSource cancellation = new();
        bool ran = false;

        Task export = coordinator.ExecuteAsync(
            _ =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            cancellation.Token);
        Assert.Equal(MutationAdmissionState.Closing, barrier.State);
        Assert.True(ordinary.RevocationToken.IsCancellationRequested);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.False(ran);
        Assert.Equal(MutationAdmissionState.Open, barrier.State);
    }

    /// <summary>Verifies an already cancelled export never touches admission at all.</summary>
    [Fact]
    public async Task Export_AlreadyCancelled_DoesNotCloseAdmission()
    {
        MutationAdmissionBarrier barrier = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SettingsExportCoordinator(barrier).ExecuteAsync(_ => Task.CompletedTask, cancellation.Token));

        Assert.Equal(MutationAdmissionState.Open, barrier.State);
    }

    private static readonly Func<Exception>[] FaultKinds =
    [
        () => new IOException("injected io"),
        () => new InvalidOperationException("injected state"),
        () => new OperationCanceledException("injected cancellation"),
    ];

    private static string[] ParticipantsOf(SettingsResetScope scope) => scope switch
    {
        SettingsResetScope.All => AllParticipants,
        SettingsResetScope.Startup => ["startup"],
        SettingsResetScope.Proxy => ["sampling", "network"],
        SettingsResetScope.TransparentProxy => ["network"],
        _ => throw new NotSupportedException(scope.ToString()),
    };

    private static Task RunImportAsync(World world) =>
        new SettingsImportCoordinator().ExecuteAsync(world, "package.zip", CancellationToken.None);

    private static Task RunResetAsync(World world, SettingsResetScope scope) =>
        new SettingsResetCoordinator().ExecuteAsync(world, scope, transparentProxyEnabled: true, CancellationToken.None);

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertContract(World world, Exception? failure, string[] scopeParticipants)
    {
        string trace = string.Join(",", world.EffectTrace);
        Assert.True(world.CommitDone <= 1 && world.RollbackDone <= 1, $"a decision may complete once: {trace}");
        Assert.False(world.CommitDone == 1 && world.RollbackDone == 1, $"commit and rollback both completed: {trace}");
        Assert.True(world.DisposeCalls <= 1, $"receipt disposed more than once: {trace}");
        if (world.ReceiptIssued)
        {
            Assert.Equal(1, world.DisposeCalls);
        }

        if (world.CommitDone == 1)
        {
            Assert.Equal(Target, world.Durable);
        }

        if (world.RollbackDone == 1)
        {
            Assert.Equal(Baseline, world.Durable);
        }

        if (failure is null)
        {
            Assert.Equal(1, world.CommitDone);
            Assert.Equal(Target, world.Durable);
            Assert.All(scopeParticipants, participant => Assert.Equal("target", world.Applied[participant]));
            return;
        }

        bool unrecoverableReport = failure is SettingsImportRecoveryException or SettingsResetRecoveryException
            or AggregateException;
        if (!unrecoverableReport)
        {
            string authority = world.Durable == Target ? "target" : "baseline";
            Assert.All(scopeParticipants, participant => Assert.Equal(authority, world.Applied[participant]));
            if (world.ReceiptIssued)
            {
                Assert.Equal(1, world.CommitDone + world.RollbackDone);
            }
        }
    }

    private sealed class World : ISettingsImportOperation, ISettingsResetOperation, IRetainedSettingsResetReceipt
    {
        private int _effects;

        public SettingsRuntimeSnapshot Durable { get; private set; } = Baseline;

        public Dictionary<string, string> Applied { get; } = AllParticipants.ToDictionary(static name => name, static _ => "baseline");

        public List<string> EffectTrace { get; } = [];

        public HashSet<int> Faults { get; init; } = [];

        public Func<Exception> FaultFactory { get; set; } = static () => new IOException("fault");

        public bool MutateBeforeBeginFault { get; init; }

        public int CancelAt { get; init; }

        public CancellationTokenSource? Cancellation { get; init; }

        public bool ReceiptIssued { get; private set; }

        public int CommitCalls { get; private set; }

        public int CommitDone { get; private set; }

        public int RollbackCalls { get; private set; }

        public int RollbackDone { get; private set; }

        public int DisposeCalls { get; private set; }

        public List<(SettingsRuntimeSnapshot Snapshot, bool Failed)> Published { get; } = [];

        public int EffectCount => _effects;

        public SettingsRuntimeSnapshot CaptureSnapshot() => Durable;

        public Task<IRetainedSettingsTransactionReceipt> BeginImportAsync(string packagePath, CancellationToken cancellationToken)
        {
            Effect("begin");
            cancellationToken.ThrowIfCancellationRequested();
            Durable = Target;
            ReceiptIssued = true;
            return Task.FromResult<IRetainedSettingsTransactionReceipt>(this);
        }

        public IRetainedSettingsResetReceipt BeginReset(SettingsResetScope scope, bool transparentProxyEnabled)
        {
            if (MutateBeforeBeginFault)
            {
                Durable = Target;
            }

            Effect("begin");
            Durable = Target;
            ReceiptIssued = true;
            return this;
        }

        public void InvalidateProfiles() => Effect("invalidate");

        public Task RestoreDurableSnapshotAsync(SettingsRuntimeSnapshot snapshot, CancellationToken cancellationToken)
        {
            Effect("restore");
            Durable = snapshot;
            return Task.CompletedTask;
        }

        public void ReportApplied(SettingsRuntimeSnapshot snapshot, SettingsResetScope scope, bool operationFailed)
        {
            Effect("report");
            Published.Add((snapshot, operationFailed));
        }

        public void ApplyLanguage(AppLanguage language) => Apply("language", language == Target.DisplayLanguage);

        public void ApplyTheme(AppThemeMode theme) => Apply("theme", theme == Target.AppThemeMode);

        public void ApplyAccentColor(AppAccentColorMode mode, string value) => Apply("accent", value == Target.AppAccentColorValue);

        public Task ApplyLaunchAtStartupAsync(bool enabled, CancellationToken cancellationToken) =>
            ApplyAsync("startup", enabled == Target.LaunchAtStartupEnabled);

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken) =>
            ApplyAsync("sampling", Durable == Target);

        public Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken) =>
            ApplyAsync("network", mixedPort == Target.MixedPort);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            CommitCalls++;
            Effect("commit");
            CommitDone++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            RollbackCalls++;
            Effect("rollback");
            Durable = Baseline;
            RollbackDone++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            Effect("dispose");
            return ValueTask.CompletedTask;
        }

        private void Apply(string participant, bool target)
        {
            Effect(participant);
            Applied[participant] = target ? "target" : "baseline";
        }

        private async Task ApplyAsync(string participant, bool target)
        {
            await Task.Yield();
            Apply(participant, target);
        }

        private void Effect(string name)
        {
            _effects++;
            EffectTrace.Add(name);
            if (CancelAt == _effects)
            {
                Cancellation!.Cancel();
            }

            if (Faults.Contains(_effects))
            {
                throw FaultFactory();
            }
        }
    }
}
