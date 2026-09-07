using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferCompletionTests
{
    [Theory]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred, false, false)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred, true, true)]
    [InlineData(InstallerOwnerTransferPhase.Verified, false, true)]
    [InlineData(InstallerOwnerTransferPhase.Verified, true, false)]
    public async Task CompletedObservationHasNoWritablePortAndPreservesBothJournals(
        InstallerOwnerTransferPhase phase, bool previous, bool next)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture(previous, next);
        fixture.SetCompleted(phase);
        var journal = fixture.Journal;
        var certificates = fixture.Certificates.State;

        await fixture.VerifyAsync();

        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(journal, fixture.Journal);
        Assert.Equal(certificates, fixture.Certificates.State);
        Assert.Equal(2, fixture.Certificates.Reads);
        Assert.Equal(2, fixture.Machine.Release.Calls);
        Assert.Equal(2, fixture.Machine.Backend.ServiceChecks);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Machine.Release.Disposed);
        using IWindowsOwnerTransferCertificateBoundary boundary =
            WindowsOwnerTransferAccessTree.AcquireForCompletedTransfer(fixture.Plan, fixture.Native, CancellationToken.None);
        InstallerProtocolException error = Assert.Throws<InstallerProtocolException>(() =>
            ((WindowsOwnerTransferAccessTree)boundary).ApplyAndVerify(CancellationToken.None));
        Assert.Equal("installer.owner_transfer.access_read_only", error.DiagnosticCode);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    [InlineData("service-present")]
    [InlineData("association")]
    [InlineData("barrier")]
    [InlineData("active-ledger")]
    [InlineData("previous-archive")]
    [InlineData("next-archive")]
    [InlineData("private-acl")]
    [InlineData("shared-acl")]
    [InlineData("unknown-child")]
    [InlineData("target-certificate-mismatch")]
    public async Task CannotDeclareCompletionWithChangedEvidence(string failure)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.SetCompleted();
        fixture.ChangeEvidence(failure);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.VerifyAsync());

        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryInstallerLeafMustAlreadyHaveExactNextOwnerAccess(int index)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.SetCompleted();
        var entry = fixture.Native.Entries[fixture.InstallerPaths[index]];
        entry.Security = WindowsOwnerTransferAccessFixture.OwnerSecurity(
            WindowsOwnerTransferAccessFixture.PreviousSid, entry.Directory, entry.Inherited);

        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.VerifyAsync());

        Assert.Equal("installer.owner_transfer.access_acl_invalid", error.DiagnosticCode);
        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("association")]
    [InlineData("barrier")]
    [InlineData("previous-archive")]
    [InlineData("private-acl")]
    [InlineData("unknown-child")]
    public async Task RechecksPinnedEvidenceAfterTheLastOwnedRead(string failure)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.SetCompleted();
        fixture.Certificates.BeforeRead = (count, _) =>
        {
            if (count == 2)
            {
                fixture.ChangeEvidence(failure);
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.VerifyAsync());

        Assert.Equal(2, fixture.Certificates.Reads);
        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Fact]
    public async Task CancellationDuringFinalReadRetainsAllBoundariesUntilItDrains()
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.SetCompleted();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Certificates.BeforeRead = (count, _) =>
        {
            if (count != 2)
            {
                return Task.CompletedTask;
            }
            entered.TrySetResult();
            return drain.Task;
        };
        Task pending = fixture.VerifyAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Equal(fixture.Native.Opened.Count, fixture.Native.LiveLeases);
        }
        finally
        {
            cancellation.Cancel();
            drain.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.Empty(fixture.Native.WritableOpened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancellationDoesNotAcquireAccess(bool completion)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        if (completion)
        {
            fixture.SetCompleted();
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            completion ? fixture.VerifyAsync(cancellation.Token) : fixture.ApplyAsync(cancellation.Token));

        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Certificates.Reads);
        Assert.Equal(0, fixture.Machine.Release.Calls);
    }

    public static TheoryData<InstallerOwnerTransferPhase> Phases => new(Enum.GetValues<InstallerOwnerTransferPhase>());

    [Theory]
    [MemberData(nameof(Phases))]
    public async Task StepsRejectEveryWrongPhaseBeforeAnyFileRead(InstallerOwnerTransferPhase phase)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.Journal = fixture.Journal with { Phase = phase, Generation = (int)phase + 1 };
        if (phase != InstallerOwnerTransferPhase.CertificateStateTransferred)
        {
            var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());
            Assert.Equal("installer.owner_transfer.installer_access_phase_invalid", error.DiagnosticCode);
        }
        if (phase is not (InstallerOwnerTransferPhase.InstallerAccessTransferred or InstallerOwnerTransferPhase.Verified))
        {
            var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.VerifyAsync());
            Assert.Equal("installer.owner_transfer.completion_phase_invalid", error.DiagnosticCode);
        }
        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Machine.Release.Calls);
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.AssociationTransferred, true, false)]
    [InlineData(InstallerOwnerTransferPhase.CertificateStateTransferred, true, true)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred, false, true)]
    [InlineData(InstallerOwnerTransferPhase.Verified, false, true)]
    public void ActiveCertificateAclObservationFollowsItsDurablePhase(
        InstallerOwnerTransferPhase phase, bool previousAccepted, bool nextAccepted)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.Journal = fixture.Journal with { Phase = phase, Generation = (int)phase + 1 };
        fixture.Plan.Validate();

        Assert.Equal(previousAccepted, fixture.Plan.HasActiveFileAccess(WindowsOwnerTransferAccessFixture.OwnerSecurity(
            WindowsOwnerTransferAccessFixture.PreviousSid, false, true)));
        Assert.Equal(nextAccepted, fixture.Plan.HasActiveFileAccess(WindowsOwnerTransferAccessFixture.OwnerSecurity(
            WindowsOwnerTransferAccessFixture.NextSid, false, true)));
        Assert.False(fixture.Plan.HasActiveFileAccess(WindowsOwnerTransferAccessFixture.Parse("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;WD)")));
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.CertificateStateTransferred)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.Verified)]
    public async Task LaterReadPhasesNeverEnableNativeCertificateMutation(InstallerOwnerTransferPhase phase)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        var initial = new InstallerOwnerTransferCertificateState(fixture.Journal.PreviousCertificateLedger, null, fixture.Journal.NextCertificateLedger);
        var certificateJournal = fixture.Journal with { Phase = InstallerOwnerTransferPhase.AssociationTransferred, Generation = 5 };
        InstallerOwnerTransferCertificateStep step = initial.GetNextStep(certificateJournal)!;
        fixture.Journal = fixture.Journal with { Phase = phase, Generation = (int)phase + 1 };

        // The phase guard must execute before the native port can open these fictional paths.
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsOwnerTransferCertificateFileNative().ApplyAsync(fixture.Plan, initial, step, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.certificate_phase_invalid", error.DiagnosticCode);
        Assert.Empty(fixture.Native.Opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalVerificationRechecksProfilesAfterOwnedCandidateVerification(bool previous)
    {
        using var fixture = new WindowsOwnerTransferFinalStateFixture();
        fixture.SetCompleted();
        fixture.Machine.Release.PostVerification = _ =>
        {
            fixture.Machine.Failure = previous ? "previous-profile-changed" : "next-profile-changed";
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.VerifyAsync());

        Assert.Equal("installer.owner_transfer.access_profile_changed", error.DiagnosticCode);
        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }
}
