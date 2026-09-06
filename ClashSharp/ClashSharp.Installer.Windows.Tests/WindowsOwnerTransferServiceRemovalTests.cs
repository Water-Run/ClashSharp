using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsOwnerTransferServiceRemovalTests
{
    private const string PreviousSid = "S-1-5-21-100-200-300-1001";
    private const string NextSid = "S-1-5-21-100-200-300-1002";
    private const string PreviousProfile = @"C:\Users\previous";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovesOnlyThePreviousServiceAndPreservesAssociationForReplay(bool servicePresent)
    {
        using var fixture = new Fixture();
        fixture.Backend.ServicePresent = servicePresent;
        Assert.Empty(fixture.Events);

        await fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None);

        Assert.False(fixture.Backend.ServicePresent);
        Assert.Equal(servicePresent ? 1 : 0, fixture.Backend.RemovalCount);
        Assert.Equal(fixture.Journal.PreviousOwner.Association, fixture.Backend.Association);
        Assert.Equal(InstallerOperation.Uninstall, fixture.Backend.Plan!.Request.Operation);
        Assert.Equal(PreviousSid, fixture.Backend.Plan.Request.TargetSid);
        Assert.Equal(PreviousProfile, fixture.Backend.Plan.TargetProfileRoot);
        Assert.Equal(fixture.Journal.PreviousOwner.Association, fixture.Backend.Plan.Association);
        Assert.Equal(
            ["release", "barrier", "profile", "plan", "roots-open", "roots-verify", "association-open",
                "association-verify", "service-remove", "service-verify", "association-verify",
                "association-dispose", "roots-dispose"], fixture.Events);
        Assert.Equal(0, fixture.Backend.LiveLeases);
        Assert.False(fixture.Release.Disposed);
    }

    [Theory]
    [InlineData(InstallerOwnerTransferPhase.Prepared)]
    [InlineData(InstallerOwnerTransferPhase.PreviousServiceRemoved)]
    [InlineData(InstallerOwnerTransferPhase.MachineAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.AssociationTransferred)]
    [InlineData(InstallerOwnerTransferPhase.CertificateStateTransferred)]
    [InlineData(InstallerOwnerTransferPhase.InstallerAccessTransferred)]
    [InlineData(InstallerOwnerTransferPhase.Verified)]
    public async Task OtherDurablePhasesCannotEnterTheServiceStep(InstallerOwnerTransferPhase phase)
    {
        using var fixture = new Fixture();
        InstallerOwnerTransferJournal other = fixture.Journal with { Phase = phase, Generation = (int)phase + 1 };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            fixture.Step.ApplyAndVerifyAsync(other, CancellationToken.None));

        Assert.Equal("installer.owner_transfer.service_phase_invalid", failure.DiagnosticCode);
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("barrier-missing")]
    [InlineData("barrier-changed")]
    [InlineData("profile-missing")]
    [InlineData("profile-changed")]
    [InlineData("roots")]
    [InlineData("association")]
    [InlineData("foreign-service")]
    public async Task FailedPrerequisitesNeverRemoveServiceOrRewriteAssociation(string condition)
    {
        using var fixture = new Fixture();
        fixture.Failure = condition;
        InstallerMachineAssociation original = fixture.Backend.Association;

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.True(fixture.Backend.ServicePresent);
        Assert.Equal(0, fixture.Backend.RemovalCount);
        Assert.Equal(original, fixture.Backend.Association);
        Assert.Equal(0, fixture.Backend.LiveLeases);
        Assert.False(fixture.Release.Disposed);
        if (condition != "foreign-service")
        {
            Assert.DoesNotContain("service-remove", fixture.Events);
        }
    }

    [Theory]
    [InlineData("service-postcondition")]
    [InlineData("association-postcondition")]
    public async Task FailedPostconditionsDoNotReportTheStepComplete(string condition)
    {
        using var fixture = new Fixture();
        fixture.Failure = condition;

        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Step.ApplyAndVerifyAsync(fixture.Journal, CancellationToken.None));

        Assert.Equal(1, fixture.Backend.RemovalCount);
        Assert.Equal(fixture.Journal.PreviousOwner.Association, fixture.Backend.Association);
        Assert.Equal(0, fixture.Backend.LiveLeases);
        Assert.Equal(["association-dispose", "roots-dispose"], fixture.Events.TakeLast(2));
    }

    [Fact]
    public async Task CancellationKeepsAllStepLeasesUntilTheOwnedServiceOperationDrains()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        fixture.Backend.BeforeServiceRemoval = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        Task pending = fixture.Step.ApplyAndVerifyAsync(fixture.Journal, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Equal(2, fixture.Backend.LiveLeases);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        Assert.False(fixture.Backend.ServicePresent);
        Assert.Equal(fixture.Journal.PreviousOwner.Association, fixture.Backend.Association);
        Assert.Equal(0, fixture.Backend.LiveLeases);
        Assert.Equal(["association-dispose", "roots-dispose"], fixture.Events.TakeLast(2));
    }

    [Fact]
    public async Task PreCancellationPerformsNoBoundaryReadOrMutation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Step.ApplyAndVerifyAsync(fixture.Journal, cancellation.Token));

        Assert.Empty(fixture.Events);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WindowsPayloadFixture _payload = new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        internal List<string> Events { get; } = [];
        internal string? Failure { get; set; }
        internal InstallerOwnerTransferJournal Journal { get; }
        internal FakeRelease Release { get; }
        internal FakeBackend Backend { get; }
        internal WindowsOwnerTransferServiceRemoval Step { get; }

        internal Fixture()
        {
            Journal = InstallerOwnerTransferJournal.Create(
                _payload.Request(targetSid: NextSid),
                new(InstallerMachineAssociation.Create(PreviousSid, new string('b', 64)), PreviousProfile),
                new(InstallerMachineAssociation.Create(NextSid, new string('c', 64)), @"C:\Users\next"))
                .TransitionTo(InstallerOwnerTransferPhase.StartupBlocked);
            Release = new FakeRelease(this, _payload.Manifest);
            Backend = new FakeBackend(this);
            Step = new WindowsOwnerTransferServiceRemoval(Release, new BarrierReader(this), Backend);
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

    private sealed class FakeRelease(Fixture fixture, InstallerReleaseManifest manifest) : IInstallerReleaseLease
    {
        public VerifiedInstallerRelease Release => new(manifest.ExpectedPackageVersion, manifest.InstallerPayloadSha256,
            true, manifest.PackageCertificateThumbprint, manifest.CertificateSha256, true);
        public InstallerReleaseManifest Manifest => manifest;
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        internal bool Disposed { get; private set; }

        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Events.Add("release");
            Assert.True(fixture.Journal.Continuation.Matches(request));
            fixture.FailIf("release");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BarrierReader(Fixture fixture) : IInstallerTransactionReader
    {
        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Events.Add("barrier");
            return Task.FromResult(fixture.Failure == "barrier-missing" ? null : InstallerTransactionSnapshot.Create(
                fixture.Failure == "barrier-changed"
                    ? fixture.Journal.Continuation.TransitionTo(InstallerTransactionPhase.MachineReserved)
                    : fixture.Journal.Continuation));
        }
    }

    private sealed class FakeBackend(Fixture fixture) : IWindowsOwnerTransferServiceBackend
    {
        internal bool ServicePresent { get; set; } = true;
        internal int RemovalCount { get; private set; }
        internal int LiveLeases { get; private set; }
        internal InstallerMachineAssociation Association { get; } = fixture.Journal.PreviousOwner.Association;
        internal WindowsMachineDeploymentPlan? Plan { get; private set; }
        internal Func<CancellationToken, Task>? BeforeServiceRemoval { get; set; }

        public string ResolveTargetProfile(string targetSid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PreviousSid, targetSid);
            fixture.Events.Add("profile");
            fixture.FailIf("profile-missing");
            return fixture.Failure == "profile-changed" ? @"C:\Users\different" : PreviousProfile;
        }

        public WindowsMachineDeploymentPlan CreatePlan(InstallerRequest request, InstallerReleaseManifest manifest,
            InstallerMachineAssociation association, string targetProfileRoot, bool removalPlan)
        {
            fixture.Events.Add("plan");
            Assert.True(removalPlan);
            Plan = WindowsMachineDeploymentPlan.CreateForRemoval(request, manifest, association, @"C:\Program Files", @"C:\ProgramData", targetProfileRoot);
            return Plan;
        }

        public IWindowsMachineRootGuard CreateRootGuard(WindowsMachineDeploymentPlan plan, bool createMissing)
        {
            Assert.Same(Plan, plan);
            Assert.False(createMissing);
            fixture.Events.Add("roots-open");
            LiveLeases++;
            return new RootLease(this, fixture);
        }

        public IWindowsMachineAssociationStore CreateAssociationStore(WindowsMachineDeploymentPlan plan, IWindowsMachineRootGuard rootGuard)
        {
            Assert.Same(Plan, plan);
            Assert.Equal(1, LiveLeases);
            fixture.Events.Add("association-open");
            LiveLeases++;
            return new AssociationLease(this, fixture);
        }

        public async Task StopDeleteServiceAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(2, LiveLeases);
            Assert.Same(Plan, plan);
            fixture.Events.Add("service-remove");
            fixture.FailIf("foreign-service");
            if (BeforeServiceRemoval is not null)
            {
                await BeforeServiceRemoval(cancellationToken);
            }
            if (ServicePresent)
            {
                ServicePresent = false;
                RemovalCount++;
            }
        }

        public void VerifyServiceAbsent(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Events.Add("service-verify");
            fixture.FailIf("service-postcondition");
            Assert.False(ServicePresent);
        }

        private sealed class RootLease(FakeBackend owner, Fixture fixture) : IWindowsMachineRootGuard
        {
            public Task EnsureProtectedAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fixture.Events.Add("roots-verify");
                fixture.FailIf("roots");
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                fixture.Events.Add("roots-dispose");
                owner.LiveLeases--;
            }
        }

        private sealed class AssociationLease(FakeBackend owner, Fixture fixture) : IWindowsMachineAssociationStore
        {
            public Task<InstallerMachineAssociationObservation> InspectAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task WriteAndVerifyAsync(InstallerMachineAssociation association, CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task DeleteAndVerifyAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
            public Task VerifyAbsentAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

            public Task VerifyExactAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fixture.Events.Add("association-verify");
                fixture.FailIf("association");
                if (owner.RemovalCount > 0)
                {
                    fixture.FailIf("association-postcondition");
                }
                Assert.Equal(fixture.Journal.PreviousOwner.Association, owner.Association);
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                fixture.Events.Add("association-dispose");
                owner.LiveLeases--;
            }
        }
    }
}
