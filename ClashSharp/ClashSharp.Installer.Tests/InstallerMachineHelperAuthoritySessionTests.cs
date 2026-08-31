using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperAuthoritySessionTests
{
    [Fact]
    public async Task ParentBoundSessionRejectsDifferentTargetBeforeProtectedMutation()
    {
        List<string> events = [];
        var store = new MemoryInstallerTransactionStore(events, initialJournal: null);
        InstallerTransactionSnapshot prepared = StateAt(
            InstallerOperation.Install,
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var operations = new RecordingOperations(events);
        InstallerMachineHelperAuthoritySession session = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                command.ToInvocation(),
                "S-1-5-21-100-200-300-1002",
                store,
                operations,
                CancellationToken.None);

        InstallerProtocolException exception = await Assert.ThrowsAsync<
            InstallerProtocolException>(() => session.ExecuteAsync(
                command,
                CancellationToken.None));

        Assert.Equal(
            "installer.machine_helper.target_sid_mismatch",
            exception.DiagnosticCode);
        Assert.Equal(["journal.load"], events);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task InitialPrepareIsDurableBeforeAnyPrivilegedOperation()
    {
        List<string> events = [];
        var store = new MemoryInstallerTransactionStore(events, initialJournal: null);
        InstallerTransactionSnapshot prepared = StateAt(
            InstallerOperation.Install,
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var operations = new RecordingOperations(events);
        InstallerMachineHelperAuthoritySession session = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                command.ToInvocation(),
                store,
                operations,
                CancellationToken.None);

        InstallerMachineHelperResult result = await session.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
        Assert.Equal(command.GetExpectedSuccessfulState(), result.ValidateAgainst(command));
        Assert.Equal(
            [
                "journal.load",
                "journal.load",
                "journal.save:Prepared",
                "operation:Prepare:Execute",
                "journal.save:MachineReserved",
                "journal.load",
            ],
            events);
        Assert.Equal(
            InstallerTransactionPhase.MachineReserved,
            store.Current?.Journal.Phase);
    }

    [Fact]
    public async Task StablePrepareFailureRetainsPreparedForExactReplay()
    {
        List<string> events = [];
        var store = new MemoryInstallerTransactionStore(events, initialJournal: null);
        InstallerTransactionSnapshot prepared = StateAt(
            InstallerOperation.Install,
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var operations = new RecordingOperations(
            events,
            static (_, _, _) => throw new InstallerProtocolException(
                "installer.machine.prepare_failed"));
        InstallerMachineHelperAuthoritySession session = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                command.ToInvocation(),
                store,
                operations,
                CancellationToken.None);

        InstallerMachineHelperResult result = await session.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Failed, result.Outcome);
        Assert.Equal("installer.machine.prepare_failed", result.DiagnosticCode);
        Assert.Equal(prepared, result.ValidateAgainst(command));
        Assert.Equal(prepared, store.Current);
        Assert.True(
            events.IndexOf("journal.save:Prepared")
            < events.IndexOf("operation:Prepare:Execute"));
        Assert.DoesNotContain("journal.save:MachineReserved", events);
    }

    [Fact]
    public async Task CommittedReplayVerifiesWithoutWritingTheJournalAgain()
    {
        List<string> events = [];
        InstallerTransactionSnapshot prepared = StateAt(
            InstallerOperation.Install,
            InstallerTransactionPhase.Prepared);
        InstallerTransactionSnapshot reserved = InstallerTransactionSnapshot.Create(
            prepared.Journal.TransitionTo(InstallerTransactionPhase.MachineReserved));
        var store = new MemoryInstallerTransactionStore(events, reserved.Journal);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var operations = new RecordingOperations(events);
        InstallerMachineHelperAuthoritySession session = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                command.ToInvocation(),
                store,
                operations,
                CancellationToken.None);

        InstallerMachineHelperResult result = await session.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
        Assert.Equal(reserved, result.ValidateAgainst(command));
        Assert.Equal(reserved, store.Current);
        Assert.Contains("operation:Prepare:VerifyCommittedReplay", events);
        Assert.DoesNotContain(events, static value => value.StartsWith(
            "journal.save:",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearDeletesOnlyVerifiedStateAndCompletesAgainstAnAbsentReload()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = StateAt(
            InstallerOperation.Install,
            InstallerTransactionPhase.Verified);
        var store = new MemoryInstallerTransactionStore(events, verified.Journal);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Clear,
            verified);
        var operations = new RecordingOperations(events);
        InstallerMachineHelperAuthoritySession session = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                command.ToInvocation(),
                store,
                operations,
                CancellationToken.None);

        InstallerMachineHelperResult result = await session.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
        Assert.Equal(verified, result.ValidateAgainst(command));
        Assert.Null(store.Current);
        Assert.Equal(
            [
                "journal.load",
                "journal.load",
                "operation:Clear:Execute",
                "journal.clear",
                "journal.load",
            ],
            events);
    }

    [Fact]
    public async Task ClearAckLossReplayVerifiesAbsenceWithoutInventingAWrite()
    {
        List<string> events = [];
        var store = new MemoryInstallerTransactionStore(events, initialJournal: null);
        InstallerTransactionSnapshot verified = StateAt(
            InstallerOperation.Uninstall,
            InstallerTransactionPhase.Verified);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Clear,
            verified);
        var operations = new RecordingOperations(events);
        InstallerMachineHelperAuthoritySession session = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                command.ToInvocation(),
                store,
                operations,
                CancellationToken.None);

        InstallerMachineHelperResult result = await session.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
        Assert.Equal(verified, result.ValidateAgainst(command));
        Assert.Null(store.Current);
        Assert.Contains("operation:Clear:VerifyCommittedReplay", events);
        Assert.DoesNotContain("journal.clear", events);
        Assert.DoesNotContain(events, static value => value.StartsWith(
            "journal.save:",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task UncertainOperationReconcilesToThePersistedPreparedCutPoint()
    {
        List<string> events = [];
        var store = new MemoryInstallerTransactionStore(events, initialJournal: null);
        InstallerTransactionSnapshot prepared = StateAt(
            InstallerOperation.Install,
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            prepared);
        var operations = new RecordingOperations(
            events,
            static (_, _, _) => throw new InstallerStateUncertainException(
                "installer.machine.operation_unconfirmed"));
        InstallerMachineHelperAuthoritySession session = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                command.ToInvocation(),
                store,
                operations,
                CancellationToken.None);

        InstallerStateUncertainException exception = await Assert.ThrowsAsync<
            InstallerStateUncertainException>(() => session.ExecuteAsync(
                command,
                CancellationToken.None));

        Assert.Equal("installer.machine.operation_unconfirmed", exception.DiagnosticCode);
        Assert.Equal(prepared, store.Current);
        Assert.Equal("journal.load", events[^1]);
    }

    [Fact]
    public async Task AuthenticatedLoopProcessesFramesUntilTheSuccessfulClearReceipt()
    {
        List<string> events = [];
        InstallerTransactionSnapshot machine = StateAt(
            InstallerOperation.Install,
            InstallerTransactionPhase.MachineCommitted);
        InstallerTransactionSnapshot verified = InstallerTransactionSnapshot.Create(
            machine.Journal.TransitionTo(InstallerTransactionPhase.Verified));
        InstallerMachineHelperCommand verify = Command(
            InstallerMachineHelperVerb.Verify,
            machine);
        InstallerMachineHelperCommand clear = Command(
            InstallerMachineHelperVerb.Clear,
            verified);
        var store = new MemoryInstallerTransactionStore(events, machine.Journal);
        var operations = new RecordingOperations(events);
        InstallerMachineHelperAuthoritySession authority = await
            InstallerMachineHelperAuthoritySession.CreateAsync(
                verify.ToInvocation(),
                store,
                operations,
                CancellationToken.None);
        using var input = new MemoryStream();
        await InstallerMachineHelperFraming.WriteCommandAsync(
            input,
            verify,
            CancellationToken.None);
        await InstallerMachineHelperFraming.WriteCommandAsync(
            input,
            clear,
            CancellationToken.None);
        input.Position = 0;
        using var output = new MemoryStream();
        await using var stream = new SplitDuplexStream(input, output);

        await InstallerMachineHelperAuthorityLoop.RunAsync(
            stream,
            authority,
            CancellationToken.None);

        Assert.Null(store.Current);
        output.Position = 0;
        InstallerMachineHelperResult verifyResult = await
            InstallerMachineHelperFraming.ReadResultAsync(
                output,
                CancellationToken.None);
        InstallerMachineHelperResult clearResult = await
            InstallerMachineHelperFraming.ReadResultAsync(
                output,
                CancellationToken.None);
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, verifyResult.Outcome);
        Assert.Equal(verified, verifyResult.ValidateAgainst(verify));
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, clearResult.Outcome);
        Assert.Equal(verified, clearResult.ValidateAgainst(clear));
        Assert.Equal(output.Length, output.Position);
    }

    private static InstallerMachineHelperCommand Command(
        InstallerMachineHelperVerb verb,
        InstallerTransactionSnapshot state)
    {
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(verb, state);
        return InstallerMachineHelperCommand.Create(invocation, state);
    }

    private static InstallerTransactionSnapshot StateAt(
        InstallerOperation operation,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal(operation);
        InstallerTransactionPhase[] order = operation == InstallerOperation.Uninstall
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

    private sealed class RecordingOperations : IInstallerMachineHelperOperationExecutor
    {
        private readonly List<string> _events;
        private readonly Func<
            InstallerMachineHelperCommand,
            InstallerMachineHelperSessionDisposition,
            CancellationToken,
            Task>? _action;

        internal RecordingOperations(
            List<string> events,
            Func<
                InstallerMachineHelperCommand,
                InstallerMachineHelperSessionDisposition,
                CancellationToken,
                Task>? action = null)
        {
            _events = events;
            _action = action;
        }

        public Task ExecuteAsync(
            InstallerMachineHelperCommand command,
            InstallerMachineHelperSessionDisposition disposition,
            CancellationToken cancellationToken)
        {
            command.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"operation:{command.Verb}:{disposition}");
            return _action?.Invoke(command, disposition, cancellationToken)
                ?? Task.CompletedTask;
        }
    }

    private sealed class SplitDuplexStream : Stream
    {
        private readonly Stream _input;
        private readonly Stream _output;

        internal SplitDuplexStream(Stream input, Stream output)
        {
            _input = input;
            _output = output;
        }

        public override bool CanRead => _input.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _output.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

    }
}
