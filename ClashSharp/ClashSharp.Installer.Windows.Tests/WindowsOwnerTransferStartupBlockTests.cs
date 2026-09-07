using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferStartupBlockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishesOnlyTheExactPreparedBarrierOrAcceptsItsReplay(bool existing)
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();
        if (existing)
        {
            fixture.Native.Add(WindowsOwnerTransferAccessFixture.ContinuationPath, false, true).Bytes =
                InstallerTransactionCodec.Serialize(fixture.Journal.Continuation);
        }

        await fixture.Startup.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None);

        Assert.Equal(existing ? 0 : 1, fixture.Ordinary.Saves);
        Assert.True(fixture.Backend.Present);
        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Equal(InstallerTransactionSnapshot.Create(fixture.Journal.Continuation), await fixture.Ordinary.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.Equal(InstallerOwnerTransferPhase.Prepared, fixture.Journal.Phase);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    [InlineData("unknown-child")]
    [InlineData("private-acl")]
    [InlineData("target-certificate-mismatch")]
    [InlineData("legacy-marker")]
    [InlineData("other-transaction")]
    [InlineData("hardlink")]
    [InlineData("partial-machine-transfer")]
    public async Task RefusesBeforeWritingOrRemovingServiceWhenInitialEvidenceIsUnsafe(string failure)
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();
        switch (failure)
        {
            case "legacy-marker":
                fixture.Native.Add(Path.Combine(WindowsOwnerTransferAccessFixture.Installer, "transaction.json"), false, true);
                break;
            case "other-transaction":
                fixture.Native.Add(WindowsOwnerTransferAccessFixture.ContinuationPath, false, true).Bytes =
                    InstallerTransactionCodec.Serialize(fixture.Journal.Continuation with { TransactionId = new string('f', 64) });
                break;
            case "hardlink": fixture.Native.Entries[fixture.State.Plan.ActivePath].Links = 2; break;
            case "partial-machine-transfer": fixture.State.Transfer(WindowsOwnerTransferAccessFixture.Product); break;
            default: fixture.State.ChangeEvidence(failure); break;
        }

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Startup.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.Equal(0, fixture.Ordinary.Saves);
        Assert.True(fixture.Backend.Present);
        Assert.Empty(fixture.Mutations);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Fact]
    public async Task ReconcilesACommittedPreparedWriteWhoseAcknowledgementWasLost()
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();
        fixture.Ordinary.Failure = "after";

        await fixture.Startup.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None);

        Assert.Equal(1, fixture.Ordinary.Saves);
        Assert.Single(fixture.Mutations);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("no-change")]
    public async Task AnUnprovedWriteCannotAdvancePrivateState(string failure)
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();
        fixture.Ordinary.Failure = failure;

        var error = await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            fixture.Startup.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.startup_state_uncertain", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.True(fixture.Backend.Present);
        Assert.Empty(fixture.Mutations);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Fact]
    public async Task CancellationDrainsWriteAndUncancelledReconciliationBeforeReleasingTheTree()
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ordinary.BeforeSave = _ =>
        {
            entered.TrySetResult();
            return drain.Task;
        };
        fixture.Ordinary.BeforeRead = (count, token) =>
        {
            if (count == 2)
            {
                Assert.False(token.CanBeCanceled);
                Assert.True(fixture.Native.LiveLeases > 0);
            }
            return Task.CompletedTask;
        };
        Task pending = fixture.Startup.ApplyAndVerifyAsync(fixture.Journal, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.True(fixture.Native.LiveLeases > 0);
        }
        finally
        {
            cancellation.Cancel();
            drain.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(2, fixture.Ordinary.Reads);
        Assert.Single(fixture.Mutations);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProfileChangeCannotAcknowledgeThePublishedBarrier(bool previous)
    {
        using var fixture = new WindowsOwnerTransferExecutorFixture();
        fixture.State.Machine.Release.PostVerification = _ =>
        {
            fixture.State.Machine.Failure = previous ? "previous-profile-changed" : "next-profile-changed";
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Startup.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.access_profile_changed", error.DiagnosticCode);
        Assert.Single(fixture.Mutations);
        Assert.True(fixture.Backend.Present);
        Assert.Equal(InstallerOwnerTransferPhase.Prepared, fixture.Journal.Phase);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }
}
