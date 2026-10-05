using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOriginalRestorationCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalRestorationUsesOnlyTheDedicatedHelperAndObservedTerminal(bool reserved)
    {
        var ports = new Ports(reserved);
        using InstallerCoordinator coordinator = ports.Coordinator();

        InstallerExecutionResult result = await coordinator.RestoreOriginalAsync(ports.Request, null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal("installer.recovery.original_restored", result.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.OriginalRestored, result.LastDurablePhase);
        Assert.False(result.RecoveryPending);
        Assert.Null(result.DirectoryCleanupReport);
        Assert.Null(ports.Current);
        Assert.Equal(1, ports.RestoreEffects);
        Assert.Equal(1, ports.ClearCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-release")]
    [InlineData("package-committed")]
    [InlineData("reassociation")]
    public async Task UnsupportedRestorationNeverReachesThePrivilegedPort(string scenario)
    {
        var ports = new Ports(true);
        InstallerRequest request = ports.Request;
        if (scenario == "missing") { ports.Current = null; }
        else if (scenario == "wrong-release")
        {
            ports.Current = InstallerTransactionSnapshot.Create(ports.Current!.Journal with { InstallerPayloadSha256 = InstallerTestData.OtherHash });
        }
        else if (scenario == "package-committed")
        {
            ports.Current = InstallerTransactionSnapshot.Create(ports.Current!.Journal.TransitionTo(InstallerTransactionPhase.PackageCommitted));
        }
        else { request = request with { AllowReassociation = true }; }
        InstallerTransactionSnapshot? before = ports.Current;
        using InstallerCoordinator coordinator = ports.Coordinator();

        InstallerExecutionResult result = await coordinator.RestoreOriginalAsync(request, null, CancellationToken.None);

        Assert.NotEqual(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(before, ports.Current);
        Assert.Equal(0, ports.RestoreCalls);
        Assert.Equal(0, ports.ClearCalls);
    }

    [Fact]
    public async Task CancelledAfterRestorationCommitKeepsTerminalAndNormalContinueOnlyFinishesPreservation()
    {
        var ports = new Ports(true);
        using var cancellation = new CancellationTokenSource();
        ports.AfterRestore = cancellation.Cancel;
        using InstallerCoordinator coordinator = ports.Coordinator();

        InstallerExecutionResult cancelled = await coordinator.RestoreOriginalAsync(ports.Request, null, cancellation.Token);

        Assert.Equal(InstallerExecutionOutcome.Cancelled, cancelled.Outcome);
        Assert.True(cancelled.RecoveryPending);
        Assert.Equal(InstallerTransactionPhase.OriginalRestored, ports.Current?.Journal.Phase);
        Assert.Equal(0, ports.ClearCalls);
        ports.AfterRestore = null;

        InstallerExecutionResult resumed = await coordinator.ExecuteAsync(ports.Request, null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal("installer.recovery.original_restored", resumed.DiagnosticCode);
        Assert.Equal(1, ports.RestoreEffects);
        Assert.Equal(2, ports.RestoreCalls);
        Assert.Null(ports.Current);
    }

    [Theory]
    [InlineData("restore-reply")]
    [InlineData("clear-reply")]
    [InlineData("clear-not-observed")]
    [InlineData("wrong-clear-receipt")]
    public async Task AcknowledgementOrObservationFailureCannotReportSuccess(string scenario)
    {
        var ports = new Ports(true) { Failure = scenario };
        using InstallerCoordinator coordinator = ports.Coordinator();

        InstallerExecutionResult result = await coordinator.RestoreOriginalAsync(ports.Request, null, CancellationToken.None);

        Assert.NotEqual(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, ports.RestoreEffects);
        if (scenario is "restore-reply" or "clear-not-observed")
        {
            Assert.True(result.RecoveryPending);
            Assert.Equal(InstallerTransactionPhase.OriginalRestored, ports.Current?.Journal.Phase);
        }
        else { Assert.Null(ports.Current); }
    }

    [Fact]
    public async Task ConcurrentCandidateExecutionCannotRaceAnAdmittedRestoration()
    {
        var ports = new Ports(true);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ports.BeforeRestore = async () => { admitted.SetResult(); await release.Task; };
        using InstallerCoordinator coordinator = ports.Coordinator();
        Task<InstallerExecutionResult> restoring = coordinator.RestoreOriginalAsync(ports.Request, null, CancellationToken.None);
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            InstallerExecutionResult concurrent = await coordinator.ExecuteAsync(ports.Request, null, CancellationToken.None);
            Assert.Equal("installer.concurrent_action_rejected", concurrent.DiagnosticCode);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(InstallerExecutionOutcome.Succeeded, (await restoring).Outcome);
    }

    private sealed class Ports : IInstallerEnvironment, IInstallerReleaseVerifier, IInstallerCertificateMutation,
        IInstallerPackageMutation, IInstallerMachineMutation, IInstallerFinalVerifier,
        IInstallerOriginalRestorationMutation, IInstallerTransactionReader
    {
        internal Ports(bool reserved)
        {
            InstallerTransactionJournal journal = InstallerTestData.Journal(InstallerOperation.Repair) with { AllowReassociation = false };
            if (reserved) { journal = journal.TransitionTo(InstallerTransactionPhase.MachineReserved); }
            Current = InstallerTransactionSnapshot.Create(journal);
        }
        internal InstallerRequest Request { get; } = InstallerTestData.Request(InstallerOperation.Repair);
        internal InstallerTransactionSnapshot? Current { get; set; }
        internal int RestoreCalls { get; private set; }
        internal int RestoreEffects { get; private set; }
        internal int ClearCalls { get; private set; }
        internal Action? AfterRestore { get; set; }
        internal Func<Task>? BeforeRestore { get; set; }
        internal string? Failure { get; init; }
        internal InstallerCoordinator Coordinator() => new(this, this, this, this, this, this, this);
        public Task<InstallerEnvironmentSnapshot> InspectAsync(InstallerRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InstallerEnvironmentSnapshot(true, "1.2.3.3", false, null));
        public Task<IInstallerReleaseLease> VerifyAsync(InstallerRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IInstallerReleaseLease>(new TestInstallerReleaseLease(InstallerTestData.Release()));
        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }
        public async Task<InstallerTransactionSnapshot> RestoreOriginalAsync(InstallerRequest request, IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableState, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCalls++;
            if (BeforeRestore is not null) { await BeforeRestore(); }
            if (durableState.Journal.Phase != InstallerTransactionPhase.OriginalRestored) { RestoreEffects++; }
            Current = InstallerTransactionSnapshot.Create(durableState.Journal.TransitionTo(InstallerTransactionPhase.OriginalRestored));
            AfterRestore?.Invoke();
            if (Failure == "restore-reply") { throw new InstallerStateUncertainException("installer.machine_helper.response_unconfirmed"); }
            return Current;
        }
        public Task<InstallerTransactionSnapshot> ClearOriginalRestoredAsync(InstallerRequest request, IInstallerReleaseLease release,
            InstallerTransactionSnapshot restoredState, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCalls++;
            if (Failure != "clear-not-observed") { Current = null; }
            if (Failure == "clear-reply") { throw new InstallerStateUncertainException("installer.machine_helper.response_unconfirmed"); }
            return Task.FromResult(Failure == "wrong-clear-receipt"
                ? InstallerTransactionSnapshot.Create(restoredState.Journal with { TransactionId = new string('b', 64) }) : restoredState);
        }
        public Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken) => throw new InvalidOperationException("Preservation must not mutate the candidate package or certificates.");
        public Task<InstallerTransactionSnapshot> PrepareAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableIntent, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<InstallerTransactionSnapshot> CommitPackageAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableIntent, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<InstallerTransactionSnapshot> ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableIntent, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<InstallerTransactionSnapshot> VerifyAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot durableState, CancellationToken cancellationToken) => throw new InvalidOperationException("Candidate final verification cannot prove preservation.");
        public Task<InstallerClearReceipt> ClearVerifiedAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot verifiedState, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
