using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

/// <summary>
/// Random user/runtime event sequences against the shell: duplicate clicks, re-entrancy, cancellation at any
/// moment, failures of every shape, shutdown and disposal during work. After every step the shell must keep
/// single-flight admission, a consistent busy/cancel state, and never re-enable a mutation on stale readiness.
/// </summary>
public sealed class InstallerShellViewModelSequenceTests
{
    private const int Seed = 20261109;

    private static readonly string[] TransientBadges = ["执行中", "检查中", "正在取消"];

    public static TheoryData<int> Seeds() => new(Enumerable.Range(0, 60).ToArray());

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RandomEventSequencesKeepSingleFlightBusyStateAndFreshReadinessInvariants(int seed)
    {
        Random random = new(Seed + seed);
        ModelRuntime runtime = new();
        InstallerShellViewModel viewModel = new(runtime);
        List<string> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        bool mutationsFresh = false;
        bool disposed = false;
        Task? inFlight = null;
        bool cancelRequestedForInFlight = false;
        int disposedAtChanges = -1;

        // Initial inspection.
        inFlight = viewModel.InitializeAsync();
        AssertInvariants(viewModel, runtime, inFlight, disposed, mutationsFresh);

        for (int step = 0; step < 90 && !disposed; step++)
        {
            int roll = random.Next(100);
            int operationsBefore = runtime.Operations.Count;
            int inspectionsBefore = runtime.Inspections;
            bool wasBusy = viewModel.IsBusy;
            if (roll < 16)
            {
                bool canExecute = viewModel.PrimaryActionCommand.CanExecute(null);
                Task click = viewModel.PrimaryActionCommand.ExecuteAsync();
                Assert.Equal(canExecute && !wasBusy ? operationsBefore + 1 : operationsBefore, runtime.Operations.Count);
                if (canExecute && !wasBusy)
                {
                    inFlight = click;
                    cancelRequestedForInFlight = false;
                    mutationsFresh = false;
                }
            }
            else if (roll < 26)
            {
                bool canExecute = viewModel.SecondaryActionCommand.CanExecute(null);
                Task click = viewModel.SecondaryActionCommand.ExecuteAsync();
                Assert.Equal(canExecute && !wasBusy ? operationsBefore + 1 : operationsBefore, runtime.Operations.Count);
                if (canExecute && !wasBusy)
                {
                    inFlight = click;
                    cancelRequestedForInFlight = false;
                    mutationsFresh = false;
                }
            }
            else if (roll < 38)
            {
                bool canExecute = viewModel.RefreshCommand.CanExecute(null);
                Task click = viewModel.RefreshCommand.ExecuteAsync();
                Assert.Equal(canExecute && !wasBusy ? inspectionsBefore + 1 : inspectionsBefore, runtime.Inspections);
                Assert.Equal(operationsBefore, runtime.Operations.Count);
                if (canExecute && !wasBusy)
                {
                    inFlight = click;
                    cancelRequestedForInFlight = false;
                    mutationsFresh = false;
                }
            }
            else if (roll < 48)
            {
                bool alreadyRequested = viewModel.IsCancellationRequested;
                bool couldCancel = viewModel.CancelCommand.CanExecute(null);
                Assert.Equal(wasBusy && !alreadyRequested, couldCancel);
                viewModel.CancelCommand.Execute(null);
                if (couldCancel)
                {
                    cancelRequestedForInFlight = true;
                    Assert.True(viewModel.IsCancellationRequested);
                    Assert.Contains(runtime.PendingTokens, static token => token.IsCancellationRequested);
                    Assert.False(viewModel.CancelCommand.CanExecute(null));
                }
            }
            else if (roll < 74)
            {
                if (runtime.TryCompleteExecution(random, out bool completed) && completed)
                {
                    await Drain(inFlight);
                    mutationsFresh = false;
                }
            }
            else if (roll < 94)
            {
                if (runtime.TryCompleteInspection(random, out bool succeededReady))
                {
                    await Drain(inFlight);
                    mutationsFresh = succeededReady && !cancelRequestedForInFlight;
                }
            }
            else if (roll < 97)
            {
                viewModel.RequestShutdown();
                Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
                Assert.False(viewModel.RefreshCommand.CanExecute(null));
            }
            else
            {
                viewModel.Dispose();
                viewModel.Dispose();
                disposed = true;
                disposedAtChanges = changed.Count;
            }

            AssertInvariants(viewModel, runtime, inFlight, disposed, mutationsFresh);
        }

        // Settle: finish whatever is still pending so no task leaks, then check the idle end state.
        while (runtime.TryCompleteExecution(random, out _) | runtime.TryCompleteInspection(random, out _))
        {
            await Drain(inFlight);
        }

        await Drain(inFlight);
        if (!disposed)
        {
            Assert.False(viewModel.IsBusy);
            Assert.Equal(0, runtime.PendingCount);
            viewModel.Dispose();
            disposedAtChanges = changed.Count;
        }

        // Nothing touched the UI-bound state after disposal, and the runtime was released exactly once, idle.
        Assert.Equal(disposedAtChanges, changed.Count);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.False(runtime.DisposedWhileActive);
        Assert.Equal(0, runtime.PendingCount);
        Assert.True(runtime.MaxConcurrentRuntimeCalls <= 1, "More than one runtime operation was admitted at once.");
    }

