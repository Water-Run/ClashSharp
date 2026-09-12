using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperAuthorityLoopCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallClearAwaitsCleanupAfterAuthorityFinishesAndBeforeAnyReply(bool committedReplay)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(InstallerOperation.Uninstall);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, committedReplay ? null : verified.Journal);
        var operations = new RecordingOperations(events);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(clear, store, operations);
        using var stream = new DuplexStream();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallerDirectoryCleanupReport report = DeletedReport();
        int calls = 0;

        Task running = InstallerMachineHelperAuthorityLoop.RunAsync(
            stream, authority, clear, async (command, result, token) =>
            {
                Assert.Equal(clear, command);
                Assert.Equal(verified, result.ValidateAgainst(command));
                Assert.Equal(cancellation.Token, token);
                Assert.Null(store.Current);
                Assert.Equal("journal.load", events[^1]);
                Assert.Equal(committedReplay, !events.Contains("journal.clear"));
                Assert.Contains(committedReplay ? "operation:VerifyCommittedReplay" : "operation:Execute", events);
                Assert.Equal(0, stream.Output.Length);
                calls++;
                // The real finalizer releases these resources. Any subsequent authority read fails.
                store.LoadAction = static _ => throw new ObjectDisposedException("terminal resources");
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return report;
            }, cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.Equal(0, stream.Output.Length);
        }
        finally
        {
            release.TrySetResult();
        }
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.Null(store.Current);
        Assert.DoesNotContain(events, static value => value.StartsWith("journal.save:", StringComparison.Ordinal));
        InstallerMachineHelperResult reply = await ReadReplyAsync(stream);
        Assert.Equal(verified, reply.ValidateAgainst(clear));
        Assert.Equal(report, reply.DirectoryCleanupReport);
        Assert.Equal(stream.Output.Length, stream.Output.Position);
    }

    [Fact]
    public async Task CleanupFailureAfterClearPropagatesWithoutSendingSuccess()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(InstallerOperation.Uninstall);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, verified.Journal);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(clear, store, new RecordingOperations(events));
        using var stream = new DuplexStream();
        var failure = new IOException("Injected terminal ledger deletion failure.");

        IOException actual = await Assert.ThrowsAsync<IOException>(() => InstallerMachineHelperAuthorityLoop.RunAsync(
            stream, authority, clear, (_, _, _) => throw failure, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Null(store.Current);
        Assert.Contains("journal.clear", events);
        Assert.Equal(0, stream.Output.Length);
    }

    [Fact]
    public async Task InvalidCleanupReportConstructionDoesNotSendAReply()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(InstallerOperation.Uninstall);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, verified.Journal);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(clear, store, new RecordingOperations(events));
        using var stream = new DuplexStream();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            InstallerMachineHelperAuthorityLoop.RunAsync(stream, authority, clear,
                static (_, _, _) => Task.FromResult<InstallerDirectoryCleanupReport?>(new([])), CancellationToken.None));

        Assert.Equal("installer.directory_cleanup.report_invalid", failure.DiagnosticCode);
        Assert.Null(store.Current);
        Assert.Equal(0, stream.Output.Length);
    }

    [Fact]
    public async Task CancellationDuringCleanupDoesNotSendAReply()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(InstallerOperation.Uninstall);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, verified.Journal);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(clear, store, new RecordingOperations(events));
        using var stream = new DuplexStream();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallerMachineHelperAuthorityLoop.RunAsync(
            stream, authority, clear, (_, _, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<InstallerDirectoryCleanupReport?>(null);
            }, cancellation.Token));

        Assert.Null(store.Current);
        Assert.Equal(0, stream.Output.Length);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task InstallAndRepairClearDoNotInvokeDirectoryCleanup(InstallerOperation operation)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(operation);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, verified.Journal);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(clear, store, new RecordingOperations(events));
        using var stream = new DuplexStream();

        await InstallerMachineHelperAuthorityLoop.RunAsync(stream, authority, clear,
            static (_, _, _) => throw new InvalidOperationException("Unexpected uninstall cleanup."), CancellationToken.None);

        InstallerMachineHelperResult reply = await ReadReplyAsync(stream);
        Assert.Equal(verified, reply.ValidateAgainst(clear));
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, reply.Outcome);
        Assert.Null(reply.DirectoryCleanupReport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedClearDoesNotInvokeDirectoryCleanupOrClaimSuccess(bool committedReplay)
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(InstallerOperation.Uninstall);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, committedReplay ? null : verified.Journal);
        var operations = new RecordingOperations(events, fail: true);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(clear, store, operations);
        using var stream = new DuplexStream();

        // A failed response does not terminate the protocol; this input ends after that response.
        await Assert.ThrowsAsync<EndOfStreamException>(() => InstallerMachineHelperAuthorityLoop.RunAsync(
            stream, authority, clear,
            static (_, _, _) => throw new InvalidOperationException("Unexpected cleanup after failed verification."),
            CancellationToken.None));

        InstallerMachineHelperResult reply = await ReadReplyAsync(stream);
        Assert.Equal(committedReplay ? InstallerMachineHelperOutcome.PostconditionFailed : InstallerMachineHelperOutcome.Failed,
            reply.Outcome);
        Assert.Null(reply.DirectoryCleanupReport);
        Assert.Equal(verified, reply.ValidateAgainst(clear));
        Assert.DoesNotContain("journal.clear", events);
    }

    [Fact]
    public async Task UninstallVerifySkipsCleanupUntilItsFollowingClear()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(InstallerOperation.Uninstall);
        InstallerTransactionSnapshot package = InstallerTransactionSnapshot.Create(verified.Journal with
        {
            Phase = InstallerTransactionPhase.PackageCommitted,
            Generation = verified.Journal.Generation - 1,
        });
        InstallerMachineHelperCommand verify = Command(InstallerMachineHelperVerb.Verify, package);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, package.Journal);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(verify, store, new RecordingOperations(events));
        using var stream = new DuplexStream();
        await InstallerMachineHelperFraming.WriteCommandAsync(stream.Input, clear, CancellationToken.None);
        stream.Input.Position = 0;
        int calls = 0;

        await InstallerMachineHelperAuthorityLoop.RunAsync(stream, authority, verify, (command, _, _) =>
        {
            Assert.Equal(clear, command);
            Assert.True(stream.Output.Length > 0);
            calls++;
            return Task.FromResult<InstallerDirectoryCleanupReport?>(DeletedReport());
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        InstallerMachineHelperResult verifyReply = await ReadReplyAsync(stream);
        Assert.Equal(verified, verifyReply.ValidateAgainst(verify));
        Assert.Null(verifyReply.DirectoryCleanupReport);
        InstallerMachineHelperResult clearReply = await InstallerMachineHelperFraming.ReadResultAsync(stream.Output, CancellationToken.None);
        Assert.Equal(verified, clearReply.ValidateAgainst(clear));
        Assert.Equal(DeletedReport(), clearReply.DirectoryCleanupReport);
    }

    [Fact]
    public async Task OptionalNullCleanupReportKeepsTheExistingClearReceipt()
    {
        List<string> events = [];
        InstallerTransactionSnapshot verified = VerifiedState(InstallerOperation.Uninstall);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        var store = new MemoryInstallerTransactionStore(events, verified.Journal);
        InstallerMachineHelperAuthoritySession authority = await CreateAuthorityAsync(clear, store, new RecordingOperations(events));
        using var stream = new DuplexStream();

        await InstallerMachineHelperAuthorityLoop.RunAsync(stream, authority, clear,
            static (_, _, _) => Task.FromResult<InstallerDirectoryCleanupReport?>(null), CancellationToken.None);

        InstallerMachineHelperResult reply = await ReadReplyAsync(stream);
        Assert.Equal(InstallerMachineHelperResult.Succeeded(clear, verified), reply);
    }

    private static Task<InstallerMachineHelperAuthoritySession> CreateAuthorityAsync(
        InstallerMachineHelperCommand command, MemoryInstallerTransactionStore store, RecordingOperations operations) =>
        InstallerMachineHelperAuthoritySession.CreateAsync(command.ToInvocation(), store, operations, CancellationToken.None);

    private static InstallerDirectoryCleanupReport DeletedReport() =>
        new(Enum.GetValues<InstallerDirectoryRole>().Select(static role =>
            new InstallerDirectoryCleanupEntry(role, InstallerDirectoryCleanupDisposition.Deleted)));

    private static InstallerMachineHelperCommand Command(InstallerMachineHelperVerb verb, InstallerTransactionSnapshot state) =>
        InstallerMachineHelperCommand.Create(InstallerMachineHelperInvocation.Create(verb, state), state);

    private static InstallerTransactionSnapshot VerifiedState(InstallerOperation operation) =>
        InstallerTransactionSnapshot.Create(InstallerTestData.Journal(operation, InstallerTransactionPhase.Verified, generation: 5));

    private static Task<InstallerMachineHelperResult> ReadReplyAsync(DuplexStream stream)
    {
        stream.Output.Position = 0;
        return InstallerMachineHelperFraming.ReadResultAsync(stream.Output, CancellationToken.None);
    }

    private sealed class RecordingOperations(List<string> events, bool fail = false) : IInstallerMachineHelperOperationExecutor
    {
        public Task ExecuteAsync(InstallerMachineHelperCommand command,
            InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"operation:{disposition}");
            if (fail) { throw new InstallerProtocolException("installer.test.native_residue_present"); }
            return Task.CompletedTask;
        }
    }

    private sealed class DuplexStream : Stream
    {
        internal MemoryStream Input { get; } = new();
        internal MemoryStream Output { get; } = new();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => Output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => Output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Input.Dispose();
                Output.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
