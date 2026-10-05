using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceRecoveryIntegrationTests
{
    [Fact]
    public async Task CertificatePreflightRejectionDoesNotCaptureOrPublishRecoveryEvidence()
    {
        using var fixture = new MaintenanceRecoveryFixture { CertificateConflict = true };
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);

        var result = await session.ExecuteAsync(prepare, CancellationToken.None);

        Assert.Equal("installer.machine_certificate.ownership_conflict", result.DiagnosticCode);
        Assert.Null(fixture.Public.Current);
        Assert.Equal(0, fixture.Captures);
        Assert.Equal(0, fixture.PrivateFiles.Writes);
        Assert.False(fixture.ServicePrepared);
    }

    [Fact]
    public async Task FailedInitialPrivateCommitCannotPublishIntentOrPrepareTheService()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        fixture.PrivateFiles.ApplyWrite = false;
        fixture.PrivateFiles.FailWrite = true;
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);

        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => session.ExecuteAsync(prepare, CancellationToken.None));

        Assert.Null(fixture.Public.Current);
        Assert.False(fixture.ServicePrepared);
        Assert.DoesNotContain("machine.prepare", fixture.Events);
    }

    [Fact]
    public async Task LostServiceReplyReopensTheFirstBaselineAndAvoidsRepeatingRestorationEffects()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);
        var reserved = (await session.ExecuteAsync(prepare, CancellationToken.None)).ToResultDurableState();
        var restore = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.RestoreOriginal, reserved);
        fixture.LoseRestoreReply = true;

        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => session.ExecuteAsync(restore, CancellationToken.None));

        Assert.Equal(WindowsMaintenanceRecoveryStage.PreserveOriginal, fixture.Private?.Stage);
        Assert.False(fixture.ServicePrepared);
        executor.Dispose();
        using var reopened = fixture.Executor();
        var resumed = await fixture.SessionAsync(restore, reopened);
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, (await resumed.ExecuteAsync(restore, CancellationToken.None)).Outcome);
        Assert.Equal(1, fixture.Captures);
        Assert.Equal(1, fixture.Restores);
    }

    [Fact]
    public async Task OriginalEvidenceAndDecisionsPrecedeEffectsAndPinsCoverBothPublicCommits()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        fixture.Public.BeforeSave = journal =>
        {
            if (journal.Phase == InstallerTransactionPhase.Prepared)
            {
                Assert.Equal(WindowsMaintenanceRecoveryStage.Captured, fixture.Private?.Stage);
                Assert.False(fixture.ServicePrepared);
            }
            if (journal.Phase == InstallerTransactionPhase.OriginalRestored)
            {
                Assert.Equal(WindowsMaintenanceRecoveryStage.OriginalVerified, fixture.Private?.Stage);
                Assert.Equal(1, fixture.ActiveOriginalSessions);
            }
        };
        fixture.Public.BeforeClear = () =>
        {
            Assert.Equal(WindowsMaintenanceRecoveryStage.OriginalClearReady, fixture.Private?.Stage);
            Assert.Equal(1, fixture.ActiveOriginalSessions);
        };
        var session = await fixture.SessionAsync(prepare, executor);

        var reserved = (await session.ExecuteAsync(prepare, CancellationToken.None)).ToResultDurableState();
        Assert.True(fixture.ServicePrepared);
        var restore = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.RestoreOriginal, reserved);
        var restored = (await session.ExecuteAsync(restore, CancellationToken.None)).ToResultDurableState();
        var clear = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.ClearOriginal, restored);
        var result = await session.ExecuteAsync(clear, CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
        Assert.Null(fixture.Public.Current);
        Assert.Equal(1, fixture.Captures);
        Assert.Equal(1, fixture.Restores);
        Assert.Equal(1, fixture.ActiveOriginalSessions);
        executor.Dispose();
        Assert.Equal(0, fixture.ActiveOriginalSessions);
        fixture.PrivateFiles.AssertBuffersCleared();
    }

    [Fact]
    public async Task DamagedOriginalStillRepairsButCannotClaimPreservation()
    {
        using var fixture = new MaintenanceRecoveryFixture { OriginalUnavailable = true };
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);

        var reserved = (await session.ExecuteAsync(prepare, CancellationToken.None)).ToResultDurableState();
        var result = await session.ExecuteAsync(MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.RestoreOriginal, reserved), CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Failed, result.Outcome);
        Assert.Equal("installer.recovery.original_baseline_unavailable", result.DiagnosticCode);
        Assert.Null(fixture.Private?.Original);
        Assert.True(fixture.ServicePrepared);
        Assert.Equal(0, fixture.Restores);
        Assert.Equal(reserved, fixture.Public.Current);
    }

    [Fact]
    public async Task LegacyPreparedReplayCannotRecaptureAnAlreadyPreparedService()
    {
        using var fixture = new MaintenanceRecoveryFixture { ServicePrepared = true };
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        fixture.Public.Current = prepare.ToDurableState();
        var session = await fixture.SessionAsync(prepare, executor);

        await session.ExecuteAsync(prepare, CancellationToken.None);

        Assert.Equal(0, fixture.Captures);
        Assert.Null(fixture.Private?.Original);
        Assert.Equal("installer.recovery.original_baseline_missing", fixture.Private?.UnavailableDiagnostic);
    }

    [Fact]
    public async Task CancelledPublicCommitReopensWithReadOnlyOriginalVerification()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        var executor = fixture.Executor();
        using var cancellation = new CancellationTokenSource();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);
        var reserved = (await session.ExecuteAsync(prepare, CancellationToken.None)).ToResultDurableState();
        var restore = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.RestoreOriginal, reserved);
        fixture.CancelAfterRestore = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(restore, cancellation.Token));

        Assert.Equal(WindowsMaintenanceRecoveryStage.OriginalVerified, fixture.Private?.Stage);
        Assert.Equal(reserved, fixture.Public.Current);
        executor.Dispose();
        fixture.CancelAfterRestore = null;
        using var reopened = fixture.Executor();
        var recoveredSession = await fixture.SessionAsync(restore, reopened);
        var result = await recoveredSession.ExecuteAsync(restore, CancellationToken.None);
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, fixture.Restores);
        Assert.Equal(1, fixture.Captures);
    }

    [Fact]
    public async Task LostClearReplyReopensPrivateCompletionAndKeepsOriginalPinsUntilHelperExit()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);
        var reserved = (await session.ExecuteAsync(prepare, CancellationToken.None)).ToResultDurableState();
        var restore = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.RestoreOriginal, reserved);
        var terminal = (await session.ExecuteAsync(restore, CancellationToken.None)).ToResultDurableState();
        var clear = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.ClearOriginal, terminal);
        fixture.Public.LoseClearReply = true;

        await Assert.ThrowsAsync<IOException>(() => session.ExecuteAsync(clear, CancellationToken.None));

        Assert.Null(fixture.Public.Current);
        Assert.Equal(WindowsMaintenanceRecoveryStage.OriginalClearReady, fixture.Private?.Stage);
        executor.Dispose();
        using var reopened = fixture.Executor();
        var recoveredSession = await fixture.SessionAsync(clear, reopened);
        Assert.Equal(InstallerMachineHelperOutcome.Succeeded, (await recoveredSession.ExecuteAsync(clear, CancellationToken.None)).Outcome);
        Assert.Equal(1, fixture.Restores);
        Assert.Equal(1, fixture.ActiveOriginalSessions);
    }

    [Fact]
    public async Task ChangedOriginalFailsBeforeServiceEffectsAndBeforeRecordingPreservation()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);
        var reserved = (await session.ExecuteAsync(prepare, CancellationToken.None)).ToResultDurableState();
        fixture.OriginalChanged = true;

        var result = await session.ExecuteAsync(MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.RestoreOriginal, reserved), CancellationToken.None);

        Assert.Equal(InstallerMachineHelperOutcome.Failed, result.Outcome);
        Assert.Equal(WindowsMaintenanceRecoveryStage.ContinueCandidate, fixture.Private?.Stage);
        Assert.Equal(0, fixture.Restores);
        Assert.Equal(reserved, fixture.Public.Current);
    }

    [Fact]
    public async Task ExplicitCandidateContinuationReleasesOriginalPinsAndFinishesPrivateEvidence()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);
        var reserved = (await session.ExecuteAsync(prepare, CancellationToken.None)).ToResultDurableState();
        var restore = MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.RestoreOriginal, reserved);
        fixture.LoseRestoreReply = true;
        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => session.ExecuteAsync(restore, CancellationToken.None));
        Assert.Equal(WindowsMaintenanceRecoveryStage.PreserveOriginal, fixture.Private?.Stage);
        Assert.Equal(1, fixture.ActiveOriginalSessions);
        await session.ExecuteAsync(MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.ContinueCandidate, reserved), CancellationToken.None);
        Assert.Equal(0, fixture.ActiveOriginalSessions);
        Assert.True(fixture.ServicePrepared);
        fixture.CandidateInstalled = true;

        var package = (await session.ExecuteAsync(MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.CommitPackage, reserved), CancellationToken.None)).ToResultDurableState();
        Assert.Equal(0, fixture.ActiveOriginalSessions);
        var machine = (await session.ExecuteAsync(MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.Apply, package), CancellationToken.None)).ToResultDurableState();
        var terminal = (await session.ExecuteAsync(MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.Verify, machine), CancellationToken.None)).ToResultDurableState();
        fixture.Public.BeforeClear = () => Assert.Equal(WindowsMaintenanceRecoveryStage.CandidateClearReady, fixture.Private?.Stage);
        await session.ExecuteAsync(MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.Clear, terminal), CancellationToken.None);

        Assert.Null(fixture.Public.Current);
        Assert.Equal(WindowsMaintenanceRecoveryStage.CandidateClearReady, fixture.Private?.Stage);
        Assert.Equal(1, fixture.Captures);
    }

    [Fact]
    public async Task UnusedCaptureAndCompletedRecordDoNotBlockTheNextRepair()
    {
        using var fixture = new MaintenanceRecoveryFixture();
        WindowsMaintenanceRecoveryRecord previous = fixture.Captured();
        fixture.PrivateFiles.Bytes = previous.Serialize();
        using var executor = fixture.Executor();
        var prepare = Initial(fixture);
        var session = await fixture.SessionAsync(prepare, executor);

        await session.ExecuteAsync(prepare, CancellationToken.None);

        Assert.Equal(1, fixture.PrivateFiles.Deletes);
        Assert.Equal(prepare.TransactionId, fixture.Private?.Intent.TransactionId);
        Assert.Equal(1, fixture.Captures);
        Assert.True(fixture.ServicePrepared);
    }

    private static InstallerMachineHelperCommand Initial(MaintenanceRecoveryFixture fixture) =>
        MaintenanceRecoveryFixture.Command(InstallerMachineHelperVerb.Prepare,
            InstallerTransactionSnapshot.Create(InstallerTransactionJournal.Create(fixture.Request)));
}
