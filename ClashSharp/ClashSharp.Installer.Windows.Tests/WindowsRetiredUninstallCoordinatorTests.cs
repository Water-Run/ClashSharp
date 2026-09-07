using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Retirement;

namespace ClashSharp.Installer.Windows.Tests;

public sealed partial class WindowsRetiredUninstallAuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCoordinatorUsesAuthenticatedReceiptsAndResumesCancellationAfterPackageRemoval(bool cancelAfterPackage)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        async Task<InstallerExecutionResult> Run(bool cancel)
        {
            await using var pair = new SessionPair(fixture);
            WindowsRetiredUninstallParentHandoff handoff = await pair.StartAsync();
            await using WindowsMachineHelperBroker broker = handoff.Broker;
            var receipts = new WindowsRetiredUninstallReceipts(handoff.Ready, broker);
            var machine = new CoordinatorMachine(receipts);
            var coordinator = new InstallerCoordinator(new ParentEnvironment(fixture), new ParentVerifier(fixture), receipts,
                new ParentPackage(fixture, cancel ? cancellation : null), machine, machine, receipts);
            return await coordinator.ExecuteAsync(fixture.Request, null, cancel ? cancellation.Token : default);
        }
        InstallerExecutionResult result = await Run(cancelAfterPackage);
        if (cancelAfterPackage)
        {
            Assert.Equal(InstallerExecutionOutcome.Cancelled, result.Outcome);
            Assert.True(result.RecoveryPending);
            Assert.Equal(InstallerTransactionPhase.MachineCommitted, result.LastDurablePhase);
            Assert.False(fixture.PackagePresent);
            Assert.True(fixture.CertificatePresent);
            Assert.NotNull(fixture.Journal.Bytes);
            Assert.Empty(fixture.Active);
            result = await Run(false);
        }
        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.False(result.RecoveryPending);
        Assert.Null(fixture.Journal.Bytes);
        Assert.Null(fixture.Archive.Bytes);
        Assert.False(fixture.PackagePresent);
        Assert.False(fixture.CertificatePresent);
        Assert.Equal(1, fixture.CertificateRemovals);
        Assert.Empty(fixture.Active);
    }

    private sealed class ParentEnvironment(Fixture fixture) : IInstallerEnvironment
    {
        public Task<InstallerEnvironmentSnapshot> InspectAsync(InstallerRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InstallerEnvironmentSnapshot(true, fixture.PackagePresent ? fixture.Request.ExpectedPackageVersion : null, false, null));
    }
    private sealed class ParentVerifier(Fixture fixture) : IInstallerReleaseVerifier
    {
        public Task<IInstallerReleaseLease> VerifyAsync(InstallerRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IInstallerReleaseLease>(new ParentLease(fixture));
    }
    private sealed class ParentLease(Fixture fixture) : IInstallerReleaseLease
    {
        private bool _disposed;
        public InstallerReleaseManifest Manifest => fixture.Payload.Manifest;
        public VerifiedInstallerRelease Release => new(Manifest.ExpectedPackageVersion, Manifest.InstallerPayloadSha256,
            false, Manifest.PackageCertificateThumbprint, Manifest.CertificateSha256, false);
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            Assert.False(_disposed);
            Assert.Equal(fixture.Request, request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class ParentPackage(Fixture fixture, CancellationTokenSource? cancelAfterPackage) : IInstallerPackageMutation
    {
        public Task ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            fixture.RequireHeld();
            Assert.Equal(InstallerOperation.Uninstall, request.Operation);
            Assert.Equal(fixture.Request, request);
            Assert.Equal(InstallerTransactionPhase.MachineCommitted, InstallerTransactionCodec.Parse(fixture.Journal.Bytes!).Phase);
            fixture.PackagePresent = false;
            cancelAfterPackage?.Cancel();
            return Task.CompletedTask;
        }
    }
    // Replaces only the platform-specific parent's signed-file lease check. Coordinator, actual
    // authenticated host/broker framing, private store and certificate operation executor are real.
    private sealed class CoordinatorMachine(WindowsRetiredUninstallReceipts receipts) : IInstallerMachineMutation, IInstallerFinalVerifier
    {
        private async Task<InstallerTransactionSnapshot> Send(InstallerMachineHelperVerb verb, InstallerTransactionSnapshot state, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            InstallerMachineHelperCommand command = Command(state, verb);
            InstallerMachineHelperResult result = await receipts.ExecuteAsync(command);
            if (result.Outcome != InstallerMachineHelperOutcome.Succeeded) { throw new InstallerProtocolException(result.DiagnosticCode); }
            return result.ValidateAgainst(command);
        }
        public Task<InstallerTransactionSnapshot> PrepareAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot state, CancellationToken token) =>
            Send(InstallerMachineHelperVerb.Prepare, state, token);
        public Task<InstallerTransactionSnapshot> ApplyAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot state, CancellationToken token) =>
            Send(InstallerMachineHelperVerb.Remove, state, token);
        public Task<InstallerTransactionSnapshot> CommitPackageAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot state, CancellationToken token) =>
            Send(InstallerMachineHelperVerb.CommitPackage, state, token);
        public Task<InstallerTransactionSnapshot> VerifyAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot state, CancellationToken token) =>
            Send(InstallerMachineHelperVerb.Verify, state, token);
        public Task<InstallerTransactionSnapshot> ClearVerifiedAsync(InstallerRequest request, IInstallerReleaseLease release, InstallerTransactionSnapshot state, CancellationToken token) =>
            Send(InstallerMachineHelperVerb.Clear, state, token);
    }
}
