using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Exercises complete preference batches, owned lifecycle work, and verified recovery boundaries.</summary>
public sealed class ConnectionSamplingSettingsCoordinatorTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(301)]
    public void InvalidInterval_IsRejectedBeforeAnOperationExists(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionSamplingSettings(true, seconds));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Apply_QuiescesBeforePublishingTheCompletePair(bool enabled, bool running)
    {
        Probe probe = new() { Settings = new(enabled, 30), Running = running };
        ConnectionSamplingSettings target = new(!enabled, 60);
        ConnectionSamplingSettingsCoordinator coordinator = Create(probe);
        Assert.Empty(probe.Calls);

        await coordinator.ApplyAsync(target, CancellationToken.None);

        Assert.Equal(target, probe.Settings);
        Assert.Equal(target.Enabled, probe.Running);
        Assert.Equal(target, Assert.Single(probe.Written));
        Assert.All(probe.Tokens, token => Assert.False(token.CanBeCanceled));
        Assert.True(probe.Calls.IndexOf("quiesce") < probe.Calls.IndexOf("write"));
        if (target.Enabled)
        {
            Assert.True(probe.Calls.IndexOf("write") < probe.Calls.IndexOf("start"));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PartialActivationFailure_RestoresPreferenceAndActualRunningBaseline(bool enabled, bool running)
    {
        IOException failure = new("partially started");
        Probe probe = new() { Settings = new(enabled, 30), Running = running };
        probe.Starting = call => call == 1 ? Task.FromException(failure) : Task.CompletedTask;

        Assert.Same(failure, await Record.ExceptionAsync(() => Create(probe).ApplyAsync(new(true, 60), CancellationToken.None)));

        Assert.Equal(new(enabled, 30), probe.Settings);
        Assert.Equal(running, probe.Running);
        Assert.Equal(2, probe.Quiesces);
        Assert.Equal(running ? 2 : 1, probe.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFailure_RestoresBothPreferencesBeforeRestarting(bool afterPublication)
    {
        IOException failure = new("batch failed");
        Probe probe = new();
        probe.Writing = (call, settings) =>
        {
            if (afterPublication || call != 1)
            {
                probe.Settings = settings;
            }

            if (call == 1)
            {
                throw failure;
            }
        };

        Assert.Same(failure, await Record.ExceptionAsync(() => Create(probe).ApplyAsync(new(false, 60), CancellationToken.None)));

        Assert.Equal(new(true, 30), probe.Settings);
        Assert.True(probe.Running);
        Assert.Equal(1, probe.Starts);
    }

    [Theory]
    [InlineData("quiesce")]
    [InlineData("write")]
    [InlineData("start")]
    public async Task UnverifiedTarget_CompensatesInsteadOfReportingSuccess(string stage)
    {
        Probe probe = new();
        probe.Quiescing = call =>
        {
            if (stage == "quiesce" && call == 1) { probe.Running = true; }
            return Task.CompletedTask;
        };
        probe.Writing = (call, settings) =>
        {
            if (stage != "write" || call != 1) { probe.Settings = settings; }
        };
        probe.Starting = call =>
        {
            if (stage == "start" && call == 1) { probe.Running = false; }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(probe).ApplyAsync(new(true, 60), CancellationToken.None));

        Assert.Equal(new(true, 30), probe.Settings);
        Assert.True(probe.Running);
    }

    [Theory]
    [InlineData("quiesce")]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("start")]
    public async Task RecoveryFailure_IsClassifiedAndNeverStartsBeforeVerifiedBaseline(string stage)
    {
        IOException application = new("apply failed");
        IOException recovery = new("recovery failed");
        Probe probe = new();
        probe.Starting = call => Task.FromException(call == 1 ? application : recovery);
        probe.Quiescing = call => stage == "quiesce" && call == 2 ? Task.FromException(recovery) : Task.CompletedTask;
        probe.Writing = (call, settings) =>
        {
            if (stage == "write" && call == 2) { throw recovery; }
            probe.Settings = settings;
        };
        probe.Reading = call => stage == "read" && call == 3 ? throw recovery : probe.Settings;

        ConnectionSamplingSettingsRecoveryException result = await Assert.ThrowsAsync<ConnectionSamplingSettingsRecoveryException>(
            () => Create(probe).ApplyAsync(new(true, 60), CancellationToken.None));

        Assert.Same(application, result.ApplicationFailure);
        Assert.Same(recovery, result.RecoveryFailure);
        Assert.Equal(stage == "start" ? 2 : 1, probe.Starts);
        if (stage != "start") { Assert.False(probe.Running); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaselineReadFailure_DoesNotTouchTheLoopOrPreferences(bool runtime)
    {
        IOException failure = new("cannot observe baseline");
        Probe probe = new();
        if (runtime) { probe.Observing = _ => throw failure; }
        else { probe.Reading = _ => throw failure; }

        Assert.Same(failure, await Record.ExceptionAsync(() => Create(probe).ApplyAsync(new(false, 60), CancellationToken.None)));
        Assert.Equal(0, probe.Quiesces);
        Assert.Empty(probe.Written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeQuiescence_DoesNotWriteOrChangeLifecycle(bool duringObservation)
    {
        using CancellationTokenSource cancellation = new();
        Probe probe = new();
        if (duringObservation) { probe.Reading = _ => { cancellation.Cancel(); return probe.Settings; }; }
        else { cancellation.Cancel(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(probe).ApplyAsync(new(false, 60), cancellation.Token));
        Assert.Equal(0, probe.Quiesces);
        Assert.Empty(probe.Written);
    }

    [Fact]
    public async Task CancellationAfterQuiescenceBegins_RetainsAdmissionThroughCompleteActivation()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe probe = new() { Quiescing = _ => release.Task };
        MutationAdmissionBarrier admission = new();
        Task apply = new ConnectionSamplingSettingsCoordinator(admission, probe).ApplyAsync(new(false, 60), cancellation.Token);
        cancellation.Cancel();
        Task<MutationAdmissionLease> drain = admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
        bool retained = !drain.IsCompleted && !apply.IsCompleted && probe.Written.Count == 0;
        release.SetResult();
        await apply;
        await using MutationAdmissionLease exclusive = await drain;

        Assert.True(retained);
        Assert.Equal(new(false, 60), probe.Settings);
        Assert.False(probe.Running);
        Assert.All(probe.Tokens, token => Assert.False(token.CanBeCanceled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRequest_CancellationOrRevocationDoesNotEnterOperation(bool revoke)
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe probe = new() { Quiescing = _ => release.Task };
        MutationAdmissionBarrier admission = new();
        ConnectionSamplingSettingsCoordinator coordinator = new(admission, probe);
        Task first = coordinator.ApplyAsync(new(false, 60), CancellationToken.None);
        Task second = coordinator.SetEnabledAsync(true, cancellation.Token);
        Task<MutationAdmissionLease>? drain = revoke
            ? admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask()
            : null;
        if (!revoke) { cancellation.Cancel(); }
        Exception? failure = await Record.ExceptionAsync(() => second);
        release.SetResult();
        await first;
        if (drain is not null) { await using MutationAdmissionLease exclusive = await drain; }

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, probe.Quiesces);
        Assert.Equal(new(false, 60), probe.Settings);
    }

    [Fact]
    public async Task OrdinaryAndAdmittedEnable_SerializeAndKeepTheLatestInterval()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe probe = new() { Quiescing = call => call == 1 ? release.Task : Task.CompletedTask };
        MutationAdmissionBarrier admission = new();
        ConnectionSamplingSettingsCoordinator coordinator = new(admission, probe);
        Task first = coordinator.ApplyAsync(new(false, 90), CancellationToken.None);
        using MutationAdmissionLease callerLease = admission.AcquireOrdinary();
        Task second = coordinator.SetEnabledAdmittedAsync(true, callerLease, CancellationToken.None);
        bool serialized = probe.Quiesces == 1 && !second.IsCompleted;
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.True(serialized);
        Assert.Equal(new(true, 90), probe.Settings);
        Assert.True(probe.Running);
        admission.EnsureActiveLease(callerLease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCallerLease_IsRejectedBeforeObservation(bool disposed)
    {
        MutationAdmissionBarrier admission = new();
        using MutationAdmissionLease lease = (disposed ? admission : new MutationAdmissionBarrier()).AcquireOrdinary();
        if (disposed) { lease.Dispose(); }
        Probe probe = new();

        Assert.NotNull(await Record.ExceptionAsync(() => new ConnectionSamplingSettingsCoordinator(admission, probe)
            .SetEnabledAdmittedAsync(false, lease, CancellationToken.None)));
        Assert.Empty(probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalFailureGraph_IsNeverConvertedToSuccessfulCompensation(bool recovery)
    {
        AggregateException fatal = new(Activator.CreateInstance<OutOfMemoryException>());
        Probe probe = new()
        {
            Quiescing = call => call == 1 && recovery
                ? Task.FromException(new IOException("apply"))
                : Task.FromException(fatal),
        };

        Exception? result = await Record.ExceptionAsync(() => Create(probe).ApplyAsync(new(false, 60), CancellationToken.None));
        Assert.Same(fatal, result);
        Assert.True(ExceptionGraphClassifier.IsProcessFatal(result!));
        Assert.Empty(probe.Written);
    }

    private static ConnectionSamplingSettingsCoordinator Create(Probe probe) => new(new MutationAdmissionBarrier(), probe);

    private sealed class Probe : IConnectionSamplingSettingsOperation
    {
        public ConnectionSamplingSettings Settings { get; set; } = new(true, 30);
        public bool Running { get; set; } = true;
        public List<string> Calls { get; } = [];
        public List<ConnectionSamplingSettings> Written { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int Quiesces { get; private set; }
        public int Starts { get; private set; }
        private int _reads;
        private int _observations;
        public Func<int, Task>? Quiescing { get; set; }
        public Func<int, Task>? Starting { get; set; }
        public Action<int, ConnectionSamplingSettings>? Writing { get; set; }
        public Func<int, ConnectionSamplingSettings>? Reading { get; set; }
        public Func<int, bool>? Observing { get; set; }

        public ConnectionSamplingSettings ReadSettings()
        {
            Calls.Add("read");
            return Reading?.Invoke(++_reads) ?? Settings;
        }

        public void WriteSettings(ConnectionSamplingSettings settings, MutationAdmissionLease admissionLease)
        {
            Assert.False(Running);
            Calls.Add("write");
            Written.Add(settings);
            if (Writing is null) { Settings = settings; }
            else { Writing(Written.Count, settings); }
        }

        public bool IsRunning
        {
            get { Calls.Add("observe"); return Observing?.Invoke(++_observations) ?? Running; }
        }

        public async Task QuiesceAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            Calls.Add("quiesce");
            Running = false;
            ++Quiesces;
            if (Quiescing is not null) { await Quiescing(Quiesces); }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            Calls.Add("start");
            Running = true;
            ++Starts;
            if (Starting is not null) { await Starting(Starts); }
        }
    }
}
