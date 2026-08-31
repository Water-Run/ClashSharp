using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Certificates;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerCertificatePostconditionTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerCertificatePresence.ExactMatch)]
    [InlineData(InstallerOperation.Repair, InstallerCertificatePresence.ExactMatch)]
    [InlineData(InstallerOperation.Uninstall, InstallerCertificatePresence.Missing)]
    [InlineData(InstallerOperation.Uninstall, InstallerCertificatePresence.ExactMatch)]
    public async Task AcceptsOnlyTheOperationSpecificReadOnlyPostcondition(
        InstallerOperation operation,
        InstallerCertificatePresence presence)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(operation, TargetSid);
        var store = new RecordingStore(presence);
        var verifier = new WindowsInstallerCertificatePostcondition(store);

        await verifier.ApplyAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            CancellationToken.None);

        Assert.Equal(1, store.InspectCalls);
        Assert.Equal(0, store.ImportCalls);
        Assert.Equal(0, store.RemoveCalls);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerCertificatePresence.Missing)]
    [InlineData(InstallerOperation.Repair, InstallerCertificatePresence.IdentityConflict)]
    [InlineData(InstallerOperation.Uninstall, InstallerCertificatePresence.IdentityConflict)]
    [InlineData(InstallerOperation.Uninstall, (InstallerCertificatePresence)99)]
    public async Task RejectsMissingInstallOrAnyIdentityConflictWithoutMutation(
        InstallerOperation operation,
        InstallerCertificatePresence presence)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(operation, TargetSid);
        var store = new RecordingStore(presence);
        var verifier = new WindowsInstallerCertificatePostcondition(store);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => verifier.ApplyAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                CancellationToken.None));

        Assert.Equal("installer.certificate.postcondition_failed", exception.DiagnosticCode);
        Assert.Equal(0, store.ImportCalls);
        Assert.Equal(0, store.RemoveCalls);
    }

    [Fact]
    public async Task PreCancellationDoesNotInspectOrMutate()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var store = new RecordingStore(InstallerCertificatePresence.ExactMatch);
        var verifier = new WindowsInstallerCertificatePostcondition(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.ApplyAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                cancellation.Token));

        Assert.Equal(0, store.InspectCalls);
    }

    private static WindowsPayloadFixture Fixture() =>
        new(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);

    private sealed class RecordingStore : IInstallerCertificateStoreAdapter
    {
        private readonly InstallerCertificatePresence _presence;

        internal RecordingStore(InstallerCertificatePresence presence)
        {
            _presence = presence;
        }

        internal int InspectCalls { get; private set; }

        internal int ImportCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        public Task<InstallerCertificatePresence> InspectAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            Assert.NotNull(release);
            InspectCalls++;
            return Task.FromResult(_presence);
        }

        public Task ImportAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            ImportCalls++;
            throw new InvalidOperationException("The parent postcondition must not import.");
        }

        public Task RemoveExactAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            RemoveCalls++;
            throw new InvalidOperationException("The parent postcondition must not remove.");
        }
    }

    private sealed class FakeReleaseLease : IInstallerReleaseLease
    {
        internal FakeReleaseLease(
            InstallerRequest request,
            InstallerReleaseManifest manifest)
        {
            Manifest = manifest;
            bool payloadAvailable = request.Operation != InstallerOperation.Uninstall;
            Release = manifest.CreateVerifiedRelease(payloadAvailable, payloadAvailable);
        }

        public VerifiedInstallerRelease Release { get; }

        public InstallerReleaseManifest Manifest { get; }

        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles { get; } = [];

        public Task ReverifyAsync(
            InstallerRequest request,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
