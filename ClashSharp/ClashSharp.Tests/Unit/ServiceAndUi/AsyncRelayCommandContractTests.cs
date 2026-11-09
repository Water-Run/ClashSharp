using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Lifetime contract shared by every view-model command: at most one run at a time, every start is
/// paired with exactly one end notification, and no sequence of failures, cancellations and repeated
/// invocations can leave a command permanently busy or permanently disabled.
/// </summary>
public sealed class AsyncRelayCommandContractTests
{
    private enum Behavior
    {
        CompleteNow,
        FailNow,
        ThrowSynchronously,
        Pending,
        PendingUntilCancelled,
    }

    [Fact]
    public async Task ReentrantInvocationsWhileRunning_NeverStartASecondRun_ForEveryEntryPoint()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Harness harness = new(new RecordingSink());
        harness.NextBehaviors.Enqueue(Behavior.Pending);
        harness.Command.Execute(null);
        Assert.True(harness.Command.IsRunning);
        int invocations = harness.Invocations;
        Task? first = harness.Command.ExecutionTask;

        harness.Command.Execute(null);
        await harness.Command.ExecuteAsync(null);
        await harness.Command.ExecuteAsync(null, CancellationToken.None);
        await harness.Command.ExecuteObservedAsync(null, CancellationToken.None);
        await harness.Command.ExecuteObservedAsync("parameter", harness.Lifetime.Token);

