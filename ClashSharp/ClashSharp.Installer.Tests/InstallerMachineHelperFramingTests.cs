using System.Buffers.Binary;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineHelperFramingTests
{
    [Fact]
    public async Task MultipleCommandsRoundTripOnOnePersistentStream()
    {
        InstallerMachineHelperCommand firstCommand = Command(
            InstallerMachineHelperVerb.Prepare,
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand secondCommand = Command(
            InstallerMachineHelperVerb.CommitPackage,
            InstallerTransactionPhase.MachineReserved);
        InstallerMachineHelperCommand thirdCommand = Command(
            InstallerMachineHelperVerb.Apply,
            InstallerTransactionPhase.PackageCommitted);
        InstallerMachineHelperInvocation first = firstCommand.ToInvocation();
        InstallerMachineHelperInvocation second = secondCommand.ToInvocation();
        InstallerMachineHelperInvocation third = thirdCommand.ToInvocation();
        using var stream = new MemoryStream();

        await InstallerMachineHelperFraming.WriteCommandAsync(
            stream,
            firstCommand,
            CancellationToken.None);
        await InstallerMachineHelperFraming.WriteCommandAsync(
            stream,
            secondCommand,
            CancellationToken.None);
        await InstallerMachineHelperFraming.WriteCommandAsync(
            stream,
            thirdCommand,
            CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(
            first,
            (await InstallerMachineHelperFraming.ReadCommandAsync(
                stream,
                CancellationToken.None)).ToInvocation());
        Assert.Equal(
            second,
            (await InstallerMachineHelperFraming.ReadCommandAsync(
                stream,
                CancellationToken.None)).ToInvocation());
        Assert.Equal(
            third,
            (await InstallerMachineHelperFraming.ReadCommandAsync(
                stream,
                CancellationToken.None)).ToInvocation());
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalResultsRoundTripWithoutClosingTheStream(bool succeeded)
    {
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            InstallerTransactionPhase.Prepared);
        InstallerMachineHelperResult expected = succeeded
            ? InstallerMachineHelperResult.Succeeded(
                command,
                InstallerTransactionSnapshot.Create(
                    command.ToDurableState().Journal.TransitionTo(
                        InstallerTransactionPhase.MachineReserved)))
            : InstallerMachineHelperResult.Failed(
                command,
                "installer.machine.prepare_failed");
        using var stream = new MemoryStream();

        await InstallerMachineHelperFraming.WriteResultAsync(
            stream,
            expected,
            CancellationToken.None);
        stream.Position = 0;
        InstallerMachineHelperResult actual =
            await InstallerMachineHelperFraming.ReadResultAsync(
                stream,
                CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal(
            expected.ValidateAgainst(command),
            actual.ValidateAgainst(command));
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public async Task InvalidFrameLengthsAreRejectedBeforeAllocation(int length)
    {
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        using var stream = new MemoryStream(header);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                InstallerMachineHelperFraming.ReadCommandAsync(
                    stream,
                    CancellationToken.None));

        Assert.Equal("installer.machine_helper.frame_size_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task TruncatedHeaderAndPayloadRemainTransportFailures()
    {
        using var header = new MemoryStream([0, 0, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            InstallerMachineHelperFraming.ReadCommandAsync(
                header,
                CancellationToken.None));

        using var payload = new MemoryStream([0, 0, 0, 4, (byte)'{']);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            InstallerMachineHelperFraming.ReadCommandAsync(
                payload,
                CancellationToken.None));
    }

    [Fact]
    public async Task PreCancelledReadAndWriteDoNotTouchTheStream()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream();
        InstallerMachineHelperCommand command = Command(
            InstallerMachineHelperVerb.Prepare,
            InstallerTransactionPhase.Prepared);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallerMachineHelperFraming.WriteCommandAsync(
                stream,
                command,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallerMachineHelperFraming.ReadCommandAsync(
                stream,
                cancellation.Token));

        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Position);
    }

    private static InstallerMachineHelperCommand Command(
        InstallerMachineHelperVerb verb,
        InstallerTransactionPhase phase)
    {
        InstallerTransactionSnapshot durableState = DurableState(phase);
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(verb, durableState);
        return InstallerMachineHelperCommand.Create(invocation, durableState);
    }

    private static InstallerTransactionSnapshot DurableState(
        InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal();
        InstallerTransactionPhase[] order =
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

        return InstallerTransactionSnapshot.Create(journal);
    }
}
