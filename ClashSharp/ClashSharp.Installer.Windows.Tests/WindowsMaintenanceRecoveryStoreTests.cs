using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceRecoveryStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstObservationAndUnavailableMarkerRoundTripAndReplayWithoutRewriting(bool available)
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        WindowsMaintenanceRecoveryRecord captured = fixture.Captured(available);

        await store.SaveAsync(null, captured, CancellationToken.None);
        await store.SaveAsync(null, captured, CancellationToken.None);
        WindowsMaintenanceRecoveryRecord? read = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(captured.Intent, read?.Intent);
        Assert.Equal(available, read?.Original is not null);
        Assert.Equal(1, fixture.PrivateFiles.Writes);
        fixture.PrivateFiles.AssertBuffersCleared();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostWriteAcknowledgementUsesObservedState(bool committed)
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        fixture.PrivateFiles.ApplyWrite = committed;
        fixture.PrivateFiles.FailWrite = true;

        Exception? failure = await Record.ExceptionAsync(() => store.SaveAsync(null, fixture.Captured(), CancellationToken.None));

        if (committed) { Assert.Null(failure); Assert.NotNull(fixture.Private); }
        else { Assert.IsType<InstallerStateUncertainException>(failure); Assert.Null(fixture.Private); }
        Assert.Equal(1, fixture.PrivateFiles.Writes);
        fixture.PrivateFiles.AssertBuffersCleared();
    }

    [Fact]
    public async Task CancellationAfterPrivateCommitDoesNotHideTheDurableRecord()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using var cancellation = new CancellationTokenSource();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        fixture.PrivateFiles.AfterWrite = cancellation.Cancel;

        WindowsMaintenanceRecoveryRecord saved = await store.SaveAsync(null, fixture.Captured(), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(saved.Intent, fixture.Private?.Intent);
        fixture.PrivateFiles.AssertBuffersCleared();
    }

    [Fact]
    public async Task AnotherTransactionCannotReplaceTheFirstObservation()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        WindowsMaintenanceRecoveryRecord first = fixture.Captured();
        await store.SaveAsync(null, first, CancellationToken.None);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.SaveAsync(null, fixture.Captured(), CancellationToken.None));

        Assert.Equal(first.Intent, fixture.Private?.Intent);
        Assert.Equal(1, fixture.PrivateFiles.Writes);
    }

    [Fact]
    public async Task ExpiredAuthorityCannotAcknowledgeAnExactReplay()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        WindowsMaintenanceRecoveryRecord first = fixture.Captured();
        await store.SaveAsync(null, first, CancellationToken.None);
        fixture.PrivateFiles.AfterRead = () => fixture.Outer.Expired = true;

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.SaveAsync(null, first, CancellationToken.None));

        Assert.Equal(1, fixture.PrivateFiles.Writes);
        fixture.PrivateFiles.AssertBuffersCleared();
    }

    [Fact]
    public async Task ActivePreparationCannotBeRetiredEvenWhenPublicEvidenceIsMissing()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        WindowsMaintenanceRecoveryRecord captured = fixture.Captured();
        await store.SaveAsync(null, captured, CancellationToken.None);
        var active = captured.Continue(InstallerTransactionSnapshot.Create(captured.Intent));
        await store.SaveAsync(captured, active, CancellationToken.None);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.RetireAsync(active, null, CancellationToken.None));

        Assert.Equal(0, fixture.PrivateFiles.Deletes);
        Assert.Equal(WindowsMaintenanceRecoveryStage.ContinueCandidate, fixture.Private?.Stage);
    }

    [Fact]
    public async Task ClearReadyRecordWaitsForItsPublicTerminalAndThenReconcilesLostDeleteReply()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        WindowsMaintenanceRecoveryRecord captured = fixture.Captured();
        await store.SaveAsync(null, captured, CancellationToken.None);
        var source = InstallerTransactionSnapshot.Create(captured.Intent);
        var selected = await store.SaveAsync(captured, captured.Preserve(source), CancellationToken.None);
        var verified = await store.SaveAsync(selected, selected.VerifyOriginal(), CancellationToken.None);
        var terminal = verified.RestoredTerminal();
        var ready = await store.SaveAsync(verified, verified.PrepareOriginalClear(terminal), CancellationToken.None);
        fixture.Public.Current = terminal;

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.RetireAsync(ready, null, CancellationToken.None));
        Assert.Equal(0, fixture.PrivateFiles.Deletes);
        fixture.Public.Current = null;
        fixture.PrivateFiles.FailDelete = true;
        await store.RetireAsync(ready, null, CancellationToken.None);

        Assert.Null(fixture.Private);
        Assert.Equal(1, fixture.PrivateFiles.Deletes);
        fixture.PrivateFiles.AssertBuffersCleared();
    }

    [Fact]
    public async Task CompletedPredecessorCanRetireUnderAnExactDifferentSuccessor()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using WindowsMaintenanceRecoveryStore store = fixture.Store();
        WindowsMaintenanceRecoveryRecord captured = fixture.Captured();
        await store.SaveAsync(null, captured, CancellationToken.None);
        var ready = await store.SaveAsync(captured, captured.PrepareCandidateClear(MaintenanceRecoveryFixture.CandidateTerminal(captured.Intent)), CancellationToken.None);
        var successor = InstallerTransactionSnapshot.Create(InstallerTransactionJournal.Create(fixture.Request));
        fixture.Public.Current = successor;

        await store.RetireAsync(ready, successor, CancellationToken.None);

        Assert.Null(fixture.Private);
        Assert.Equal(successor, fixture.Public.Current);
    }

    [Fact]
    public void RestorationDecisionCannotChangeOriginalEvidenceOrSkipVerification()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        WindowsMaintenanceRecoveryRecord captured = fixture.Captured();
        var source = InstallerTransactionSnapshot.Create(captured.Intent);
        var selected = captured.Preserve(source);
        Assert.Throws<InstallerProtocolException>(() => selected.PrepareOriginalClear(selected.RestoredTerminal()));
        var altered = new WindowsMaintenanceRecoveryRecord(1, captured.Intent, null, "installer.recovery.original_baseline_missing",
            WindowsMaintenanceRecoveryStage.ContinueCandidate, 2, null);
        Assert.Throws<InstallerProtocolException>(() => altered.RequireSuccessorOf(captured));
        var resumed = selected.Continue(source);
        Assert.Same(captured.Original, resumed.Original);
        Assert.Null(resumed.RestorationSource);
        Assert.False(resumed.CanRetire);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("stage")]
    public void PrivateRecordRejectsAmbiguousOrUnsupportedJson(string change)
    {
        using var fixture = new MaintenanceRecoveryFixture();
        byte[] bytes = fixture.Captured().Serialize();
        string json = Encoding.UTF8.GetString(bytes);
        string altered = change switch
        {
            "unknown" => json.Insert(1, "\"extra\":true,"),
            "duplicate" => json.Insert(1, "\"schema\":1,"),
            "missing" => json.Replace("\"generation\":1,", "", StringComparison.Ordinal),
            _ => json.Replace("\"stage\":0", "\"stage\":99", StringComparison.Ordinal),
        };
        byte[] input = Encoding.UTF8.GetBytes(altered);
        try { Assert.Throws<InstallerProtocolException>(() => WindowsMaintenanceRecoveryRecord.Parse(input)); }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(input); }
    }
}
