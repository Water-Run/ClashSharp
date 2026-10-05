using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceOriginalContentsReaderTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task HealthyOlderPackageProducesCompleteOriginalEvidenceWithoutMutation()
    {
        using var fixture = Fixture();
        await using var release = new Lease(fixture.Manifest);
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var state = new State(plan);

        WindowsMaintenanceOriginalContents contents = await Reader(state, release).ReadAsync(plan, CancellationToken.None);

        Assert.Equal("1.2.3.3", contents.Package.Version);
        Assert.Equal(Hash, contents.PackageContentsSha256);
        Assert.Equal(plan.PayloadTargets.Count, contents.Files.Count);
        Assert.Equal(2, state.PackageReads);
        Assert.Equal(2, release.Verifications);
        Assert.Equal(1, state.PayloadReads);
        contents.RequirePlan(plan);
    }

    [Theory]
    [InlineData("missing-package")]
    [InlineData("unhealthy")]
    [InlineData("ambiguous")]
    [InlineData("missing-root")]
    [InlineData("ambiguous-root")]
    [InlineData("relative-root")]
    [InlineData("missing-association")]
    [InlineData("wrong-association")]
    [InlineData("wrong-payload")]
    [InlineData("wrong-package-hash")]
    [InlineData("registration-changed")]
    public async Task IncompleteOrChangingEvidenceCannotAuthorizeOriginalInstallation(string scenario)
    {
        using var fixture = Fixture();
        await using var release = new Lease(fixture.Manifest);
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var state = new State(plan);
        if (scenario == "missing-package") { state.Packages = []; }
        else if (scenario == "unhealthy") { state.Packages = [state.Packages[0] with { IsHealthy = false }]; }
        else if (scenario == "ambiguous") { state.Packages = [state.Packages[0], state.Packages[0]]; }
        else if (scenario == "missing-root") { state.Roots = []; }
        else if (scenario == "ambiguous-root") { state.Roots = [@"C:\packages\old", @"C:\packages\other"]; }
        else if (scenario == "relative-root") { state.Roots = ["old"]; }
        else if (scenario == "missing-association") { state.Association = new(InstallerMachineAssociationStatus.Missing, null); }
        else if (scenario == "wrong-association")
        {
            state.Association = new(InstallerMachineAssociationStatus.Valid, InstallerMachineAssociation.Create(Owner, new string('b', 64)));
        }
        else if (scenario == "wrong-payload") { state.Files = state.Files.Skip(1).ToArray(); }
        else if (scenario == "wrong-package-hash") { state.PackageHash = "invalid"; }
        else
        {
            state.AfterTrustRead = () => state.Packages = [state.Packages[0] with
            {
                Version = fixture.Manifest.ExpectedPackageVersion,
                PackageFullName = fixture.Manifest.PackageIdentity.PackageFullName,
            }];
        }

        await Assert.ThrowsAsync<InstallerProtocolException>(() => Reader(state, release).ReadAsync(plan, CancellationToken.None));
    }

    [Fact]
    public async Task EqualManifestFromAnotherLeaseCannotBeUsedAsTheAuthenticatedCandidate()
    {
        using var fixture = Fixture();
        byte[] bytes = InstallerReleaseManifestCodec.Serialize(fixture.Manifest);
        await using var release = new Lease(InstallerReleaseManifestCodec.Parse(bytes));
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var state = new State(plan);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            Reader(state, release).ReadAsync(plan, CancellationToken.None));

        Assert.Equal("installer.recovery.original_contents_mismatch", failure.DiagnosticCode);
        Assert.Equal(0, state.PackageReads);
    }

    [Fact]
    public async Task ExpiredCandidateRejectsBeforePackageOrPayloadObservation()
    {
        using var fixture = Fixture();
        await using var release = new Lease(fixture.Manifest) { Expired = true };
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var state = new State(plan);

        await Assert.ThrowsAsync<InstallerProtocolException>(() => Reader(state, release).ReadAsync(plan, CancellationToken.None));

        Assert.Equal(0, state.PackageReads);
        Assert.Equal(0, state.PayloadReads);
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
    private static WindowsMachineDeploymentPlan Plan(WindowsPayloadFixture fixture) => WindowsMachineDeploymentPlan.Create(
        fixture.Request(InstallerOperation.Repair, Owner), fixture.Manifest, InstallerMachineAssociation.Create(Owner, Hash),
        @"C:\Program Files", @"C:\ProgramData", @"C:\Users\owner");
    private static WindowsMaintenanceOriginalContentsReader Reader(State state, Lease release) =>
        new(state, state, state, state, state, state, state, release);

    private sealed class Lease(InstallerReleaseManifest manifest) : IInstallerReleaseLease
    {
        internal bool Expired { get; init; }
        internal int Verifications { get; private set; }
        public InstallerReleaseManifest Manifest { get; } = manifest;
        public VerifiedInstallerRelease Release { get; } = manifest.CreateVerifiedRelease(true, true);
        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles => [];
        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Verifications++;
            return Expired ? throw new InstallerProtocolException("installer.release.lease_expired") : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class State : IWindowsPackageManagerFacade, IWindowsInstalledPackageFootprintCatalog,
        IWindowsPackageFootprintReader, IWindowsMaintenancePayloadStateReader, IWindowsMaintenancePackageStateReader,
        IWindowsMachineAssociationStore, IWindowsMaintenanceTrustStateReader
    {
        private readonly WindowsMachineDeploymentPlan _plan;
        internal State(WindowsMachineDeploymentPlan plan)
        {
            _plan = plan;
            var identity = plan.Manifest.PackageIdentity;
            const string version = "1.2.3.3";
            Packages = [new WindowsPackageRegistration(identity.Name, identity.Publisher, identity.PublisherId,
                version, identity.Architecture, identity.ResourceId, $"{identity.Name}_{version}_x64__{identity.PublisherId}",
                identity.PackageFamilyName, true, false, false, false, false, false, false)];
            Files = plan.PayloadTargets.Select(target => new WindowsMaintenanceFileFingerprint(target.Source.Path,
                target.RelativeTargetPath.Replace('\\', '/'), target.Source.Length, target.Source.Sha256)).ToArray();
            Association = new(InstallerMachineAssociationStatus.Valid, plan.Association);
        }
        internal IReadOnlyList<WindowsPackageRegistration> Packages { get; set; }
        internal IReadOnlyList<string> Roots { get; set; } = [@"C:\packages\old"];
        internal IReadOnlyList<WindowsMaintenanceFileFingerprint> Files { get; set; }
        internal InstallerMachineAssociationObservation Association { get; set; }
        internal string PackageHash { get; set; } = Hash;
        internal int PackageReads { get; private set; }
        internal int PayloadReads { get; private set; }
        internal Action? AfterTrustRead { get; set; }

        public IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(string userSecurityId, string packageFamilyName)
        {
            Assert.Equal(Owner, userSecurityId);
            Assert.Equal(_plan.Manifest.PackageIdentity.PackageFamilyName, packageFamilyName);
            PackageReads++;
            return Packages;
        }
        public IReadOnlyList<string> FindInstalledRoots(string packageFamilyName, string packageFullName, CancellationToken cancellationToken)
        {
            Assert.Equal(Packages[0].PackageFullName, packageFullName);
            return Roots;
        }
        public WindowsPackageFootprint ReadInstalled(string installedRoot, CancellationToken cancellationToken) => new(4, Hash, 8, Hash);
        public string Read(string originalPackageRoot, CancellationToken cancellationToken) => PackageHash;
        public IReadOnlyList<WindowsMaintenanceFileFingerprint> Read(WindowsMachineDeploymentPlan plan, string originalPackageRoot, CancellationToken cancellationToken)
        {
            Assert.Same(_plan, plan);
            PayloadReads++;
            return Files;
        }
        public Task<InstallerMachineAssociationObservation> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(Association);
        public Task<WindowsMaintenanceTrustFingerprint> ReadAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken)
        {
            AfterTrustRead?.Invoke();
            return Task.FromResult(new WindowsMaintenanceTrustFingerprint(Hash, Hash,
                _plan.Manifest.PackageCertificateThumbprint, _plan.Manifest.CertificateSha256));
        }
        public WindowsPackageFootprint ReadCandidate(IInstallerReleaseLease release, InstallerPayloadFileEntry entry, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task DeployAsync(WindowsPackageDeploymentRequest request) => throw new InvalidOperationException();
        public Task RemoveAsync(string packageFullName) => throw new InvalidOperationException();
        public Task WriteAndVerifyAsync(InstallerMachineAssociation association, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task DeleteAndVerifyAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task VerifyExactAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task VerifyAbsentAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public void Dispose() { }
    }
}