    [Fact]
    public async Task ADuplicateClickStormWhileBusyStartsExactlyOneOperationAndLaterClicksAreDropped()
    {
        ModelRuntime runtime = new();
        using InstallerShellViewModel viewModel = new(runtime);
        Task init = viewModel.InitializeAsync();
        Assert.True(runtime.TryCompleteInspection(new Random(Seed), out bool ready, forced: InspectionKind.Available));
        await init;
        Assert.True(ready);

        Task[] storm = Enumerable.Range(0, 50).Select(_ => viewModel.PrimaryActionCommand.ExecuteAsync()).ToArray();

        Assert.Single(runtime.Operations);
        Assert.Equal(InstallerOperation.Install, runtime.Operations[0]);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.Equal(49, storm.Count(static task => task.IsCompletedSuccessfully));
        // The one in-flight click is identified before the runtime completes, because completing it may
        // run the continuation inline and finish the task before it could be looked up again.
        Task inFlight = storm.Single(static task => !task.IsCompleted);

        Assert.True(runtime.TryCompleteExecution(new Random(Seed), out _, forced: ExecutionKind.Succeeded));
        await inFlight;
        Assert.False(viewModel.IsBusy);
        // The success invalidated readiness: no second install without a fresh inspection.
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
        await viewModel.PrimaryActionCommand.ExecuteAsync();
        Assert.Single(runtime.Operations);
    }

    [Theory]
    [InlineData(ExecutionKind.Succeeded)]
    [InlineData(ExecutionKind.Blocked)]
    [InlineData(ExecutionKind.Failed)]
    [InlineData(ExecutionKind.Uncertain)]
    [InlineData(ExecutionKind.Cancelled)]
    [InlineData(ExecutionKind.ThrowProtocol)]
    [InlineData(ExecutionKind.ThrowIo)]
    [InlineData(ExecutionKind.ThrowUserCancel)]
    [InlineData(ExecutionKind.InvalidResult)]
    public async Task EveryTerminalOutcomeReleasesTheGateDisablesStaleMutationsAndNeverLeaksRawText(ExecutionKind kind)
    {
        ModelRuntime runtime = new();
        using InstallerShellViewModel viewModel = new(runtime);
        Task init = viewModel.InitializeAsync();
        runtime.TryCompleteInspection(new Random(Seed), out _, forced: InspectionKind.Available);
        await init;
        Task running = viewModel.PrimaryActionCommand.ExecuteAsync();
        Assert.True(viewModel.IsBusy);

        Assert.True(runtime.TryCompleteExecution(new Random(Seed), out _, forced: kind));
        await running;

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsProgressIndeterminate);
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
        Assert.DoesNotContain("secret", viewModel.StatusDetail + viewModel.StatusTitle + viewModel.DiagnosticCode, StringComparison.Ordinal);
        Assert.NotEqual("执行中", viewModel.StatusBadge);
        Assert.InRange(viewModel.ProgressValue, 0, 100);
        InstallerProtocolValidation.ValidateDiagnosticCode(viewModel.DiagnosticCode);

