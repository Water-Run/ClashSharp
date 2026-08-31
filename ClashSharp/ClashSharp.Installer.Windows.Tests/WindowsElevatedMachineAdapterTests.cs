using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsElevatedMachineAdapterTests
{
    private const string ContentHash =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task EveryOperationPreparesAtExactDurableIntent(
        InstallerOperation operation)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(operation);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerTransactionSnapshot state = Snapshot(
            request,
            InstallerTransactionPhase.Prepared);

        InstallerTransactionSnapshot committed = await adapter.PrepareAsync(
            request,
            lease,
            state,
            CancellationToken.None);

        InstallerMachineHelperInvocation invocation = Assert.Single(broker.Invocations);
        Assert.Equal(InstallerMachineHelperVerb.Prepare, invocation.Verb);
        invocation.ValidateAgainst(state);
        Assert.Equal(state, Assert.Single(broker.Commands).ToDurableState());
        Assert.Equal(SuccessfulState(Assert.Single(broker.Commands)), committed);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.MachineReserved)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted)]
    public async Task EveryOperationCommitsOnlyAnIndependentlyVerifiedPackagePhase(
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(operation);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerTransactionSnapshot state = Snapshot(request, phase);

        InstallerTransactionSnapshot committed = await adapter.CommitPackageAsync(
            request,
            lease,
            state,
            CancellationToken.None);

        InstallerMachineHelperInvocation invocation = Assert.Single(broker.Invocations);
        Assert.Equal(InstallerMachineHelperVerb.CommitPackage, invocation.Verb);
        invocation.ValidateAgainst(state);
        Assert.Equal(state, Assert.Single(broker.Commands).ToDurableState());
        Assert.Equal(SuccessfulState(Assert.Single(broker.Commands)), committed);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task InstallAndRepairUseApplyAtExactPackageCommit(
        InstallerOperation operation)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(operation);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerTransactionSnapshot state = Snapshot(
            request,
            InstallerTransactionPhase.PackageCommitted);

        InstallerTransactionSnapshot committed = await adapter.ApplyAsync(
            request,
            lease,
            state,
            CancellationToken.None);

        InstallerMachineHelperInvocation invocation = Assert.Single(broker.Invocations);
        Assert.Equal(InstallerMachineHelperVerb.Apply, invocation.Verb);
        invocation.ValidateAgainst(state);
        Assert.Equal(state, Assert.Single(broker.Commands).ToDurableState());
        Assert.Equal(SuccessfulState(Assert.Single(broker.Commands)), committed);
    }

    [Fact]
    public async Task UninstallUsesRemoveOnlyAfterDurableAuthorization()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerTransactionSnapshot state = Snapshot(
            request,
            InstallerTransactionPhase.MachineRemovalAuthorized);

        InstallerTransactionSnapshot committed = await adapter.ApplyAsync(
            request,
            lease,
            state,
            CancellationToken.None);

        InstallerMachineHelperInvocation invocation = Assert.Single(broker.Invocations);
        Assert.Equal(InstallerMachineHelperVerb.Remove, invocation.Verb);
        invocation.ValidateAgainst(state);
        Assert.Equal(state, Assert.Single(broker.Commands).ToDurableState());
        Assert.Equal(SuccessfulState(Assert.Single(broker.Commands)), committed);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.MachineCommitted)]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Verified)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.Verified)]
    public async Task FinalVerifierUsesVerifyAtEveryAllowedReplayPhase(
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(operation);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerTransactionSnapshot state = Snapshot(request, phase);

        InstallerTransactionSnapshot committed = await adapter.VerifyAsync(
            request,
            lease,
            state,
            CancellationToken.None);

        InstallerMachineHelperInvocation invocation = Assert.Single(broker.Invocations);
        Assert.Equal(InstallerMachineHelperVerb.Verify, invocation.Verb);
        invocation.ValidateAgainst(state);
        Assert.Equal(state, Assert.Single(broker.Commands).ToDurableState());
        Assert.Equal(SuccessfulState(Assert.Single(broker.Commands)), committed);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task FinalClearUsesExactVerifiedJournalAsTheHelperReceipt(
        InstallerOperation operation)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(operation);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerTransactionSnapshot verified = Snapshot(
            request,
            InstallerTransactionPhase.Verified);

        InstallerTransactionSnapshot receipt = await adapter.ClearVerifiedAsync(
            request,
            lease,
            verified,
            CancellationToken.None);

        InstallerMachineHelperInvocation invocation = Assert.Single(broker.Invocations);
        Assert.Equal(InstallerMachineHelperVerb.Clear, invocation.Verb);
        invocation.ValidateAgainst(verified);
        Assert.Equal(verified, Assert.Single(broker.Commands).ToDurableState());
        Assert.Equal(verified, receipt);
    }

    [Fact]
    public async Task CancellationBeforeBrokerStartDoesNotLaunchElevation()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ApplyAsync(
            request,
            lease,
            Snapshot(request, InstallerTransactionPhase.PackageCommitted),
            cancellation.Token));

        Assert.Empty(broker.Invocations);
    }

    [Fact]
    public async Task CancellationAfterBrokerStartWaitsForTerminalResultAndKeepsLease()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var completion = new TaskCompletionSource<InstallerMachineHelperResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<InstallerMachineHelperCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new RecordingBroker(command =>
        {
            started.SetResult(command);
            return completion.Task;
        });
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        using var cancellation = new CancellationTokenSource();

        Task action = adapter.ApplyAsync(
            request,
            lease,
            Snapshot(request, InstallerTransactionPhase.PackageCommitted),
            cancellation.Token);
        InstallerMachineHelperCommand command = await started.Task;
        cancellation.Cancel();
        Assert.False(action.IsCompleted);
        completion.SetResult(InstallerMachineHelperResult.Succeeded(
            command,
            SuccessfulState(command)));

        await action;
        await lease.ReverifyAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task StableHelperFailureIsReturnedWithoutRawDetails()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = new RecordingBroker(command => Task.FromResult(
            InstallerMachineHelperResult.Failed(
                command,
                "installer.machine.service_configuration_failed")));
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.ApplyAsync(
                request,
                lease,
                Snapshot(request, InstallerTransactionPhase.PackageCommitted),
                CancellationToken.None));

        Assert.Equal("installer.machine.service_configuration_failed", exception.DiagnosticCode);
    }

    [Fact]
    public async Task CommittedReplayDriftIsReportedWithoutJournalRegression()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = new RecordingBroker(command => Task.FromResult(
            InstallerMachineHelperResult.PostconditionFailed(
                command,
                SuccessfulState(command),
                "installer.machine.final_state_drifted")));
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerTransactionSnapshot verified = Snapshot(
            request,
            InstallerTransactionPhase.Verified);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.VerifyAsync(
                request,
                lease,
                verified,
                CancellationToken.None));

        Assert.Equal("installer.machine.final_state_drifted", exception.DiagnosticCode);
        Assert.Equal(verified, Assert.Single(broker.Commands).GetExpectedSuccessfulState());
    }

    [Fact]
    public async Task MismatchedHelperResultCannotCrossInvocationBoundary()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = new RecordingBroker(command => Task.FromResult(
            InstallerMachineHelperResult.Succeeded(
                command,
                SuccessfulState(command)) with
            {
                JournalContentHash =
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            }));
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.ApplyAsync(
                request,
                lease,
                Snapshot(request, InstallerTransactionPhase.PackageCommitted),
                CancellationToken.None));

        Assert.Equal("installer.machine_helper.result_mismatch", exception.DiagnosticCode);
    }

    [Fact]
    public async Task RecoverableBrokerFailureIsSanitizedButFatalFailurePropagates()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        InstallerTransactionSnapshot state = Snapshot(
            request,
            InstallerTransactionPhase.PackageCommitted);

        var recoverable = new WindowsElevatedMachineAdapter(
            new RecordingBroker(static _ => throw new InvalidOperationException("sensitive")),
            () => request.TargetSid);
        InstallerProtocolException sanitized =
            await Assert.ThrowsAsync<InstallerProtocolException>(() => recoverable.ApplyAsync(
                request,
                lease,
                state,
                CancellationToken.None));
        Assert.Equal("installer.elevation.failed", sanitized.DiagnosticCode);

        var fatal = new WindowsElevatedMachineAdapter(
            new RecordingBroker(static _ => throw new FatalTestException("sentinel")),
            () => request.TargetSid);
        await Assert.ThrowsAsync<FatalTestException>(() => fatal.ApplyAsync(
            request,
            lease,
            state,
            CancellationToken.None));
    }

    [Fact]
    public async Task UserCancellationAndUncertainTerminationRemainDistinct()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        InstallerTransactionSnapshot state = Snapshot(
            request,
            InstallerTransactionPhase.PackageCommitted);

        var cancelled = new WindowsElevatedMachineAdapter(
            new RecordingBroker(static _ => throw new InstallerUserCancelledException(
                "installer.elevation.user_cancelled")),
            () => request.TargetSid);
        InstallerUserCancelledException user =
            await Assert.ThrowsAsync<InstallerUserCancelledException>(() => cancelled.ApplyAsync(
                request,
                lease,
                state,
                CancellationToken.None));
        Assert.Equal("installer.elevation.user_cancelled", user.DiagnosticCode);

        var uncertain = new WindowsElevatedMachineAdapter(
            new RecordingBroker(static _ => throw new InstallerStateUncertainException(
                "installer.elevation.termination_unconfirmed")),
            () => request.TargetSid);
        InstallerStateUncertainException unknown =
            await Assert.ThrowsAsync<InstallerStateUncertainException>(() => uncertain.ApplyAsync(
                request,
                lease,
                state,
                CancellationToken.None));
        Assert.Equal("installer.elevation.termination_unconfirmed", unknown.DiagnosticCode);
    }

    [Fact]
    public async Task SidAndDurableRequestMismatchesFailBeforeElevation()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var broker = RecordingBroker.Succeeding();
        var wrongSid = new WindowsElevatedMachineAdapter(
            broker,
            static () => "S-1-5-21-100-200-300-1002");

        InstallerProtocolException sid = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            wrongSid.ApplyAsync(
                request,
                lease,
                Snapshot(request, InstallerTransactionPhase.PackageCommitted),
                CancellationToken.None));
        Assert.Equal("installer.machine.target_user_mismatch", sid.DiagnosticCode);

        InstallerRequest different = fixture.Request(InstallerOperation.Repair);
        var adapter = new WindowsElevatedMachineAdapter(broker, () => request.TargetSid);
        InstallerProtocolException transaction =
            await Assert.ThrowsAsync<InstallerProtocolException>(() => adapter.ApplyAsync(
                request,
                lease,
                Snapshot(different, InstallerTransactionPhase.PackageCommitted),
                CancellationToken.None));
        Assert.Equal("installer.machine_helper.transaction_mismatch", transaction.DiagnosticCode);
        Assert.Empty(broker.Invocations);
    }

    private static InstallerTransactionSnapshot Snapshot(
        InstallerRequest request,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        InstallerTransactionPhase[] order = request.Operation == InstallerOperation.Uninstall
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
        foreach (InstallerTransactionPhase next in order.Skip(1))
        {
            if (journal.Phase == phase)
            {
                break;
            }

            journal = journal.TransitionTo(next);
        }

        Assert.Equal(phase, journal.Phase);
        return InstallerTransactionSnapshot.Create(journal);
    }

    private static InstallerTransactionSnapshot SuccessfulState(
        InstallerMachineHelperCommand command)
    {
        InstallerTransactionSnapshot requestState = command.ToDurableState();
        InstallerTransactionPhase committedPhase = command.Verb switch
        {
            InstallerMachineHelperVerb.Prepare =>
                requestState.Journal.Operation == InstallerOperation.Uninstall
                    ? InstallerTransactionPhase.MachineRemovalAuthorized
                    : InstallerTransactionPhase.MachineReserved,
            InstallerMachineHelperVerb.CommitPackage =>
                InstallerTransactionPhase.PackageCommitted,
            InstallerMachineHelperVerb.Apply or InstallerMachineHelperVerb.Remove =>
                InstallerTransactionPhase.MachineCommitted,
            InstallerMachineHelperVerb.Verify => InstallerTransactionPhase.Verified,
            InstallerMachineHelperVerb.Clear => InstallerTransactionPhase.Verified,
            _ => throw new InvalidOperationException(),
        };
        return requestState.Journal.Phase == committedPhase
            ? requestState
            : InstallerTransactionSnapshot.Create(
                requestState.Journal.TransitionTo(committedPhase));
    }

    private sealed class RecordingBroker : IWindowsMachineHelperBroker
    {
        private readonly Func<InstallerMachineHelperCommand, Task<InstallerMachineHelperResult>>
            _execute;

        internal RecordingBroker(
            Func<InstallerMachineHelperCommand, Task<InstallerMachineHelperResult>> execute)
        {
            _execute = execute;
        }

        internal List<InstallerMachineHelperInvocation> Invocations { get; } = [];

        internal List<InstallerMachineHelperCommand> Commands { get; } = [];

        public Task<InstallerMachineHelperResult> ExecuteAsync(
            InstallerMachineHelperCommand command)
        {
            command.Validate();
            InstallerMachineHelperInvocation invocation = command.ToInvocation();
            Commands.Add(command);
            Invocations.Add(invocation);
            return _execute(command);
        }

        internal static RecordingBroker Succeeding() => new(command =>
            Task.FromResult(InstallerMachineHelperResult.Succeeded(
                command,
                SuccessfulState(command))));
    }
}
