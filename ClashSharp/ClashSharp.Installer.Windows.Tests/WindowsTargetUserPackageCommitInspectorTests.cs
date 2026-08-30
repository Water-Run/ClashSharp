using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsTargetUserPackageCommitInspectorTests
{
    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public void InstalledPostconditionQueriesTheExactJournalUserAndFamily(
        InstallerOperation operation)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        InstallerRequest request = fixture.Request(operation);
        var facade = new RecordingPackageManager(Registration(fixture.Manifest));
        var inspector = new WindowsTargetUserPackageCommitInspector(facade);

        inspector.Verify(request, fixture.Manifest, CancellationToken.None);

        Assert.Equal(request.TargetSid, facade.UserSecurityId);
        Assert.Equal(
            fixture.Manifest.PackageIdentity.PackageFamilyName,
            facade.PackageFamilyName);
        Assert.Equal(1, facade.QueryCalls);
    }

    [Fact]
    public void UninstallPostconditionRequiresTheExactJournalUserToBeAbsent()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);
        var facade = new RecordingPackageManager();
        var inspector = new WindowsTargetUserPackageCommitInspector(facade);

        inspector.Verify(request, fixture.Manifest, CancellationToken.None);

        Assert.Equal(request.TargetSid, facade.UserSecurityId);
        Assert.Equal(1, facade.QueryCalls);
    }

    [Theory]
    [InlineData("install_absent", InstallerOperation.Install,
        "installer.package.deployment_verification_failed")]
    [InlineData("repair_absent", InstallerOperation.Repair,
        "installer.package.deployment_verification_failed")]
    [InlineData("installed_unhealthy", InstallerOperation.Install,
        "installer.package.deployment_verification_failed")]
    [InlineData("installed_wrong_version", InstallerOperation.Repair,
        "installer.package.deployment_verification_failed")]
    [InlineData("uninstall_present", InstallerOperation.Uninstall,
        "installer.package.removal_verification_failed")]
    public void UnsatisfiedTargetUserPostconditionCannotCommitPackage(
        string scenario,
        InstallerOperation operation,
        string expectedDiagnostic)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        WindowsPackageRegistration? registration = scenario switch
        {
            "install_absent" or "repair_absent" => null,
            "installed_unhealthy" => Registration(fixture.Manifest) with
            {
                IsHealthy = false,
            },
            "installed_wrong_version" => Registration(
                fixture.Manifest,
                version: "1.2.3.3"),
            "uninstall_present" => Registration(fixture.Manifest),
            _ => throw new InvalidOperationException("Unknown package test scenario."),
        };
        var facade = registration is null
            ? new RecordingPackageManager()
            : new RecordingPackageManager(registration);
        var inspector = new WindowsTargetUserPackageCommitInspector(facade);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            inspector.Verify(
                fixture.Request(operation),
                fixture.Manifest,
                CancellationToken.None));

        Assert.Equal(expectedDiagnostic, exception.DiagnosticCode);
        Assert.Equal(1, facade.QueryCalls);
    }

    [Fact]
    public void RequestReleaseMismatchFailsBeforeAppxSvcQuery()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        InstallerRequest changed = fixture.Request() with
        {
            ExpectedPackageVersion = "1.2.3.5",
        };
        var facade = new RecordingPackageManager();
        var inspector = new WindowsTargetUserPackageCommitInspector(facade);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            inspector.Verify(changed, fixture.Manifest, CancellationToken.None));

        Assert.Equal("installer.release.identity_mismatch", exception.DiagnosticCode);
        Assert.Equal(0, facade.QueryCalls);
    }

    [Fact]
    public void PreCancellationFailsBeforeAppxSvcQuery()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        var facade = new RecordingPackageManager();
        var inspector = new WindowsTargetUserPackageCommitInspector(facade);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            inspector.Verify(
                fixture.Request(),
                fixture.Manifest,
                cancellation.Token));

        Assert.Equal(0, facade.QueryCalls);
    }

    private static WindowsPackageRegistration Registration(
        InstallerReleaseManifest manifest,
        string? version = null)
    {
        InstallerPackageIdentity identity = manifest.PackageIdentity;
        string installedVersion = version ?? manifest.ExpectedPackageVersion;
        return new WindowsPackageRegistration(
            identity.Name,
            identity.Publisher,
            identity.PublisherId,
            installedVersion,
            identity.Architecture,
            identity.ResourceId,
            $"{identity.Name}_{installedVersion}_{identity.Architecture}_{identity.ResourceId}_{identity.PublisherId}",
            identity.PackageFamilyName,
            IsHealthy: true,
            IsBundle: false,
            IsDevelopmentMode: false,
            IsFramework: false,
            IsOptional: false,
            IsResourcePackage: false,
            IsStub: false);
    }

    private sealed class RecordingPackageManager : IWindowsPackageManagerFacade
    {
        private readonly IReadOnlyList<WindowsPackageRegistration> _registrations;

        internal RecordingPackageManager(params WindowsPackageRegistration[] registrations)
        {
            _registrations = registrations;
        }

        internal int QueryCalls { get; private set; }

        internal string? UserSecurityId { get; private set; }

        internal string? PackageFamilyName { get; private set; }

        public IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(
            string userSecurityId,
            string packageFamilyName)
        {
            QueryCalls++;
            UserSecurityId = userSecurityId;
            PackageFamilyName = packageFamilyName;
            return _registrations;
        }

        public Task DeployAsync(WindowsPackageDeploymentRequest request) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string packageFullName) =>
            throw new NotSupportedException();
    }
}
