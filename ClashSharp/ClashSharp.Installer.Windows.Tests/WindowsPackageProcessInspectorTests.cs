using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsPackageProcessInspectorTests
{
    [Fact]
    public void ExactPackageFamilyIsReportedAsRunning()
    {
        using var fixture = Fixture();
        var catalog = new RecordingCatalog(
        [
            new(
                WindowsPackageProcessObservationKind.Packaged,
                fixture.Manifest.PackageIdentity.PackageFamilyName),
        ]);
        var inspector = new WindowsPackageProcessInspector(catalog);

        bool running = inspector.IsApplicationRunning(
            fixture.Manifest,
            CancellationToken.None);

        Assert.True(running);
        Assert.Equal("ClashSharp", catalog.ExecutableBaseName);
    }

    [Fact]
    public void UnrelatedPackagedAndUnpackagedNamesAreNotProductProcesses()
    {
        using var fixture = Fixture();
        var inspector = new WindowsPackageProcessInspector(new RecordingCatalog(
        [
            new(WindowsPackageProcessObservationKind.Unpackaged, null),
            new(
                WindowsPackageProcessObservationKind.Packaged,
                "Other.Product_123456789abcd"),
        ]));

        Assert.False(inspector.IsApplicationRunning(
            fixture.Manifest,
            CancellationToken.None));
    }

    [Fact]
    public void UninspectableCandidateConservativelyBlocksMutation()
    {
        using var fixture = Fixture();
        var inspector = new WindowsPackageProcessInspector(new RecordingCatalog(
        [
            new(WindowsPackageProcessObservationKind.Uncertain, null),
        ]));

        Assert.True(inspector.IsApplicationRunning(
            fixture.Manifest,
            CancellationToken.None));
    }

    [Fact]
    public void MalformedCatalogOutputFailsClosed()
    {
        using var fixture = Fixture();
        var inspector = new WindowsPackageProcessInspector(new RecordingCatalog(
        [
            new(WindowsPackageProcessObservationKind.Packaged, null),
        ]));

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            inspector.IsApplicationRunning(fixture.Manifest, CancellationToken.None));

        Assert.Equal(
            "installer.application_process_inspection_failed",
            exception.DiagnosticCode);
    }

    [Fact]
    public void PreCancellationDoesNotEnumerateProcesses()
    {
        using var fixture = Fixture();
        var catalog = new RecordingCatalog([]);
        var inspector = new WindowsPackageProcessInspector(catalog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            inspector.IsApplicationRunning(fixture.Manifest, cancellation.Token));

        Assert.Null(catalog.ExecutableBaseName);
    }

    private static WindowsPayloadFixture Fixture() => new(
        createPayload: false,
        removeCurrentUserCertificateOnDispose: false);

    private sealed class RecordingCatalog : IWindowsPackageProcessCatalog
    {
        private readonly IReadOnlyList<WindowsPackageProcessObservation> _observations;

        internal RecordingCatalog(
            IReadOnlyList<WindowsPackageProcessObservation> observations)
        {
            _observations = observations;
        }

        internal string? ExecutableBaseName { get; private set; }

        public IReadOnlyList<WindowsPackageProcessObservation> ObserveCandidates(
            string executableBaseName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutableBaseName = executableBaseName;
            return _observations;
        }
    }
}
