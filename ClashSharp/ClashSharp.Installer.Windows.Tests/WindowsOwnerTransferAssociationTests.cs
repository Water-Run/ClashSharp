using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferAssociationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TransfersOrReplaysOnlyTheRecordedIdentityAndDrainsItsTemporary(bool alreadyTransferred, bool temporary)
    {
        using var fixture = new Fixture();
        fixture.Files.Association = alreadyTransferred ? fixture.Journal.NextOwner.Association : fixture.Journal.PreviousOwner.Association;
        fixture.Files.TemporaryPresent = temporary;
        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Files.Inspections);

        await fixture.ApplyAsync();

        Assert.Equal(alreadyTransferred && !temporary ? 0 : 1, fixture.Files.Replacements);
        Assert.Equal(fixture.Journal.NextOwner.Association, fixture.Files.Association);
        Assert.False(fixture.Files.TemporaryPresent);
        Assert.Empty(fixture.Native.Writes);
        Assert.Empty(fixture.Native.WritableOpened);
        Assert.Equal(InstallerOwnerTransferPhase.MachineAccessTransferred, fixture.Journal.Phase);
        Assert.Equal(InstallerTransactionPhase.Prepared, fixture.Journal.Continuation.Phase);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Machine.Release.Disposed);
        int writes = fixture.Files.Replacements;
        await fixture.ApplyAsync();
        Assert.Equal(writes, fixture.Files.Replacements);
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.StartupBlocked)]
    [InlineData(InstallerOwnerTransferPhase.PreviousServiceRemoved)]
    [InlineData(InstallerOwnerTransferPhase.AssociationTransferred)]
    [InlineData(InstallerOwnerTransferPhase.CertificateStateTransferred)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.Verified)]
    public async Task OtherPhasesCannotEnterAssociationReplacement(InstallerOwnerTransferPhase phase)
    {
        using var fixture = new Fixture();
        var journal = fixture.Journal with { Phase = phase, Generation = (int)phase + 1 };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Step.ApplyAndVerifyAsync(journal, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.association_phase_invalid", failure.DiagnosticCode);
        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Files.Inspections);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    [InlineData("service-present")]
    [InlineData("service-reappeared-before-write")]
    [InlineData("old-shared-acl")]
    [InlineData("changed-barrier")]
    [InlineData("foreign-association")]
    [InlineData("other-temporary")]
    [InlineData("noncanonical-temporary")]
    public async Task FailedPrerequisitesLeaveTheAssociationAndAllAccessUntouched(string condition)
    {
        using var fixture = new Fixture();
        fixture.Machine.Failure = condition;
        switch (condition)
        {
            case "old-shared-acl":
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.Payload].Security =
                    WindowsOwnerTransferAccessFixture.OwnerSecurity(WindowsOwnerTransferAccessFixture.PreviousSid, false, true);
                break;
            case "changed-barrier":
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                    InstallerTransactionCodec.Serialize(fixture.Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved));
                break;
            case "foreign-association":
                fixture.Files.Association = InstallerMachineAssociation.Create(WindowsOwnerTransferAccessFixture.NextSid, new string('f', 64));
                break;
            case "other-temporary":
                fixture.Native.Add(Path.Combine(WindowsOwnerTransferAccessFixture.ServiceData,
                    $".association-transfer-{new string('f', 64)}.tmp"), false, true);
                break;
            case "noncanonical-temporary":
                fixture.Native.Add(Path.Combine(WindowsOwnerTransferAccessFixture.ServiceData,
                    Path.GetFileName(fixture.Plan.TemporaryPath).ToUpperInvariant()), false, true);
                break;
        }
        InstallerMachineAssociation original = fixture.Files.Association;

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Equal(original, fixture.Files.Association);
        Assert.Equal(0, fixture.Files.Replacements);
        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("io-after")]
    [InlineData("win32-after")]
    [InlineData("cancel-after")]
    public async Task LostReplacementAcknowledgementIsReconciledFromTheActualNewState(string failure)
    {
        using var fixture = new Fixture();
        fixture.Files.Failure = failure;

        await fixture.ApplyAsync();

        Assert.Equal(1, fixture.Files.Replacements);
        Assert.Equal(fixture.Journal.NextOwner.Association, fixture.Files.Association);
        Assert.Equal(3, fixture.Files.Inspections);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("io-before")]
    [InlineData("no-change")]
    [InlineData("foreign-after")]
    [InlineData("temporary-remains")]
    [InlineData("read-after")]
    public async Task UnprovedReplacementNeverCompletesTheDurablePhase(string failure)
    {
        using var fixture = new Fixture();
        fixture.Files.Failure = failure;

        InstallerStateUncertainException error = await Assert.ThrowsAsync<InstallerStateUncertainException>(() => fixture.ApplyAsync());

        Assert.Equal("installer.owner_transfer.association_state_uncertain", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.Equal(InstallerOwnerTransferPhase.MachineAccessTransferred, fixture.Journal.Phase);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData("release-postcondition")]
    [InlineData("service-postcondition")]
    [InlineData("access-postcondition")]
    [InlineData("association-postcondition")]
    [InlineData("barrier-postcondition")]
    public async Task LateBoundaryChangesAreNotReportedAsCompleted(string condition)
    {
        using var fixture = new Fixture();
        fixture.Machine.Failure = condition;
        fixture.Files.BeforeInspect = (count, _) =>
        {
            if (count == 3 && condition == "association-postcondition")
            {
                fixture.Files.Association = fixture.Journal.PreviousOwner.Association;
            }
            if (count == 3 && condition == "barrier-postcondition")
            {
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                    InstallerTransactionCodec.Serialize(fixture.Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved));
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Equal(1, fixture.Files.Replacements);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsEveryBoundaryUntilReplacementAndUncancelledObservationDrain(bool waitDuringObservation)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Hold()
        {
            entered.TrySetResult();
            return release.Task;
        }
        if (waitDuringObservation)
        {
            fixture.Files.BeforeInspect = (count, token) =>
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
            fixture.Files.BeforeReplace = _ => Hold();
        }
        Task pending = fixture.ApplyAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Equal(fixture.Native.Opened.Count, fixture.Native.LiveLeases);
            Assert.DoesNotContain(WindowsOwnerTransferAccessFixture.AssociationPath, fixture.Native.Opened);
            Assert.Contains(WindowsOwnerTransferAccessFixture.ContinuationPath, fixture.Native.Opened);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(fixture.Journal.NextOwner.Association, fixture.Files.Association);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Machine.Release.Disposed);
    }

    [Fact]
    public async Task MatchingTemporaryDoesNotRelaxUnrelatedDirectoryMembership()
    {
        using var fixture = new Fixture();
        fixture.Native.Add(fixture.Plan.TemporaryPath, false, true);
        fixture.Files.TemporaryPresent = true;

        await fixture.ApplyAsync();

        Assert.Equal(1, fixture.Files.Replacements);
        Assert.DoesNotContain(fixture.Plan.TemporaryPath, fixture.Native.Opened);
        Assert.Empty(fixture.Native.WritableOpened);
    }

    [Fact]
    public async Task InitialNativeFailureHasNoPrivateExceptionDetails()
    {
        using var fixture = new Fixture();
        fixture.Files.BeforeInspect = (_, _) => throw new IOException(@"C:\private\synthetic-credential");

        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.ApplyAsync());

        Assert.Equal("installer.owner_transfer.association_failed", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Files.Replacements);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Fact]
    public async Task PreCancellationPerformsNoReadOrMutation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ApplyAsync(cancellation.Token));

        Assert.Empty(fixture.Native.Opened);
        Assert.Equal(0, fixture.Files.Inspections);
        Assert.Equal(0, fixture.Files.Replacements);
    }

    [Fact]
    public async Task CancelledUncommittedReplacementIsObservedBeforeReturningCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Files.BeforeReplace = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ApplyAsync(cancellation.Token));

        Assert.Equal(2, fixture.Files.Inspections);
        Assert.Equal(fixture.Journal.PreviousOwner.Association, fixture.Files.Association);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostBarrierOrAccessDuringReplacementMakesTheResultUncertain(bool access)
    {
        using var fixture = new Fixture();
        fixture.Files.BeforeReplace = _ =>
        {
            if (access)
            {
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.Payload].Links = 2;
            }
            else
            {
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes = [1, 2, 3];
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => fixture.ApplyAsync());

        Assert.Equal(fixture.Journal.NextOwner.Association, fixture.Files.Association);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    private sealed class Fixture : IDisposable
    {
        internal WindowsOwnerTransferMachineAccessTests.Fixture Machine { get; } = new();
        internal WindowsOwnerTransferAccessFixture Native => Machine.Native;
        internal InstallerOwnerTransferJournal Journal { get; }
        internal WindowsOwnerTransferAssociationPlan Plan { get; }
        internal FakeFiles Files { get; }
        internal WindowsOwnerTransferAssociation Step { get; }

        internal Fixture()
        {
            Journal = Machine.Journal.TransitionTo(InstallerOwnerTransferPhase.MachineAccessTransferred);
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
            Plan = new(Native.Roots, Journal.Continuation.TransactionId, Journal.PreviousOwner.Association, Journal.NextOwner.Association);
            Files = new(this);
            Step = new(Machine.Release, Machine.Backend, Native, Files);
        }

        internal Task ApplyAsync(CancellationToken cancellationToken = default) => Step.ApplyAndVerifyAsync(Journal, cancellationToken);
        public void Dispose() => Machine.Dispose();
    }

    private sealed class FakeFiles(Fixture fixture) : IWindowsOwnerTransferAssociationFileNative
    {
        internal InstallerMachineAssociation Association { get; set; } = fixture.Journal.PreviousOwner.Association;
        internal bool TemporaryPresent { get; set; }
        internal int Inspections { get; private set; }
        internal int Replacements { get; private set; }
        internal string? Failure { get; set; }
        internal Func<int, CancellationToken, Task>? BeforeInspect { get; set; }
        internal Func<CancellationToken, Task>? BeforeReplace { get; set; }

        public async Task<WindowsOwnerTransferAssociationObservation> InspectAsync(
            WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(fixture.Plan, plan);
            Assert.True(fixture.Native.LiveLeases > 0);
            Assert.Empty(fixture.Native.WritableOpened);
            Inspections++;
            if (BeforeInspect is not null)
            {
                await BeforeInspect(Inspections, cancellationToken);
            }
            if (Failure == "read-after" && Inspections == 2)
            {
                throw new IOException("Synthetic unobservable postcondition.");
            }
            return new(Association, TemporaryPresent);
        }

        public async Task ReplaceExactAsync(WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(fixture.Plan, plan);
            Assert.True(fixture.Native.LiveLeases > 0);
            Assert.DoesNotContain(plan.AssociationPath, fixture.Native.Opened);
            Replacements++;
            if (BeforeReplace is not null)
            {
                await BeforeReplace(cancellationToken);
            }
            if (Failure == "io-before")
            {
                throw new IOException("Synthetic uncommitted failure.");
            }
            if (Failure == "no-change")
            {
                return;
            }
            Association = Failure == "foreign-after"
                ? InstallerMachineAssociation.Create(plan.Next.OwnerSid, new string('f', 64)) : plan.Next;
            TemporaryPresent = Failure == "temporary-remains";
            switch (Failure)
            {
                case "io-after": throw new IOException("Synthetic acknowledgement loss.");
                case "win32-after": throw new Win32Exception(5, "Synthetic acknowledgement loss.");
                case "cancel-after": throw new OperationCanceledException("Synthetic late cancellation.");
            }
        }
    }
}
