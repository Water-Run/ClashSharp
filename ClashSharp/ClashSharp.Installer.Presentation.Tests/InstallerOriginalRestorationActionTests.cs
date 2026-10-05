using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerOriginalRestorationActionTests
{
    [Theory]
    [InlineData(InstallerTransactionPhase.Prepared, false, true)]
    [InlineData(InstallerTransactionPhase.MachineReserved, false, true)]
    [InlineData(InstallerTransactionPhase.PackageCommitted, false, false)]
    [InlineData(InstallerTransactionPhase.OriginalRestored, false, false)]
    [InlineData(InstallerTransactionPhase.Prepared, true, false)]
    public async Task TrustedReadinessExposesOnlySameOwnerPrecommitPreservation(InstallerTransactionPhase phase, bool reassociation, bool expected)
    {
        using var backend = new Backend(phase, reassociation);
        using var runtime = new ProductionInstallerRuntime(backend);

        InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(CancellationToken.None);

        Assert.Equal(expected, readiness.CanRestoreOriginal);
        if (phase == InstallerTransactionPhase.OriginalRestored) { Assert.Contains("收尾", readiness.StatusDetail, StringComparison.Ordinal); }
    }

    [Fact]
    public async Task VisibleButtonDispatchesDedicatedRecoveryAndInvalidatesReadiness()
    {
        using var backend = new Backend(InstallerTransactionPhase.MachineReserved, false);
        using var runtime = new ProductionInstallerRuntime(backend);
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsOriginalRestoreActionVisible);

        await viewModel.RestoreOriginalCommand.ExecuteAsync();

        Assert.Equal(1, backend.RestoreCalls);
        Assert.Equal(0, backend.OrdinaryCalls);
        Assert.Equal("已保留原安装", viewModel.StatusTitle);
        Assert.False(viewModel.IsOriginalRestoreActionVisible);
    }

    [Fact]
    public async Task CandidateSuccessCannotSatisfyAnExplicitPreservationRequest()
    {
        using var backend = new Backend(InstallerTransactionPhase.Prepared, false)
        {
            Result = new(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false),
        };
        using var viewModel = new InstallerShellViewModel(new ProductionInstallerRuntime(backend));
        await viewModel.InitializeAsync();

        await viewModel.RestoreOriginalCommand.ExecuteAsync();

        Assert.Equal("installer.runtime.result_invalid", viewModel.DiagnosticCode);
        Assert.Equal("操作未完成", viewModel.StatusTitle);
    }

    [Fact]
    public async Task MissingBaselineGivesActionableContinuationGuidance()
    {
        using var backend = new Backend(InstallerTransactionPhase.Prepared, false)
        {
            Result = new(InstallerExecutionOutcome.Failed, "installer.recovery.original_baseline_unavailable", InstallerTransactionPhase.Prepared, true),
        };
        using var viewModel = new InstallerShellViewModel(new ProductionInstallerRuntime(backend));
        await viewModel.InitializeAsync();

        await viewModel.RestoreOriginalCommand.ExecuteAsync();

        Assert.Equal("无法确认原安装可恢复", viewModel.StatusTitle);
        Assert.Contains("继续完成安装", viewModel.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationAndWindowDisposalWaitForAdmittedRecoveryToFinish()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<InstallerExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend(InstallerTransactionPhase.Prepared, false)
        {
            Restore = (_, _) => { admitted.SetResult(); return completed.Task; },
        };
        var viewModel = new InstallerShellViewModel(new ProductionInstallerRuntime(backend));
        await viewModel.InitializeAsync();
        Task running = viewModel.RestoreOriginalCommand.ExecuteAsync();
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.CancelCommand.Execute(null);
            Assert.True(viewModel.IsBusy);
            Assert.True(viewModel.IsCancellationRequested);
            viewModel.Dispose();
            Assert.False(running.IsCompleted);
            Assert.False(backend.Disposed);
        }
        finally
        {
            completed.TrySetResult(backend.Result);
            await running;
            viewModel.Dispose();
        }
        Assert.True(backend.Disposed);
    }

    private sealed class Backend : IInstallerRuntimeBackend, IInstallerOriginalRestorationRuntimeBackend
    {
        private readonly InstallerRuntimeInspection _inspection;
        internal Backend(InstallerTransactionPhase phase, bool reassociation)
        {
            var request = new InstallerRequest(InstallerOperation.Repair, "S-1-5-21-100-200-300-1001", reassociation, "1.0.0.0", new string('a', 64));
            InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
            if (phase == InstallerTransactionPhase.OriginalRestored) { journal = journal.TransitionTo(phase); }
            else if (phase != InstallerTransactionPhase.Prepared)
            {
                journal = journal.TransitionTo(InstallerTransactionPhase.MachineReserved);
                if (phase == InstallerTransactionPhase.PackageCommitted) { journal = journal.TransitionTo(phase); }
            }
            _inspection = new(new InstallerEnvironmentSnapshot(true, "0.9.0.0", false, null), InstallerTransactionSnapshot.Create(journal), "1.0.0.0");
        }
        internal InstallerExecutionResult Result { get; init; } = new(InstallerExecutionOutcome.Succeeded,
            "installer.recovery.original_restored", InstallerTransactionPhase.OriginalRestored, false);
        internal Func<IProgress<InstallerProgress>?, CancellationToken, Task<InstallerExecutionResult>>? Restore { get; init; }
        internal int RestoreCalls { get; private set; }
        internal int OrdinaryCalls { get; private set; }
        internal bool Disposed { get; private set; }
        public bool SupportsOriginalRestoration => true;
        public Task<InstallerRuntimeInspection> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(_inspection);
        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation, IProgress<InstallerProgress>? progress, CancellationToken cancellationToken)
        {
            OrdinaryCalls++;
            throw new InvalidOperationException("Preservation must use its dedicated entry.");
        }
        public Task<InstallerExecutionResult> RestoreOriginalAsync(IProgress<InstallerProgress>? progress, CancellationToken cancellationToken)
        {
            RestoreCalls++;
            return Restore?.Invoke(progress, cancellationToken) ?? Task.FromResult(Result);
        }
        public void Dispose() => Disposed = true;
    }
}
