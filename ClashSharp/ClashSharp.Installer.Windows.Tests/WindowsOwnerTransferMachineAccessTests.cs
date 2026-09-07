using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferMachineAccessTests
{
    [Fact]
    public async Task TransfersOnlyMachineAccessAndRetainsTheOrdinaryBarrierAndRelease()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, fixture.Release.Calls);
        Assert.Empty(fixture.Native.Opened);
        byte[] association = fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes.ToArray();

        await fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None);

        Assert.Equal(8, fixture.Native.Writes.Count);
        Assert.Equal(2, fixture.Release.Calls);
        Assert.Equal(3, fixture.Backend.ServiceChecks);
        Assert.Equal(association, fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes);
        Assert.Equal(InstallerOwnerTransferPhase.PreviousServiceRemoved, fixture.Journal.Phase);
        Assert.Equal(InstallerTransactionPhase.Prepared, fixture.Journal.Continuation.Phase);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Release.Disposed);
        await fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None);
        Assert.Equal(8, fixture.Native.Writes.Count);
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.StartupBlocked)]
    [InlineData(InstallerOwnerTransferPhase.MachineAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.AssociationTransferred)]
    [InlineData(InstallerOwnerTransferPhase.CertificateStateTransferred)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.Verified)]
    public async Task WrongDurablePhaseCannotReadOrMutateMachineState(InstallerOwnerTransferPhase phase)
    {
        using var fixture = new Fixture();
        InstallerOwnerTransferJournal other = fixture.Journal with { Phase = phase, Generation = (int)phase + 1 };

        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Step.ApplyAndVerifyAsync(other, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.access_phase_invalid", error.DiagnosticCode);
        Assert.Equal(0, fixture.Release.Calls);
        Assert.Empty(fixture.Native.Opened);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("barrier-missing")]
    [InlineData("barrier-changed")]
    [InlineData("previous-profile-missing")]
    [InlineData("previous-profile-changed")]
    [InlineData("next-profile-changed")]
    [InlineData("different-roots")]
    [InlineData("service-present")]
    [InlineData("service-reappeared-before-write")]
    [InlineData("association-changed")]
    public async Task FailedPrerequisitesPreserveAllAccess(string condition)
    {
        using var fixture = new Fixture { Failure = condition };
        if (condition == "association-changed")
        {
            fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
                InstallerMachineAssociationCodec.Serialize(fixture.Journal.NextOwner.Association);
        }
        if (condition == "barrier-missing")
        {
            fixture.Native.Entries.Remove(WindowsOwnerTransferAccessFixture.ContinuationPath);
        }
        if (condition == "barrier-changed")
        {
            fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                InstallerTransactionCodec.Serialize(fixture.Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved));
        }

        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Release.Disposed);
    }

    [Theory]
    [InlineData("release-postcondition")]
    [InlineData("barrier-postcondition")]
    [InlineData("service-postcondition")]
    [InlineData("association-postcondition")]
    [InlineData("access-postcondition")]
    public async Task FailedPostconditionsPreserveTheDurablePhaseForRecovery(string condition)
    {
        using var fixture = new Fixture { Failure = condition };
        if (condition == "association-postcondition")
        {
            fixture.Native.BeforeWrite = _ =>
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.AssociationPath].Bytes =
                    InstallerMachineAssociationCodec.Serialize(fixture.Journal.NextOwner.Association);
        }
        if (condition == "barrier-postcondition")
        {
            fixture.Native.BeforeWrite = _ =>
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                    InstallerTransactionCodec.Serialize(fixture.Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved));
        }

        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.Equal(8, fixture.Native.Writes.Count);
        Assert.Equal(InstallerOwnerTransferPhase.PreviousServiceRemoved, fixture.Journal.Phase);
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Release.Disposed);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("win32")]
    public async Task NativeFailuresDoNotLeakPathsOrIdentities(string kind)
    {
        using var fixture = new Fixture();
        fixture.Native.BeforeObserve = _ => throw kind switch
        {
            "io" => new IOException(@"C:\private\synthetic"),
            "access" => new UnauthorizedAccessException(@"C:\private\synthetic"),
            _ => new Win32Exception(5, WindowsOwnerTransferAccessFixture.PreviousSid),
        };

        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.access_failed", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(WindowsOwnerTransferAccessFixture.PreviousSid, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Native.Writes);
        Assert.Equal(0, fixture.Native.LiveLeases);
    }

    [Fact]
    public async Task CancellationRetainsAllHandlesUntilOwnedPostVerificationDrains()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Release.PostVerification = _ =>
        {
            entered.TrySetResult();
            return drained.Task;
        };
        Task pending = fixture.Step.ApplyAndVerifyAsync(fixture.Journal, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Equal(fixture.Native.Opened.Count, fixture.Native.LiveLeases);
            Assert.Equal(8, fixture.Native.Writes.Count);
        }
        finally
        {
            cancellation.Cancel();
            drained.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal(0, fixture.Native.LiveLeases);
        Assert.False(fixture.Release.Disposed);
    }

    [Fact]
    public async Task PreCancellationDoesNotOpenAnyBoundary()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Step.ApplyAndVerifyAsync(fixture.Journal, cancellation.Token));

        Assert.Equal(0, fixture.Release.Calls);
        Assert.Empty(fixture.Native.Opened);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly WindowsPayloadFixture _payload = new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        internal WindowsOwnerTransferAccessFixture Native { get; } = new();
        internal InstallerOwnerTransferJournal Journal { get; }
        internal ReleaseLease Release { get; }
        internal Backend Backend { get; }
        internal WindowsOwnerTransferMachineAccess Step { get; }
        internal string? Failure { get; set; }

        internal Fixture()
        {
            Journal = InstallerOwnerTransferJournal.Create(_payload.Request(targetSid: WindowsOwnerTransferAccessFixture.NextSid),
                new(WindowsOwnerTransferAccessFixture.Association, @"C:\Users\previous"),
                new(InstallerMachineAssociation.Create(WindowsOwnerTransferAccessFixture.NextSid, new string('c', 64)), @"C:\Users\next"))
                .TransitionTo(InstallerOwnerTransferPhase.StartupBlocked)
                .TransitionTo(InstallerOwnerTransferPhase.PreviousServiceRemoved);
            Release = new(this, _payload.Manifest);
            Backend = new(this);
            Native.Entries[WindowsOwnerTransferAccessFixture.ContinuationPath].Bytes =
                InstallerTransactionCodec.Serialize(Journal.Continuation);
            Step = new(Release, Backend, Native);
        }

        internal void FailIf(string condition)
        {
            if (Failure == condition)
            {
                throw new InstallerProtocolException("installer.owner_transfer.injected_refusal");
            }
        }

        public void Dispose() => _payload.Dispose();
    }

    internal sealed class ReleaseLease(Fixture fixture, InstallerReleaseManifest manifest) : IInstallerReleaseLease
    {
        public VerifiedInstallerRelease Release => new(manifest.ExpectedPackageVersion, manifest.InstallerPayloadSha256,
            true, manifest.PackageCertificateThumbprint, manifest.CertificateSha256, true);
        public InstallerReleaseManifest Manifest => manifest;
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        internal Func<CancellationToken, Task>? PostVerification { get; set; }

        public async Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(fixture.Journal.Continuation.Matches(request));
            Assert.False(request.AllowReassociation);
            Calls++;
            fixture.FailIf(Calls == 1 ? "release" : "release-postcondition");
            if (Calls == 2 && PostVerification is not null)
            {
                await PostVerification(cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Backend(Fixture fixture) : IWindowsOwnerTransferStateBackend
    {
        internal int ServiceChecks { get; private set; }

        public string ResolveTargetProfile(string targetSid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool previous = targetSid == WindowsOwnerTransferAccessFixture.PreviousSid;
            fixture.FailIf(previous ? "previous-profile-missing" : "next-profile-missing");
            return fixture.Failure == (previous ? "previous-profile-changed" : "next-profile-changed")
                ? @"C:\Users\unexpected" : previous ? fixture.Journal.PreviousOwner.ProfileRoot : fixture.Journal.NextOwner.ProfileRoot;
        }

        public WindowsMachineDeploymentPlan CreatePlan(InstallerRequest request, InstallerReleaseManifest manifest,
            InstallerMachineAssociation association, string targetProfileRoot, bool removalPlan)
        {
            Assert.False(request.AllowReassociation);
            if (removalPlan)
            {
                Assert.Equal(InstallerOperation.Uninstall, request.Operation);
                Assert.Equal(fixture.Journal.PreviousOwner.Association, association);
                return WindowsMachineDeploymentPlan.CreateForRemoval(request, manifest, association,
                    @"C:\Program Files", @"C:\ProgramData", targetProfileRoot);
            }
            Assert.Equal(fixture.Journal.NextOwner.Association, association);
            return WindowsMachineDeploymentPlan.Create(request, manifest, association,
                fixture.Failure == "different-roots" ? @"D:\Program Files" : @"C:\Program Files", @"C:\ProgramData", targetProfileRoot);
        }

        public void VerifyServiceAbsent(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceChecks++;
            fixture.FailIf(ServiceChecks switch
            {
                1 => "service-present",
                2 => "service-reappeared-before-write",
                _ => "service-postcondition",
            });
            if (ServiceChecks == 3 && fixture.Failure == "access-postcondition")
            {
                fixture.Native.Entries[WindowsOwnerTransferAccessFixture.Payload].Links = 2;
            }
        }
    }
}