        // Recovery path: a fresh inspection is the only way back to an actionable state.
        Task refresh = viewModel.RefreshCommand.ExecuteAsync();
        Assert.True(runtime.TryCompleteInspection(new Random(Seed), out bool ready, forced: InspectionKind.Available));
        await refresh;
        Assert.True(ready);
        Assert.True(viewModel.PrimaryActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingEveryKindOfPendingWorkCancelsItsTokenOnceAndEndsIdleAndRetryable()
    {
        foreach (bool inspection in new[] { true, false })
        {
            ModelRuntime runtime = new();
            using InstallerShellViewModel viewModel = new(runtime);
            Task running = viewModel.InitializeAsync();
            runtime.TryCompleteInspection(new Random(Seed), out _, forced: InspectionKind.Available);
            await running;
            if (!inspection)
            {
                running = viewModel.PrimaryActionCommand.ExecuteAsync();
            }
            else
            {
                running = viewModel.RefreshCommand.ExecuteAsync();
            }

            Assert.True(viewModel.CancelCommand.CanExecute(null));
            viewModel.CancelCommand.Execute(null);
            viewModel.CancelCommand.Execute(null);
            viewModel.RequestCancellation();

            Assert.True(viewModel.IsCancellationRequested);
            Assert.False(viewModel.CancelCommand.CanExecute(null));
            Assert.Equal(1, runtime.PendingTokens.Count(static token => token.IsCancellationRequested));
            // A cancellation is not a retry: nothing starts until the gate drains.
            Assert.False(viewModel.RefreshCommand.CanExecute(null));
            Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));

            if (inspection)
            {
                Assert.True(runtime.TryCompleteInspection(new Random(Seed), out _, forced: InspectionKind.Cancel));
            }
            else
            {
                Assert.True(runtime.TryCompleteExecution(new Random(Seed), out _, forced: ExecutionKind.ThrowCancel));
            }

            await running;
            Assert.False(viewModel.IsBusy);
            Assert.Equal("installer.cancelled", viewModel.DiagnosticCode);
            Assert.True(viewModel.RefreshCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task ShutdownAndDisposeDuringWorkCancelItAndReleaseTheRuntimeOnlyAfterTheWorkDrains()
    {
        foreach (bool disposeFirst in new[] { false, true })
        {
            ModelRuntime runtime = new();
            InstallerShellViewModel viewModel = new(runtime);
            Task init = viewModel.InitializeAsync();
            runtime.TryCompleteInspection(new Random(Seed), out _, forced: InspectionKind.Available);
            await init;
            Task running = viewModel.PrimaryActionCommand.ExecuteAsync();
            int changesBefore = 0;

            if (disposeFirst)
            {
                viewModel.Dispose();
            }
            else
            {
                viewModel.RequestShutdown();
                Assert.False(viewModel.RefreshCommand.CanExecute(null));
                Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
            }

            Assert.True(runtime.PendingTokens.All(static token => token.IsCancellationRequested));
            Assert.Equal(0, runtime.DisposeCount);
            viewModel.PropertyChanged += (_, _) => changesBefore++;
            Assert.True(runtime.TryCompleteExecution(new Random(Seed), out _, forced: ExecutionKind.ThrowCancel));
            await running;
            viewModel.Dispose();

            Assert.Equal(1, runtime.DisposeCount);
            Assert.False(runtime.DisposedWhileActive);
            if (disposeFirst)
            {
                Assert.Equal(0, changesBefore);
            }

            await viewModel.PrimaryActionCommand.ExecuteAsync();
            await viewModel.RefreshCommand.ExecuteAsync();
            Assert.Single(runtime.Operations);
        }
    }

    [Fact]
    public async Task FatalFailuresFromTheRuntimeAreNeverSwallowedByTheShell()
    {
        foreach (string wrapper in new[] { "direct", "aggregate", "protocol", "cancel" })
        {
            ModelRuntime runtime = new();
            using InstallerShellViewModel viewModel = new(runtime);
            Task init = viewModel.InitializeAsync();
            runtime.TryCompleteInspection(new Random(Seed), out _, forced: InspectionKind.Available);
            await init;
            Task running = viewModel.PrimaryActionCommand.ExecuteAsync();
            Exception fatal = new FatalPresentationTestException("fatal sentinel");
            Exception failure = wrapper switch
            {
                "aggregate" => new AggregateException(new IOException("sibling"), fatal),
                "protocol" => new InstallerProtocolException("installer.test.wrapped", fatal),
                "cancel" => new OperationCanceledException("wrapped", fatal),
                _ => fatal,
            };

            runtime.FailExecution(failure);

            Assert.Same(failure, await Record.ExceptionAsync(() => running));
            // The single-flight gate is released even when the failure is propagated.
            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.RefreshCommand.CanExecute(null));
        }
    }

    private static async Task Drain(Task? inFlight)
    {
        if (inFlight is not null)
        {
            await inFlight.WaitAsync(TestTimeouts.Completion);
        }
    }

    private static void AssertInvariants(
        InstallerShellViewModel viewModel,
        ModelRuntime runtime,
        Task? inFlight,
        bool disposed,
        bool mutationsFresh)
    {
        Assert.True(runtime.MaxConcurrentRuntimeCalls <= 1, "More than one runtime operation was admitted at once.");
        Assert.InRange(viewModel.ProgressValue, 0, 100);
        InstallerProtocolValidation.ValidateDiagnosticCode(viewModel.DiagnosticCode);
        if (disposed)
        {
            Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
            Assert.False(viewModel.SecondaryActionCommand.CanExecute(null));
            Assert.False(viewModel.RefreshCommand.CanExecute(null));
            return;
        }

        bool pending = runtime.PendingCount > 0;
        Assert.Equal(pending, viewModel.IsBusy);
        if (viewModel.IsBusy)
        {
            Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
            Assert.False(viewModel.SecondaryActionCommand.CanExecute(null));
            Assert.False(viewModel.RefreshCommand.CanExecute(null));
            Assert.False(viewModel.RestoreOriginalCommand.CanExecute(null));
            Assert.False(viewModel.OwnerTransferCommand.CanExecute(null));
            Assert.False(viewModel.RetiredUninstallCommand.CanExecute(null));
            Assert.Equal(!viewModel.IsCancellationRequested, viewModel.CancelCommand.CanExecute(null));
        }
        else
        {
            Assert.False(viewModel.CancelCommand.CanExecute(null));
            Assert.False(viewModel.IsProgressIndeterminate);
            Assert.DoesNotContain(viewModel.StatusBadge, TransientBadges);
            // A mutation is only ever enabled by a successful fresh inspection that proved readiness.
            if (viewModel.PrimaryActionCommand.CanExecute(null))
            {
                Assert.True(mutationsFresh, "A mutation was enabled on stale or failed readiness.");
                Assert.True(viewModel.CanExecuteMutations);
            }
        }

        _ = inFlight;
    }

    public enum InspectionKind
    {
        Random,
        Available,
        Installed,
        Recovery,
        Blocked,
        ThrowProtocol,
        ThrowIo,
        Cancel,
    }

    public enum ExecutionKind
    {
        Random,
        Succeeded,
        Blocked,
        Failed,
        Uncertain,
        Cancelled,
        ThrowProtocol,
        ThrowIo,
        ThrowCancel,
        ThrowUserCancel,
        InvalidResult,
    }

    private sealed class ModelRuntime : IInstallerRuntime, IDisposable
    {
        private readonly List<(TaskCompletionSource<InstallerRuntimeReadiness> Source, CancellationToken Token)> _inspections = [];
        private readonly List<(TaskCompletionSource<InstallerExecutionResult> Source, CancellationToken Token, InstallerOperation Operation)> _executions = [];
        private int _active;

        internal int Inspections { get; private set; }

        internal List<InstallerOperation> Operations { get; } = [];

        internal int DisposeCount { get; private set; }

        internal bool DisposedWhileActive { get; private set; }

        internal int MaxConcurrentRuntimeCalls { get; private set; }

        internal int PendingCount => _inspections.Count + _executions.Count;

        internal IEnumerable<CancellationToken> PendingTokens =>
            _inspections.Select(static item => item.Token).Concat(_executions.Select(static item => item.Token));

        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken)
        {
            Inspections++;
            TaskCompletionSource<InstallerRuntimeReadiness> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _inspections.Add((source, cancellationToken));
            Track();
            return Wait(source.Task, () => _inspections.RemoveAll(item => item.Source == source));
        }

        public Task<InstallerExecutionResult> ExecuteAsync(
            InstallerOperation operation,
            IProgress<InstallerProgress> progress,
            CancellationToken cancellationToken)
        {
            Operations.Add(operation);
            TaskCompletionSource<InstallerExecutionResult> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _executions.Add((source, cancellationToken, operation));
            Track();
            return Wait(source.Task, () => _executions.RemoveAll(item => item.Source == source));
        }

        public void Dispose()
        {
            DisposeCount++;
            DisposedWhileActive |= _active > 0;
        }

        internal bool TryCompleteInspection(Random random, out bool ready, InspectionKind forced = InspectionKind.Random)
        {
            ready = false;
            if (_inspections.Count == 0)
            {
                return false;
            }

            (TaskCompletionSource<InstallerRuntimeReadiness> source, CancellationToken token) = _inspections[0];
            InspectionKind kind = forced == InspectionKind.Random
                ? (InspectionKind)random.Next(1, Enum.GetValues<InspectionKind>().Length)
                : forced;
            if (token.IsCancellationRequested && kind != InspectionKind.ThrowIo)
            {
                kind = InspectionKind.Cancel;
            }

            switch (kind)
            {
                case InspectionKind.Available:
                    source.SetResult(InstallerPresentationTestData.Readiness());
                    ready = true;
                    break;
                case InspectionKind.Installed:
                    source.SetResult(InstallerPresentationTestData.Readiness(InstallerProductState.Installed));
                    ready = true;
                    break;
                case InspectionKind.Recovery:
                    source.SetResult(InstallerPresentationTestData.Readiness(
                        InstallerProductState.RecoveryRequired, InstallerOperation.Repair));
                    ready = true;
                    break;
                case InspectionKind.Blocked:
                    source.SetResult(InstallerPresentationTestData.Readiness(canExecute: false));
                    break;
                case InspectionKind.ThrowProtocol:
                    source.SetException(new InstallerProtocolException("installer.test.inspection_refused", new IOException("secret detail")));
                    break;
                case InspectionKind.ThrowIo:
                    source.SetException(new IOException("secret detail"));
                    break;
                default:
                    source.SetException(new OperationCanceledException(token));
                    break;
            }

            // A "ready" inspection that raced a cancellation request is reported as cancelled by the shell.
            ready &= !token.IsCancellationRequested;
            return true;
        }

        internal bool TryCompleteExecution(Random random, out bool completed, ExecutionKind forced = ExecutionKind.Random)
        {
            completed = false;
            if (_executions.Count == 0)
            {
                return false;
            }

            (TaskCompletionSource<InstallerExecutionResult> source, CancellationToken token, _) = _executions[0];
            ExecutionKind kind = forced == ExecutionKind.Random
                ? (ExecutionKind)random.Next(1, Enum.GetValues<ExecutionKind>().Length)
                : forced;
            if (token.IsCancellationRequested && random.Next(2) == 0)
            {
                kind = ExecutionKind.ThrowCancel;
            }

            switch (kind)
            {
                case ExecutionKind.Succeeded:
                    source.SetResult(InstallerPresentationTestData.Result());
                    break;
                case ExecutionKind.Blocked:
                    source.SetResult(InstallerPresentationTestData.Result(InstallerExecutionOutcome.Blocked, phase: null));
                    break;
                case ExecutionKind.Failed:
                    source.SetResult(InstallerPresentationTestData.Result(
                        InstallerExecutionOutcome.Failed, recoveryPending: true, phase: InstallerTransactionPhase.MachineReserved));
                    break;
                case ExecutionKind.Uncertain:
                    source.SetResult(InstallerPresentationTestData.Result(
                        InstallerExecutionOutcome.Uncertain, recoveryPending: true, phase: InstallerTransactionPhase.PackageCommitted));
                    break;
                case ExecutionKind.Cancelled:
                    source.SetResult(InstallerPresentationTestData.Result(
                        InstallerExecutionOutcome.Cancelled, recoveryPending: true, phase: InstallerTransactionPhase.Prepared));
                    break;
                case ExecutionKind.ThrowProtocol:
                    source.SetException(new InstallerProtocolException("installer.test.execution_refused", new IOException("secret detail")));
                    break;
                case ExecutionKind.ThrowIo:
                    source.SetException(new IOException("secret detail"));
                    break;
                case ExecutionKind.ThrowUserCancel:
                    source.SetException(new InstallerUserCancelledException("installer.test.user_declined"));
                    break;
                case ExecutionKind.InvalidResult:
                    source.SetResult(new InstallerExecutionResult((InstallerExecutionOutcome)99, "installer.test.invalid", null, false));
                    break;
                default:
                    source.SetException(new OperationCanceledException(token));
                    break;
            }

            completed = true;
            return true;
        }

        internal void FailExecution(Exception failure) => _executions[0].Source.SetException(failure);

        private void Track()
        {
            _active++;
            MaxConcurrentRuntimeCalls = Math.Max(MaxConcurrentRuntimeCalls, _active);
        }

        private async Task<T> Wait<T>(Task<T> task, Action remove)
        {
            try
            {
                return await task;
            }
            finally
            {
                remove();
                _active--;
            }
        }
    }
}
