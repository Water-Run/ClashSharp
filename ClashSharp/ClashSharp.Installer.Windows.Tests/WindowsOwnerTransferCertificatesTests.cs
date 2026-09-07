using System.ComponentModel;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferCertificatesTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 3)]
    public async Task CopiesExactLiveEvidenceAndRemovesOnlyTheDuplicateTargetArchive(bool previous, bool next, int mutations)
    {
        using var fixture = new Fixture(previous, next);
        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Files.Reads);

        await fixture.ApplyAsync();

        Assert.Equal(mutations, fixture.Files.Actions.Count);
        Assert.Equal(fixture.Journal.PreviousCertificateLedger, fixture.Files.State.PreviousArchive);
        Assert.Equal(fixture.Journal.NextCertificateLedger, fixture.Files.State.Active);
        Assert.Null(fixture.Files.State.NextArchive);
        Assert.Equal(InstallerOwnerTransferPhase.AssociationTransferred, fixture.Journal.Phase);
        Assert.Empty(fixture.Native.Writes);
        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Machine.Release.Disposed);
        await fixture.ApplyAsync();
        Assert.Equal(mutations, fixture.Files.Actions.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ANewStepResumesFromEveryAcknowledgedOrUnacknowledgedFileBoundary(int applied)
    {
        using var fixture = new Fixture();
        for (int index = 0; index < applied; index++)
        {
            fixture.Files.State = fixture.Files.State.GetNextStep(fixture.Journal)!.After;
        }

        await fixture.ApplyAsync();

        Assert.Equal(3 - applied, fixture.Files.Actions.Count);
        Assert.Null(fixture.Files.State.GetNextStep(fixture.Journal));
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    [InlineData("service-present")]
    [InlineData("service-reappeared-before-write")]
    [InlineData("old-association")]
    [InlineData("old-shared-acl")]
    [InlineData("changed-barrier")]
    [InlineData("missing-private-root")]
    [InlineData("private-root-acl")]
    [InlineData("target-certificate-mismatch")]
    public async Task FailedPrerequisitesCannotCopyOrDeleteCertificateEvidence(string failure)
    {
        using var fixture = new Fixture();
        fixture.Machine.Failure = failure;
        switch (failure)
        {
            case "old-association":
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
                    InstallerMachineAssociationCodec.Serialize(fixture.Journal.PreviousOwner.Association);
                break;
            case "old-shared-acl":
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.Product].Security =
                    WindowsOwnerTransferAccessFixture.OwnerSecurity(WindowsOwnerTransferAccessFixture.PreviousSid, true, false);
                break;
            case "changed-barrier":
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                    InstallerTransactionCodec.Serialize(fixture.Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved));
                break;
            case "missing-private-root":
                fixture.Native.Entries.Remove(fixture.Plan.PrivateRoot);
                break;
            case "private-root-acl":
                fixture.Native.Entries[fixture.Plan.PrivateRoot].Security =
                    WindowsOwnerTransferAccessFixture.OwnerSecurity(WindowsOwnerTransferAccessFixture.NextSid, true, false);
                break;
            case "target-certificate-mismatch":
                fixture.Journal = fixture.Journal with
                {
                    NextCertificateLedger = fixture.Journal.NextCertificateLedger! with { CertificateSha256 = new string('f', 64) },
                };
                break;
        }
        var before = fixture.Files.State;

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Equal(before, fixture.Files.State);
        Assert.Empty(fixture.Files.Actions);
        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("io-after", 1)]
    [InlineData("io-after", 2)]
    [InlineData("io-after", 3)]
    [InlineData("win32-after", 2)]
    [InlineData("cancel-after", 2)]
    public async Task EveryCommittedBoundaryCanRecoverLostAcknowledgement(string failure, int mutation)
    {
        using var fixture = new Fixture();
        fixture.Files.Failure = failure;
        fixture.Files.FailAt = mutation;

        await fixture.ApplyAsync();

        Assert.Equal(3, fixture.Files.Actions.Count);
        Assert.Null(fixture.Files.State.GetNextStep(fixture.Journal));
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("io-before", 1)]
    [InlineData("io-before", 2)]
    [InlineData("io-before", 3)]
    [InlineData("no-change", 2)]
    [InlineData("different-state", 2)]
    [InlineData("read-after", 2)]
    public async Task UnprovedMutationStopsBeforeAnyFurtherCopyOrRemoval(string failure, int mutation)
    {
        using var fixture = new Fixture();
        fixture.Files.Failure = failure;
        fixture.Files.FailAt = mutation;

        InstallerStateUncertainException error = await Assert.ThrowsAsync<InstallerStateUncertainException>(() => fixture.ApplyAsync());

        Assert.Equal("installer.owner_transfer.certificate_state_uncertain", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.Equal(mutation, fixture.Files.Actions.Count);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.Equal(InstallerOwnerTransferPhase.AssociationTransferred, fixture.Journal.Phase);
    }

    [Theory]
    [InlineData("release-postcondition")]
    [InlineData("service-postcondition")]
    [InlineData("access-postcondition")]
    [InlineData("association-postcondition")]
    [InlineData("barrier-postcondition")]
    [InlineData("ledger-postcondition")]
    public async Task ChangedFinalEvidenceCannotCompleteTheDurablePhase(string failure)
    {
        using var fixture = new Fixture();
        if (failure == "release-postcondition")
        {
            fixture.Machine.Failure = failure;
        }
        fixture.Machine.Release.PostVerification = _ =>
        {
            switch (failure)
            {
                case "service-postcondition":
                    fixture.Machine.Failure = "service-postcondition";
                    break;
                case "access-postcondition":
                    fixture.Native.Entries[fixture.Plan.PrivateRoot].Security = WindowsOwnerTransferAccessFixture.Parse("O:BAD:P");
                    break;
                case "association-postcondition":
                    fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
                        InstallerMachineAssociationCodec.Serialize(fixture.Journal.PreviousOwner.Association);
                    break;
                case "barrier-postcondition":
                    fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                        InstallerTransactionCodec.Serialize(fixture.Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved));
                    break;
                case "ledger-postcondition":
                    fixture.Files.State = fixture.Files.State with { PreviousArchive = null };
                    break;
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Equal(3, fixture.Files.Actions.Count);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWaitsForOwnedMutationAndUncancelledObservation(bool holdObservation)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Hold()
        {
            entered.TrySetResult();
            return drain.Task;
        }
        if (holdObservation)
        {
            fixture.Files.BeforeRead = (count, token) =>
            {
                if (count != 2)
                {
                    return Task.CompletedTask;
                }
                Assert.False(token.CanBeCanceled);
                return Hold();
            };
        }
        else
        {
            fixture.Files.BeforeApply = _ => Hold();
        }
        Task pending = fixture.ApplyAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Equal(fixture.Native.Opened.Count, fixture.Native.LiveLeases);
            Assert.Contains(fixture.Plan.PrivateRoot, fixture.Native.Opened);
            Assert.Contains(WindowsOwnerTransferAccessFixture.AssociationPath, fixture.Native.Opened);
            Assert.Contains(WindowsOwnerTransferAccessFixture.ContinuationPath, fixture.Native.Opened);
        }
        finally
        {
            cancellation.Cancel();
            drain.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Single(fixture.Files.Actions);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Fact]
    public async Task PreCancellationDoesNotAcquireAnyFileOrDirectory()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ApplyAsync(cancellation.Token));

        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Files.Reads);
        Assert.Equal(0, fixture.Machine.Release.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal WindowsOwnerTransferMachineAccessTests.Fixture Machine { get; } = new();
        internal WindowsOwnerTransferAccessFixture Native => Machine.Native;
        internal InstallerOwnerTransferJournal Journal { get; set; }
        internal WindowsOwnerTransferCertificatePlan Plan { get; }
        internal FakeFiles Files { get; }
        internal WindowsOwnerTransferCertificates Step { get; }

        internal Fixture(bool previous = true, bool next = true)
        {
            InstallerRequest request = WindowsOwnerTransferDeployment.CreateContinuationRequest(Machine.Journal);
            Journal = Machine.Journal with
            {
                Phase = InstallerOwnerTransferPhase.AssociationTransferred,
                Generation = (int)InstallerOwnerTransferPhase.AssociationTransferred + 1,
                PreviousCertificateLedger = previous
                    ? InstallerCertificateOwnershipLedger.Create(request with { TargetSid = WindowsOwnerTransferAccessFixture.PreviousSid },
                        Machine.Release.Release, certificateWasPresent: false) : null,
                NextCertificateLedger = next
                    ? InstallerCertificateOwnershipLedger.Create(request, Machine.Release.Release, certificateWasPresent: true) : null,
            };
            foreach ((string path, WindowsOwnerTransferAccessFixture.Entry entry) in Native.Entries)
            {
                bool shared = (path.StartsWith(@"C:\Program Files\ClashSharp", StringComparison.Ordinal)
                    || path.StartsWith(WindowsOwnerTransferAccessFixture.Product, StringComparison.Ordinal))
                    && !path.StartsWith(WindowsOwnerTransferAccessFixture.Installer, StringComparison.Ordinal)
                    && entry.Security.AccessEntries.Any(ace => ace.Sid == WindowsOwnerTransferAccessFixture.PreviousSid);
                if (shared)
                {
                    entry.Security = WindowsOwnerTransferAccessFixture.OwnerSecurity(
                        WindowsOwnerTransferAccessFixture.NextSid, entry.Directory, entry.Inherited);
                }
            }
            Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
                InstallerMachineAssociationCodec.Serialize(Journal.NextOwner.Association);
            Plan = new(Native.Roots, Journal);
            Native.Add(Plan.PrivateRoot, directory: true, inherited: false).Security =
                WindowsOwnerTransferAccessFixture.Parse("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
            Files = new(this);
            Step = new(Machine.Release, Machine.Backend, Native, Files);
        }

        internal Task ApplyAsync(CancellationToken cancellationToken = default) => Step.ApplyAndVerifyAsync(Journal, cancellationToken);
        public void Dispose() => Machine.Dispose();
    }

    private sealed class FakeFiles(Fixture fixture) : IWindowsOwnerTransferCertificateFileNative
    {
        internal InstallerOwnerTransferCertificateState State { get; set; } =
            new(fixture.Journal.PreviousCertificateLedger, null, fixture.Journal.NextCertificateLedger);
        internal List<InstallerOwnerTransferCertificateAction> Actions { get; } = [];
        internal int Reads { get; private set; }
        internal string? Failure { get; set; }
        internal int FailAt { get; set; }
        internal Func<int, CancellationToken, Task>? BeforeRead { get; set; }
        internal Func<CancellationToken, Task>? BeforeApply { get; set; }

        public async Task<InstallerOwnerTransferCertificateState> ReadAsync(
            WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(fixture.Plan, plan);
            Assert.True(fixture.Native.LiveLeases > 0);
            Assert.Empty(fixture.Native.WritableOpened);
            Reads++;
            if (BeforeRead is not null)
            {
                await BeforeRead(Reads, cancellationToken);
            }
            if (Failure == "read-after" && Actions.Count == FailAt)
            {
                throw new IOException("Synthetic private read failure.");
            }
            return State;
        }

        public async Task ApplyAsync(WindowsOwnerTransferCertificatePlan plan,
            InstallerOwnerTransferCertificateState expected, InstallerOwnerTransferCertificateStep step,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(fixture.Plan, plan);
            Assert.Equal(State, expected);
            Assert.Equal(expected.GetNextStep(plan.Journal), step);
            Assert.True(fixture.Native.LiveLeases > 0);
            Actions.Add(step.Action);
            if (BeforeApply is not null)
            {
                await BeforeApply(cancellationToken);
            }
            string? failure = Actions.Count == FailAt ? Failure : null;
            if (failure == "io-before")
            {
                throw new IOException("Synthetic uncommitted failure.");
            }
            if (failure == "no-change")
            {
                return;
            }
            State = failure == "different-state" ? step.After with { PreviousArchive = null } : step.After;
            switch (failure)
            {
                case "io-after": throw new IOException("Synthetic acknowledgement loss.");
                case "win32-after": throw new Win32Exception(5, "Synthetic acknowledgement loss.");
                case "cancel-after": throw new OperationCanceledException("Synthetic acknowledgement loss.");
            }
        }
    }
}
