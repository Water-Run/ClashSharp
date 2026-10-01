using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsPackageDeploymentPreflightTests
{
    private static readonly WindowsPackageFootprint Exact = new(4, new string('a', 64), 8, new string('b', 64));
    private const string InstalledRoot = @"C:\Program Files\WindowsApps\owned-test-package";

    [Fact]
    public async Task AbsentExactIdentitiesCheckPrimaryAndDependenciesWithoutReadingFiles()
    {
        using var fixture = Fixture();
        await using var lease = new Lease(fixture.Manifest);
        var catalog = new Catalog();
        var reader = new Reader();

        await new WindowsPackageDeploymentPreflight(catalog, reader)
            .VerifyCanDeployAsync(fixture.Request(), lease, CancellationToken.None);

        Assert.Equal(fixture.Manifest.Dependencies.Count + 1, catalog.Queries.Count);
        Assert.Equal(fixture.Manifest.PackageIdentity.PackageFullName, catalog.Queries[0].FullName);
        Assert.Equal(fixture.Manifest.Dependencies[0].PackageFullName, catalog.Queries[1].FullName);
        Assert.Empty(reader.Candidates);
        Assert.Equal(0, reader.InstalledReads);
        Assert.Equal(2, lease.Reverifications);
    }

    [Fact]
    public async Task MatchingPrimaryAndDependencyFootprintsAllowFurtherDeploymentValidation()
    {
        using var fixture = Fixture();
        await using var lease = new Lease(fixture.Manifest);
        var catalog = new Catalog { Result = (_, _) => [InstalledRoot] };
        var reader = new Reader();

        await new WindowsPackageDeploymentPreflight(catalog, reader)
            .VerifyCanDeployAsync(fixture.Request(InstallerOperation.Repair), lease, CancellationToken.None);

        Assert.Equal([InstallerPayloadFileRole.PrimaryPackage, InstallerPayloadFileRole.DependencyPackage], reader.Candidates);
        Assert.Equal(2, reader.InstalledReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryOrDependencyConflictRejectsTheCandidate(bool dependencyConflict)
    {
        using var fixture = Fixture();
        await using var lease = new Lease(fixture.Manifest);
        string conflictingName = dependencyConflict ? fixture.Manifest.Dependencies[0].PackageFullName
            : fixture.Manifest.PackageIdentity.PackageFullName;
        var catalog = new Catalog { Result = (_, name) => name == conflictingName ? [InstalledRoot] : [] };
        var reader = new Reader { Installed = Exact with { SignatureSha256 = new string('c', 64) } };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsPackageDeploymentPreflight(catalog, reader)
                .VerifyCanDeployAsync(fixture.Request(), lease, CancellationToken.None));

        Assert.Equal("installer.package.content_conflict", failure.DiagnosticCode);
        Assert.Single(reader.Candidates);
        Assert.Equal(dependencyConflict ? InstallerPayloadFileRole.DependencyPackage : InstallerPayloadFileRole.PrimaryPackage, reader.Candidates[0]);
    }

    [Theory]
    [InlineData("ambiguous", "installer.package.registration_ambiguous")]
    [InlineData("relative", "installer.package.installed_path_invalid")]
    [InlineData("null-result", "installer.package.inspection_result_invalid")]
    public async Task UncertainCatalogCannotGrantMaintenance(string scenario, string diagnostic)
    {
        using var fixture = Fixture();
        await using var lease = new Lease(fixture.Manifest);
        var catalog = new Catalog
        {
            Result = (_, _) => scenario switch
            {
                "ambiguous" => [InstalledRoot, @"D:\another-package"],
                "relative" => ["relative-package"],
                _ => null!,
            },
        };
        var reader = new Reader();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsPackageDeploymentPreflight(catalog, reader)
                .VerifyCanDeployAsync(fixture.Request(), lease, CancellationToken.None));

        Assert.Equal(diagnostic, failure.DiagnosticCode);
        Assert.Empty(reader.Candidates);
    }

    [Fact]
    public async Task CancellationAfterCandidateReadStopsInstalledInspection()
    {
        using var fixture = Fixture();
        await using var lease = new Lease(fixture.Manifest);
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader { AfterCandidate = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsPackageDeploymentPreflight(new Catalog { Result = (_, _) => [InstalledRoot] }, reader)
                .VerifyCanDeployAsync(fixture.Request(), lease, cancellation.Token));

        Assert.Equal(0, reader.InstalledReads);
    }

    [Fact]
    public async Task InaccessibleFootprintsReturnSanitizedInspectionFailure()
    {
        using var fixture = Fixture();
        await using var lease = new Lease(fixture.Manifest);
        var reader = new Reader { Failure = new IOException("private-path-and-state") };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsPackageDeploymentPreflight(new Catalog { Result = (_, _) => [InstalledRoot] }, reader)
                .VerifyCanDeployAsync(fixture.Request(), lease, CancellationToken.None));

        Assert.Equal("installer.package.content_inspection_failed", failure.DiagnosticCode);
        Assert.DoesNotContain("private", failure.DiagnosticCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseMismatchCannotQueryInstalledPackages()
    {
        using var fixture = Fixture();
        await using var lease = new Lease(fixture.Manifest);
        var catalog = new Catalog();

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsPackageDeploymentPreflight(catalog, new Reader())
                .VerifyCanDeployAsync(fixture.Request() with { ExpectedPackageVersion = "1.2.3.5" }, lease, CancellationToken.None));

        Assert.Equal("installer.release.identity_mismatch", failure.DiagnosticCode);
        Assert.Empty(catalog.Queries);
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);

    private sealed class Catalog : IWindowsInstalledPackageFootprintCatalog
    {
        internal List<(string Family, string FullName)> Queries { get; } = [];
        internal Func<string, string, IReadOnlyList<string>> Result { get; init; } = (_, _) => [];

        public IReadOnlyList<string> FindInstalledRoots(string packageFamilyName, string packageFullName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add((packageFamilyName, packageFullName));
            return Result(packageFamilyName, packageFullName);
        }
    }

    private sealed class Reader : IWindowsPackageFootprintReader
    {
        internal List<InstallerPayloadFileRole> Candidates { get; } = [];
        internal int InstalledReads { get; private set; }
        internal WindowsPackageFootprint Installed { get; init; } = Exact;
        internal Action? AfterCandidate { get; init; }
        internal Exception? Failure { get; init; }

        public WindowsPackageFootprint ReadCandidate(IInstallerReleaseLease release, InstallerPayloadFileEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) { throw Failure; }
            Candidates.Add(entry.Role);
            AfterCandidate?.Invoke();
            return Exact;
        }

        public WindowsPackageFootprint ReadInstalled(string installedRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstalledReads++;
            return Installed;
        }
    }

    private sealed class Lease(InstallerReleaseManifest manifest) : IInstallerReleaseLease
    {
        public InstallerReleaseManifest Manifest { get; } = manifest;
        public VerifiedInstallerRelease Release { get; } = manifest.CreateVerifiedRelease(true, true);
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        internal int Reverifications { get; private set; }

        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reverifications++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