        Assert.Equal(invocations, harness.Invocations);
        Assert.Same(first, harness.Command.ExecutionTask);
        Assert.False(harness.Command.CanExecute(null));
        harness.ResolveOne(failure: null);
        await first!;
        Assert.True(harness.Command.CanExecute(null));
    }

    [Fact]
    public async Task CancellationDuringARun_EndsItQuietlyAndTheNextRunIsAccepted()
    {
        RecordingSink sink = new();
        Harness harness = new(sink);
        harness.NextBehaviors.Enqueue(Behavior.PendingUntilCancelled);
        Task run = harness.Command.ExecuteObservedAsync(null, harness.Lifetime.Token);
        Assert.True(harness.Command.IsRunning);

        harness.CancelLifetime();
        await run;

        Assert.False(harness.Command.IsRunning);
        Assert.Empty(sink.Errors);
        Assert.Null(harness.Command.LastError);
        harness.NextBehaviors.Enqueue(Behavior.CompleteNow);
        await harness.Command.ExecuteObservedAsync(null, CancellationToken.None);
        Assert.Equal(2, harness.Invocations);
    }

    [Fact]
    public async Task ExplicitCancellation_PropagatesToTheCaller_YetStillReleasesTheCommand()
    {
        Harness harness = new(new RecordingSink());
        harness.NextBehaviors.Enqueue(Behavior.PendingUntilCancelled);
        Task run = harness.Command.ExecuteAsync(null, harness.Lifetime.Token);
        harness.CancelLifetime();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(harness.Command.IsRunning);
        Assert.True(harness.Command.CanExecute(null));
    }

    [Fact]
    public async Task SynchronousDelegateFailure_IsObservedOnceAndReleasesTheCommand_ForEveryEntryPoint()
    {
        RecordingSink sink = new();
        Harness harness = new(sink);

        harness.NextBehaviors.Enqueue(Behavior.ThrowSynchronously);
        harness.Command.Execute(null);
        Assert.False(harness.Command.IsRunning);
        Assert.Single(sink.Errors);
        Assert.IsType<InvalidOperationException>(harness.Command.LastError);

        harness.NextBehaviors.Enqueue(Behavior.ThrowSynchronously);
        await harness.Command.ExecuteObservedAsync(null, CancellationToken.None);
        Assert.Equal(2, sink.Errors.Count);

        harness.NextBehaviors.Enqueue(Behavior.ThrowSynchronously);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Command.ExecuteAsync(null));
        Assert.Equal(2, sink.Errors.Count);
        Assert.False(harness.Command.IsRunning);
        Assert.True(harness.Command.CanExecute(null));
    }

    [Fact]
    public async Task AFailingErrorSink_NeverLeavesTheCommandBusy()
    {
        Harness harness = new(new ThrowingSink(new IOException("sink down")));
        harness.NextBehaviors.Enqueue(Behavior.FailNow);

        await harness.Command.ExecuteObservedAsync(null, CancellationToken.None);

        Assert.False(harness.Command.IsRunning);
        Assert.IsType<AggregateException>(harness.Command.LastError);
        Assert.True(harness.Command.CanExecute(null));
    }

    [Fact]
    public async Task AFailingAvailabilityPredicate_DoesNotCorruptTheRunningState()
    {
        bool throwing = true;
        AsyncRelayCommand command = new(
            _ => Task.CompletedTask,
            new RecordingSink(),
            () => throwing ? throw new InvalidOperationException("predicate") : true);

        Assert.Throws<InvalidOperationException>(() => command.CanExecute(null));
        Assert.Throws<InvalidOperationException>(() => command.Execute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(null));
        Assert.False(command.IsRunning);

        throwing = false;
        Assert.True(command.CanExecute(null));
        await command.ExecuteAsync(null);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task ParameterIsForwardedUnchanged_AndAvailabilityGatesEveryEntryPoint()
    {
        object? seen = null;
        bool available = false;
        AsyncRelayCommand command = new(
            (parameter, _) =>
            {
                seen = parameter;
                return Task.CompletedTask;
            },
            new RecordingSink(),
            () => available);
        object parameter = new();

        command.Execute(parameter);
        await command.ExecuteAsync(parameter);
        await command.ExecuteObservedAsync(parameter, CancellationToken.None);
        Assert.Null(seen);

        available = true;
        await command.ExecuteAsync(parameter);
        Assert.Same(parameter, seen);
    }

    [Fact]
    public void RelayCommand_HonorsAvailabilityOnEveryInvocationAndNotifiesSubscribers()
    {
        int executions = 0;
        bool available = true;
        RelayCommand command = new(() => executions++, () => available);
        int notifications = 0;
        command.CanExecuteChanged += (_, _) => notifications++;

        command.Execute(null);
        available = false;
        command.Execute(null);
        command.Execute("ignored");
        Assert.Equal(1, executions);
        Assert.False(command.CanExecute(null));

        command.NotifyCanExecuteChanged();
        command.NotifyCanExecuteChanged();
        Assert.Equal(2, notifications);
        available = true;
        command.Execute(null);
        Assert.Equal(2, executions);
        Assert.Throws<ArgumentNullException>(() => new RelayCommand(null!));
        Assert.True(new RelayCommand(() => { }).CanExecute(null));
    }

    [Fact]
    public void RelayCommand_ExecuteFailureReachesTheCaller_AndDoesNotDisableTheCommand()
    {
        int attempts = 0;
        RelayCommand command = new(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("first run fails");
            }
        });

        Assert.Throws<InvalidOperationException>(() => command.Execute(null));
        command.Execute(null);

        Assert.Equal(2, attempts);
        Assert.True(command.CanExecute(null));
    }

    private sealed class Harness
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _pending = [];
        private CancellationTokenSource _lifetime = new();

        internal Harness(IApplicationErrorSink sink)
        {
            Command = new AsyncRelayCommand(RunAsync, sink, () => Available, "contract");
            Command.CanExecuteChanged += (_, _) => CanExecuteChangedCount++;
            Command.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AsyncRelayCommand.IsRunning))
                {
                    IsRunningChangedCount++;
                }
            };
        }

        internal AsyncRelayCommand Command { get; }

        internal Queue<Behavior> NextBehaviors { get; } = new();

        internal bool Available { get; set; } = true;

        internal CancellationTokenSource Lifetime => _lifetime;

        internal int Invocations { get; private set; }

        internal int Completions { get; private set; }

        internal int CanExecuteChangedCount { get; private set; }

        internal int IsRunningChangedCount { get; private set; }

        internal int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>Gets the number of accepted starts, derived from the delegate's own count.</summary>
        internal int Started => Invocations;

        /// <summary>Gets the number of finished runs, derived from the delegate's own count.</summary>
        internal int Ended => Completions;

        internal void Script(Random random, bool allowCancellationWait = true)
        {
            Behavior[] behaviors = Enum.GetValues<Behavior>();
            Behavior behavior = behaviors[random.Next(behaviors.Length)];
            if (!allowCancellationWait && behavior == Behavior.PendingUntilCancelled)
            {
                behavior = Behavior.Pending;
            }

            NextBehaviors.Clear();
            NextBehaviors.Enqueue(behavior);
        }

        internal void ResolveOne(Exception? failure)
        {
            TaskCompletionSource? target;
            lock (_gate)
            {
                target = _pending.Count == 0 ? null : _pending[0];
                if (target is not null)
                {
                    _pending.RemoveAt(0);
                }
            }

            if (target is null)
            {
                return;
            }

            if (failure is null)
            {
                target.TrySetResult();
            }
            else
            {
                target.TrySetException(failure);
            }
        }

        internal void CancelLifetime()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
        }

        /// <summary>
        /// Waits for the command and its notifications to settle, then asserts the invariants. The runtime does not
        /// promise that an await continuation runs inline with the completion that released it, so an observation
        /// taken in the same instant is not a contract. A command that never settles still fails here, after a
        /// bounded number of scheduler turns, which is the "never stuck" guarantee.
        /// </summary>
        internal async Task AssertSettledAsync(string label)
        {
            for (int turn = 0; turn < 5000 && !IsSettled(); turn++)
            {
                await Task.Yield();
            }

            AssertInvariants(label);
        }

        private bool IsSettled() =>
            Command.IsRunning == (Invocations - Completions == 1)
            && IsRunningChangedCount == Invocations + Completions;

        internal void AssertInvariants(string label)
        {
            bool running = Command.IsRunning;
            Assert.True(running == Command.IsBusy, label);
            Assert.True(Command.CanExecute(null) == (!running && Available), label);
            // The delegate's own bookkeeping says whether a run is in flight.
            Assert.True(Invocations - Completions == (running ? 1 : 0), $"{label}: in-flight {Invocations - Completions}, running {running}");
            Assert.True(running == PendingOrActive(), label);
            // Every accepted start raised exactly one begin and one end notification pair.
            int begins = Invocations;
            int ends = Completions;
            Assert.True(CanExecuteChangedCount >= begins + ends, $"{label}: missing CanExecuteChanged ({CanExecuteChangedCount} < {begins + ends})");
            Assert.True(IsRunningChangedCount == begins + ends, $"{label}: IsRunning changes {IsRunningChangedCount} != {begins + ends}");
        }

        private bool PendingOrActive() => Invocations - Completions == 1;

        private async Task RunAsync(CancellationToken token)
        {
            Invocations++;
            Behavior behavior = NextBehaviors.Count == 0 ? Behavior.CompleteNow : NextBehaviors.Dequeue();
            try
            {
                switch (behavior)
                {
                    case Behavior.CompleteNow:
                        return;
                    case Behavior.FailNow:
                        await Task.FromException(new InvalidOperationException("scripted failure"));
                        return;
                    case Behavior.ThrowSynchronously:
                        throw new InvalidOperationException("scripted synchronous failure");
                    case Behavior.Pending:
                        {
                            TaskCompletionSource source = new();
                            lock (_gate)
                            {
                                _pending.Add(source);
                            }

                            await source.Task;
                            return;
                        }

                    default:
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return;
                }
            }
            finally
            {
                Completions++;
            }
        }
    }

    private sealed class RecordingSink : IApplicationErrorSink
    {
        internal List<ApplicationError> Errors { get; } = [];

        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
        {
            Errors.Add(applicationError);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink(Exception failure) : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }
}
