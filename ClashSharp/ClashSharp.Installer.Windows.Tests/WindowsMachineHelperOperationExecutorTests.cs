using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineHelperOperationExecutorTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task PackageCommitVerifiesExactTargetPackageAndCertificate(
        InstallerOperation operation)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(operation, TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var certificate = new RecordingCertificateStore(
            InstallerCertificatePresence.ExactMatch);
        var package = new RecordingPackageInspector();
        var machine = new RecordingMachineOperations();
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            certificate,
            package,
            machine,
            out RecordingCertificateMutation certificateMutation);

        await executor.ExecuteAsync(
            Command(request, InstallerMachineHelperVerb.CommitPackage),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Equal(request, release.Request);
        Assert.Equal(request, package.Request);
        Assert.Same(fixture.Manifest, package.Manifest);
        Assert.Equal(request, certificate.Request);
        Assert.Equal(0, certificateMutation.ApplyCalls);
        Assert.Equal(1, certificateMutation.VerifyCalls);
        Assert.Empty(machine.Calls);
        Assert.Equal(1, release.Lease!.ReverifyCalls);
        Assert.Equal(1, release.Lease.DisposeCalls);
    }

    [Fact]
    public async Task UninstallPackageCommitDoesNotAssumePreExistingCertificateWasRemoved()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var certificate = new RecordingCertificateStore(
            InstallerCertificatePresence.ExactMatch);
        var package = new RecordingPackageInspector();
        var machine = new RecordingMachineOperations();
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            certificate,
            package,
            machine,
            out RecordingCertificateMutation certificateMutation);

        await executor.ExecuteAsync(
            Command(request, InstallerMachineHelperVerb.CommitPackage),
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            CancellationToken.None);

        Assert.Equal(1, package.VerifyCalls);
        Assert.Equal(1, certificate.InspectCalls);
        Assert.Equal(0, certificateMutation.ApplyCalls);
        Assert.Equal(1, certificateMutation.VerifyCalls);
        Assert.Empty(machine.Calls);
    }

    [Theory]
    [InlineData(InstallerMachineHelperVerb.Verify)]
    [InlineData(InstallerMachineHelperVerb.Clear)]
    public async Task InstalledFinalStateReverifiesMachinePackageAndExactCertificate(
        InstallerMachineHelperVerb verb)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var certificate = new RecordingCertificateStore(
            InstallerCertificatePresence.ExactMatch);
        var package = new RecordingPackageInspector();
        var machine = new RecordingMachineOperations();
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            certificate,
            package,
            machine,
            out _);

        await executor.ExecuteAsync(
            Command(request, verb),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Equal(["verify"], machine.Calls);
        Assert.Equal(1, package.VerifyCalls);
        Assert.Equal(1, certificate.InspectCalls);
    }

    [Theory]
    [InlineData(InstallerCertificatePresence.Missing)]
    [InlineData(InstallerCertificatePresence.ExactMatch)]
    public async Task UninstallFinalStateAllowsAbsentOrPreservedPreExistingCertificate(
        InstallerCertificatePresence presence)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var certificate = new RecordingCertificateStore(presence);
        var package = new RecordingPackageInspector();
        var machine = new RecordingMachineOperations();
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            certificate,
            package,
            machine,
            out _);

        await executor.ExecuteAsync(
            Command(request, InstallerMachineHelperVerb.Verify),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Equal(1, certificate.InspectCalls);
        Assert.Equal(1, package.VerifyCalls);
        Assert.Equal(["verify"], machine.Calls);
    }

    [Fact]
    public async Task CertificateConflictBlocksFinalJournalAdvance()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            new RecordingCertificateStore(
                InstallerCertificatePresence.IdentityConflict),
            new RecordingPackageInspector(),
            new RecordingMachineOperations(),
            out _);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => executor.ExecuteAsync(
                Command(request, InstallerMachineHelperVerb.Verify),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal(
            "installer.certificate.identity_conflict",
            exception.DiagnosticCode);
        Assert.Equal(1, release.Lease!.DisposeCalls);
    }

    [Theory]
    [InlineData(
        InstallerMachineHelperVerb.Prepare,
        InstallerOperation.Install,
        InstallerMachineHelperSessionDisposition.Execute,
        "prepare:Execute")]
    [InlineData(
        InstallerMachineHelperVerb.Prepare,
        InstallerOperation.Uninstall,
        InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
        "prepare:VerifyCommittedReplay")]
    [InlineData(
        InstallerMachineHelperVerb.Apply,
        InstallerOperation.Install,
        InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
        "apply:VerifyCommittedReplay")]
    [InlineData(
        InstallerMachineHelperVerb.Remove,
        InstallerOperation.Uninstall,
        InstallerMachineHelperSessionDisposition.Execute,
        "remove:Execute")]
    public async Task MachineVerbsPreserveAuthenticatedReplayDisposition(
        InstallerMachineHelperVerb verb,
        InstallerOperation operation,
        InstallerMachineHelperSessionDisposition disposition,
        string expectedCall)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(operation, TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var machine = new RecordingMachineOperations();
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            new RecordingCertificateStore(InstallerCertificatePresence.ExactMatch),
            new RecordingPackageInspector(),
            machine,
            out _);

        await executor.ExecuteAsync(
            Command(request, verb),
            disposition,
            CancellationToken.None);

        Assert.Equal([expectedCall], machine.Calls);
        Assert.Equal(1, release.Lease!.ReverifyCalls);
    }

    [Fact]
    public async Task MissingInstallCertificateBlocksPackageCommit()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            new RecordingCertificateStore(InstallerCertificatePresence.Missing),
            new RecordingPackageInspector(),
            new RecordingMachineOperations(),
            out _);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => executor.ExecuteAsync(
                Command(request, InstallerMachineHelperVerb.CommitPackage),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal(
            "installer.certificate.installation_verification_failed",
            exception.DiagnosticCode);
    }

    [Fact]
    public async Task InvalidDispositionFailsBeforeReleaseAcquisition()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            new RecordingCertificateStore(InstallerCertificatePresence.ExactMatch),
            new RecordingPackageInspector(),
            new RecordingMachineOperations(),
            out _);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => executor.ExecuteAsync(
                Command(request, InstallerMachineHelperVerb.Prepare),
                (InstallerMachineHelperSessionDisposition)99,
                CancellationToken.None));

        Assert.Equal(
            "installer.machine_helper.disposition_invalid",
            exception.DiagnosticCode);
        Assert.Null(release.Lease);
    }

    [Fact]
    public async Task PackageFailureStillReleasesIndependentLease()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var package = new RecordingPackageInspector
        {
            Failure = new InstallerProtocolException(
                "installer.package.deployment_verification_failed"),
        };
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            new RecordingCertificateStore(InstallerCertificatePresence.ExactMatch),
            package,
            new RecordingMachineOperations(),
            out _);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => executor.ExecuteAsync(
                Command(request, InstallerMachineHelperVerb.CommitPackage),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal(
            "installer.package.deployment_verification_failed",
            exception.DiagnosticCode);
        Assert.Equal(1, release.Lease!.DisposeCalls);
    }

    [Theory]
    [InlineData(InstallerMachineHelperSessionDisposition.Execute, 1)]
    [InlineData(InstallerMachineHelperSessionDisposition.VerifyCommittedReplay, 0)]
    public async Task InstallPrepareMutatesCertificateOnlyOnExecutableDisposition(
        InstallerMachineHelperSessionDisposition disposition,
        int expectedApplyCalls)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var machine = new RecordingMachineOperations();
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            new RecordingCertificateStore(InstallerCertificatePresence.ExactMatch),
            new RecordingPackageInspector(),
            machine,
            out RecordingCertificateMutation certificateMutation);
        certificateMutation.ApplyObserver = () =>
            Assert.Equal(["prepare:Execute"], machine.Calls);

        await executor.ExecuteAsync(
            Command(request, InstallerMachineHelperVerb.Prepare),
            disposition,
            CancellationToken.None);

        Assert.Equal(expectedApplyCalls, certificateMutation.ApplyCalls);
        Assert.Equal(1, certificateMutation.VerifyCalls);
    }

    [Fact]
    public async Task UninstallPackageCommitVerifiesPackageBeforeCertificateRemoval()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var release = new RecordingReleaseVerifier(fixture.Manifest);
        var package = new RecordingPackageInspector();
        WindowsMachineHelperOperationExecutor executor = Executor(
            release,
            new RecordingCertificateStore(InstallerCertificatePresence.Missing),
            package,
            new RecordingMachineOperations(),
            out RecordingCertificateMutation certificateMutation);
        certificateMutation.ApplyObserver = () => Assert.Equal(1, package.VerifyCalls);

        await executor.ExecuteAsync(
            Command(request, InstallerMachineHelperVerb.CommitPackage),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Equal(1, certificateMutation.ApplyCalls);
        Assert.Equal(1, certificateMutation.VerifyCalls);
    }

    private static WindowsMachineHelperOperationExecutor Executor(
        IInstallerReleaseVerifier releaseVerifier,
        RecordingCertificateStore certificateStore,
        IWindowsTargetUserPackageCommitInspector packageInspector,
        IWindowsMachineHelperMachineOperations machineOperations,
        out RecordingCertificateMutation certificateMutation)
    {
        certificateMutation = new RecordingCertificateMutation(certificateStore);
        return new WindowsMachineHelperOperationExecutor(
            releaseVerifier,
            certificateMutation,
            certificateMutation,
            packageInspector,
            machineOperations);
    }

    private static WindowsPayloadFixture Fixture() =>
        new(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);

    private static InstallerMachineHelperCommand Command(
        InstallerRequest request,
        InstallerMachineHelperVerb verb)
    {
        InstallerTransactionPhase phase = (verb, request.Operation) switch
        {
            (InstallerMachineHelperVerb.Prepare, _) =>
                InstallerTransactionPhase.Prepared,
            (InstallerMachineHelperVerb.CommitPackage,
                InstallerOperation.Install or InstallerOperation.Repair) =>
                InstallerTransactionPhase.MachineReserved,
            (InstallerMachineHelperVerb.CommitPackage, InstallerOperation.Uninstall) =>
                InstallerTransactionPhase.MachineCommitted,
            (InstallerMachineHelperVerb.Apply, _) =>
                InstallerTransactionPhase.PackageCommitted,
            (InstallerMachineHelperVerb.Remove, _) =>
                InstallerTransactionPhase.MachineRemovalAuthorized,
            (InstallerMachineHelperVerb.Verify,
                InstallerOperation.Install or InstallerOperation.Repair) =>
                InstallerTransactionPhase.MachineCommitted,
            (InstallerMachineHelperVerb.Verify, InstallerOperation.Uninstall) =>
                InstallerTransactionPhase.PackageCommitted,
            (InstallerMachineHelperVerb.Clear, _) =>
                InstallerTransactionPhase.Verified,
            _ => throw new InvalidOperationException("Unsupported command test case."),
        };
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        while (journal.Phase != phase)
        {
            journal = journal.TransitionTo(NextPhase(journal));
        }

        InstallerTransactionSnapshot state = InstallerTransactionSnapshot.Create(journal);
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(verb, state);
        return InstallerMachineHelperCommand.Create(invocation, state);
    }

    private static InstallerTransactionPhase NextPhase(
        InstallerTransactionJournal journal) =>
        (journal.Operation, journal.Phase) switch
        {
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.Prepared) =>
                InstallerTransactionPhase.MachineReserved,
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.MachineReserved) =>
                InstallerTransactionPhase.PackageCommitted,
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.PackageCommitted) =>
                InstallerTransactionPhase.MachineCommitted,
            (InstallerOperation.Install or InstallerOperation.Repair,
                InstallerTransactionPhase.MachineCommitted) =>
                InstallerTransactionPhase.Verified,
            (InstallerOperation.Uninstall, InstallerTransactionPhase.Prepared) =>
                InstallerTransactionPhase.MachineRemovalAuthorized,
            (InstallerOperation.Uninstall,
                InstallerTransactionPhase.MachineRemovalAuthorized) =>
                InstallerTransactionPhase.MachineCommitted,
            (InstallerOperation.Uninstall, InstallerTransactionPhase.MachineCommitted) =>
                InstallerTransactionPhase.PackageCommitted,
            (InstallerOperation.Uninstall, InstallerTransactionPhase.PackageCommitted) =>
                InstallerTransactionPhase.Verified,
            _ => throw new InvalidOperationException("No next test phase exists."),
        };

    private sealed class RecordingReleaseVerifier : IInstallerReleaseVerifier
    {
        private readonly InstallerReleaseManifest _manifest;

        internal RecordingReleaseVerifier(InstallerReleaseManifest manifest)
        {
            _manifest = manifest;
        }

        internal InstallerRequest? Request { get; private set; }

        internal RecordingReleaseLease? Lease { get; private set; }

        public Task<IInstallerReleaseLease> VerifyAsync(
            InstallerRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            Lease = new RecordingReleaseLease(request, _manifest);
            return Task.FromResult<IInstallerReleaseLease>(Lease);
        }
    }

    private sealed class RecordingReleaseLease : IInstallerReleaseLease
    {
        private readonly InstallerRequest _request;

        internal RecordingReleaseLease(
            InstallerRequest request,
            InstallerReleaseManifest manifest)
        {
            _request = request;
            Manifest = manifest;
            bool payloadAvailable = request.Operation != InstallerOperation.Uninstall;
            Release = manifest.CreateVerifiedRelease(
                payloadAvailable,
                payloadAvailable);
        }

        public VerifiedInstallerRelease Release { get; }

        public InstallerReleaseManifest Manifest { get; }

        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles { get; } = [];

        internal int ReverifyCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        public Task ReverifyAsync(
            InstallerRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_request, request);
            ReverifyCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCertificateStore : IInstallerCertificateStoreAdapter
    {
        private readonly InstallerCertificatePresence _presence;

        internal RecordingCertificateStore(InstallerCertificatePresence presence)
        {
            _presence = presence;
        }

        internal int InspectCalls { get; private set; }

        internal InstallerRequest? Request { get; private set; }

        public Task<InstallerCertificatePresence> InspectAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            Request = request;
            return Task.FromResult(_presence);
        }

        public Task ImportAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The verifier must not import certificates.");

        public Task RemoveExactAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The verifier must not remove certificates.");
    }

    private sealed class RecordingCertificateMutation :
        IInstallerCertificateMutation,
        IInstallerCertificateMutationVerifier
    {
        private readonly RecordingCertificateStore _store;

        internal RecordingCertificateMutation(RecordingCertificateStore store)
        {
            _store = store;
        }

        internal int ApplyCalls { get; private set; }

        internal int VerifyCalls { get; private set; }

        internal Action? ApplyObserver { get; set; }

        public Task ApplyAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            Assert.NotNull(release);
            ApplyCalls++;
            ApplyObserver?.Invoke();
            return Task.CompletedTask;
        }

        public async Task VerifyAppliedAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            VerifyCalls++;
            InstallerCertificatePresence presence = await _store
                .InspectAsync(request, release, cancellationToken);
            if (presence == InstallerCertificatePresence.IdentityConflict)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.identity_conflict");
            }

            if (request.Operation is InstallerOperation.Install or InstallerOperation.Repair
                && presence != InstallerCertificatePresence.ExactMatch)
            {
                throw new InstallerProtocolException(
                    "installer.certificate.installation_verification_failed");
            }
        }
    }

    private sealed class RecordingPackageInspector
        : IWindowsTargetUserPackageCommitInspector
    {
        internal int VerifyCalls { get; private set; }

        internal InstallerRequest? Request { get; private set; }

        internal InstallerReleaseManifest? Manifest { get; private set; }

        internal Exception? Failure { get; init; }

        public void Verify(
            InstallerRequest request,
            InstallerReleaseManifest manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            Request = request;
            Manifest = manifest;
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingMachineOperations
        : IWindowsMachineHelperMachineOperations
    {
        internal List<string> Calls { get; } = [];

        public Task PrepareAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerMachineHelperSessionDisposition disposition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"prepare:{disposition}");
            return Task.CompletedTask;
        }

        public Task ApplyAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerMachineHelperSessionDisposition disposition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"apply:{disposition}");
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerMachineHelperSessionDisposition disposition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"remove:{disposition}");
            return Task.CompletedTask;
        }

        public Task VerifyAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("verify");
            return Task.CompletedTask;
        }
    }
}
