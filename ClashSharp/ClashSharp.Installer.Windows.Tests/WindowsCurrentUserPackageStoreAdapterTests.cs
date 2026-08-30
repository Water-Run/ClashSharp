using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsCurrentUserPackageStoreAdapterTests
{
    [Fact]
    public async Task InspectQueriesOnlyTheExactCurrentUserFamilyAndMapsAllIdentityFields()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        WindowsPackageRegistration registration = Registration(fixture);
        var facade = new FakePackageManagerFacade(registration);
        var adapter = Adapter(facade, request);

        InstallerInstalledPackage? installed = await adapter.InspectAsync(
            request,
            lease,
            CancellationToken.None);

        Assert.NotNull(installed);
        Assert.Equal(fixture.Manifest.PackageIdentity.Name, installed.Name);
        Assert.Equal(fixture.Manifest.PackageIdentity.Publisher, installed.Publisher);
        Assert.Equal(fixture.Manifest.PackageIdentity.PublisherId, installed.PublisherId);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, installed.Version);
        Assert.Equal(fixture.Manifest.PackageIdentity.Architecture, installed.Architecture);
        Assert.Equal(fixture.Manifest.PackageIdentity.ResourceId, installed.ResourceId);
        Assert.Equal(fixture.Manifest.PackageIdentity.PackageFullName, installed.PackageFullName);
        Assert.Equal(
            fixture.Manifest.PackageIdentity.PackageFamilyName,
            installed.PackageFamilyName);
        Assert.True(installed.IsHealthy);
        Assert.Equal(
            fixture.Manifest.PackageIdentity.PackageFamilyName,
            facade.QueriedPackageFamilyName);
        Assert.Equal(string.Empty, facade.QueriedUserSecurityId);
        Assert.Equal(1, facade.InspectCalls);
    }

    [Fact]
    public async Task InspectReturnsAbsentOnlyForAnEmptyExactFamilyResult()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var facade = new FakePackageManagerFacade();
        var adapter = Adapter(facade, request);

        InstallerInstalledPackage? installed = await adapter.InspectAsync(
            request,
            lease,
            CancellationToken.None);

        Assert.Null(installed);
        Assert.Equal(1, facade.InspectCalls);
    }

    [Fact]
    public async Task InspectRejectsAmbiguousRegistrationsInsteadOfChoosingOne()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        WindowsPackageRegistration registration = Registration(fixture);
        var facade = new FakePackageManagerFacade(registration, registration);
        var adapter = Adapter(facade, request);

        await AssertDiagnosticAsync(
            () => adapter.InspectAsync(request, lease, CancellationToken.None),
            "installer.package.registration_ambiguous");
    }

    [Theory]
    [InlineData("bundle")]
    [InlineData("development")]
    [InlineData("framework")]
    [InlineData("optional")]
    [InlineData("resource")]
    [InlineData("stub")]
    public async Task InspectRejectsARegistrationThatIsNotThePrimaryApplicationPackage(
        string packageKind)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        WindowsPackageRegistration registration = packageKind switch
        {
            "bundle" => Registration(fixture) with { IsBundle = true },
            "development" => Registration(fixture) with { IsDevelopmentMode = true },
            "framework" => Registration(fixture) with { IsFramework = true },
            "optional" => Registration(fixture) with { IsOptional = true },
            "resource" => Registration(fixture) with { IsResourcePackage = true },
            "stub" => Registration(fixture) with { IsStub = true },
            _ => throw new InvalidOperationException("Unknown test package kind."),
        };
        var facade = new FakePackageManagerFacade(registration);
        var adapter = Adapter(facade, request);

        await AssertDiagnosticAsync(
            () => adapter.InspectAsync(request, lease, CancellationToken.None),
            "installer.package.installed_identity_mismatch");
    }

    [Fact]
    public async Task AdapterRejectsARequestNotBoundToTheWindowsLease()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        InstallerRequest changed = fixture.Request(InstallerOperation.Repair);
        var facade = new FakePackageManagerFacade();
        var adapter = Adapter(facade, changed);

        await AssertDiagnosticAsync(
            () => adapter.InspectAsync(changed, lease, CancellationToken.None),
            "installer.release.request_changed");

        Assert.Equal(0, facade.InspectCalls);
    }

    [Fact]
    public async Task AdapterRejectsARequestForAnyUserOtherThanTheInvoker()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var facade = new FakePackageManagerFacade();
        var adapter = new WindowsCurrentUserPackageStoreAdapter(
            facade,
            () => request.TargetSid + "-different");

        await AssertDiagnosticAsync(
            () => adapter.InspectAsync(request, lease, CancellationToken.None),
            "installer.package.target_user_mismatch");

        Assert.Equal(0, facade.InspectCalls);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task DeployUsesOnlyLockedLocalPackagesAndConservativeOptions(
        InstallerOperation operation)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request(operation);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var facade = new FakePackageManagerFacade();
        var adapter = Adapter(facade, request);

        await adapter.DeployAsync(request, lease, CancellationToken.None);

        WindowsPackageDeploymentRequest deployment = Assert.IsType<WindowsPackageDeploymentRequest>(
            facade.DeploymentRequest);
        Assert.True(deployment.PrimaryPackageUri.IsFile);
        Assert.Equal(fixture.PrimaryPath, deployment.PrimaryPackageUri.LocalPath);
        Uri dependency = Assert.Single(deployment.DependencyPackageUris);
        Assert.True(dependency.IsFile);
        Assert.Equal(fixture.DependencyPath, dependency.LocalPath);
        Assert.False(deployment.AllowUnsigned);
        Assert.False(deployment.DeferRegistrationWhenPackagesAreInUse);
        Assert.False(deployment.DeveloperMode);
        Assert.False(deployment.ForceAppShutdown);
        Assert.False(deployment.ForceTargetAppShutdown);
        Assert.False(deployment.ForceUpdateFromAnyVersion);
        Assert.False(deployment.InstallAllResources);
        Assert.False(deployment.RequiredContentGroupOnly);
        Assert.False(deployment.RetainFilesOnFailure);
        Assert.False(deployment.StageInPlace);
        Assert.Equal(1, facade.DeployCalls);
    }

    [Fact]
    public void ProductionOptionMappingPreservesTheFailClosedDeploymentPolicy()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        Uri primary = new("file:///C:/payload/product.msix");
        Uri dependency = new("file:///C:/payload/dependencies/x64/framework.msix");
        var deployment = new WindowsPackageDeploymentRequest(
            primary,
            [dependency],
            AllowUnsigned: false,
            DeferRegistrationWhenPackagesAreInUse: false,
            DeveloperMode: false,
            ForceAppShutdown: false,
            ForceTargetAppShutdown: false,
            ForceUpdateFromAnyVersion: false,
            InstallAllResources: false,
            RequiredContentGroupOnly: false,
            RetainFilesOnFailure: false,
            StageInPlace: false);

        global::Windows.Management.Deployment.AddPackageOptions options =
            WindowsPackageManagerFacade.CreateOptions(deployment);

        Assert.False(options.AllowUnsigned);
        Assert.False(options.DeferRegistrationWhenPackagesAreInUse);
        Assert.False(options.DeveloperMode);
        Assert.False(options.ForceAppShutdown);
        Assert.False(options.ForceTargetAppShutdown);
        Assert.False(options.ForceUpdateFromAnyVersion);
        Assert.False(options.InstallAllResources);
        Assert.False(options.RequiredContentGroupOnly);
        Assert.False(options.RetainFilesOnFailure);
        Assert.False(options.StageInPlace);
        Assert.Equal(dependency, Assert.Single(options.DependencyPackageUris));
        Assert.Empty(options.OptionalPackageFamilyNames);
        Assert.Empty(options.OptionalPackageUris);
        Assert.Empty(options.RelatedPackageUris);
        Assert.Null(options.ExternalLocationUri);
        Assert.Null(options.TargetVolume);
    }

    [Fact]
    public async Task CancellationBeforeDeploymentDoesNotReachAppxSvc()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var facade = new FakePackageManagerFacade();
        var adapter = Adapter(facade, request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.DeployAsync(request, lease, cancellation.Token));

        Assert.Equal(0, facade.DeployCalls);
    }

    [Fact]
    public async Task CancellationAfterDeploymentStartsCannotReleaseTheLeaseBeforeTerminalState()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var facade = new FakePackageManagerFacade { BlockDeployment = true };
        var adapter = Adapter(facade, request);
        using var cancellation = new CancellationTokenSource();

        Task operation = adapter.DeployAsync(request, lease, cancellation.Token);
        await facade.DeploymentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(operation.IsCompleted);
        facade.CompleteDeployment();
        await operation;
    }

    [Fact]
    public async Task DeploymentFailureUsesAStableDiagnosticAndRetainsTheCause()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var cause = new InvalidOperationException("sensitive AppXSVC detail");
        var facade = new FakePackageManagerFacade { DeploymentException = cause };
        var adapter = Adapter(facade, request);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => adapter.DeployAsync(request, lease, CancellationToken.None));

        Assert.Equal("installer.package.deployment_failed", exception.DiagnosticCode);
        Assert.Same(cause, exception.InnerException);
        Assert.DoesNotContain("sensitive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FatalDeploymentFailureIsNotConvertedIntoARecoverableProtocolResult()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture();
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var cause = new FatalTestException("fatal test sentinel");
        var facade = new FakePackageManagerFacade { DeploymentException = cause };
        var adapter = Adapter(facade, request);

        FatalTestException exception = await Assert.ThrowsAsync<FatalTestException>(
            () => adapter.DeployAsync(request, lease, CancellationToken.None));

        Assert.Same(cause, exception);
    }

    [Fact]
    public async Task RemoveTargetsOnlyTheExactObservedFullNameWithoutPayloadFiles()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);
        await using var lease = new WindowsInstallerReleaseLease(
            request,
            fixture.Manifest,
            payloadRoot: null,
            lockedFiles: [],
            directoryGuards: []);
        var facade = new FakePackageManagerFacade();
        var adapter = Adapter(facade, request);
        InstallerInstalledPackage installed = Installed(fixture);

        await adapter.RemoveAsync(request, lease, installed, CancellationToken.None);

        Assert.Equal(1, facade.RemoveCalls);
        Assert.Equal(installed.PackageFullName, facade.RemovedPackageFullName);
    }

    [Fact]
    public async Task RemoveRejectsAnObservedPackageOutsideTheReleaseFamily()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);
        await using var lease = new WindowsInstallerReleaseLease(
            request,
            fixture.Manifest,
            payloadRoot: null,
            lockedFiles: [],
            directoryGuards: []);
        var facade = new FakePackageManagerFacade();
        var adapter = Adapter(facade, request);
        InstallerInstalledPackage installed = Installed(fixture) with
        {
            PackageFamilyName = "Contoso.Other_1234567890abc",
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            adapter.RemoveAsync(request, lease, installed, CancellationToken.None));

        Assert.Equal(0, facade.RemoveCalls);
    }

    [Fact]
    public async Task CancellationAfterRemovalStartsWaitsForAppxSvcTerminalState()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(createPayload: false);
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);
        await using var lease = new WindowsInstallerReleaseLease(
            request,
            fixture.Manifest,
            payloadRoot: null,
            lockedFiles: [],
            directoryGuards: []);
        var facade = new FakePackageManagerFacade { BlockRemoval = true };
        var adapter = Adapter(facade, request);
        using var cancellation = new CancellationTokenSource();

        Task operation = adapter.RemoveAsync(
            request,
            lease,
            Installed(fixture),
            cancellation.Token);
        await facade.RemovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.False(operation.IsCompleted);
        facade.CompleteRemoval();
        await operation;
    }

    private static WindowsCurrentUserPackageStoreAdapter Adapter(
        IWindowsPackageManagerFacade facade,
        InstallerRequest request) =>
        new(facade, () => request.TargetSid);

    private static WindowsPackageRegistration Registration(
        WindowsPayloadFixture fixture,
        bool isHealthy = true)
    {
        var identity = fixture.Manifest.PackageIdentity;
        return new WindowsPackageRegistration(
            identity.Name,
            identity.Publisher,
            identity.PublisherId,
            fixture.Manifest.ExpectedPackageVersion,
            identity.Architecture,
            identity.ResourceId,
            identity.PackageFullName,
            identity.PackageFamilyName,
            isHealthy,
            IsBundle: false,
            IsDevelopmentMode: false,
            IsFramework: false,
            IsOptional: false,
            IsResourcePackage: false,
            IsStub: false);
    }

    private static InstallerInstalledPackage Installed(WindowsPayloadFixture fixture)
    {
        WindowsPackageRegistration registration = Registration(fixture);
        return new InstallerInstalledPackage(
            registration.Name,
            registration.Publisher,
            registration.PublisherId,
            registration.Version,
            registration.Architecture,
            registration.ResourceId,
            registration.PackageFullName,
            registration.PackageFamilyName,
            registration.IsHealthy);
    }

    private static async Task AssertDiagnosticAsync(Func<Task> action, string expected)
    {
        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            action);
        Assert.Equal(expected, exception.DiagnosticCode);
    }

    private sealed class FakePackageManagerFacade : IWindowsPackageManagerFacade
    {
        private readonly IReadOnlyList<WindowsPackageRegistration> _registrations;
        private readonly TaskCompletionSource _deploymentCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _removalCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakePackageManagerFacade(params WindowsPackageRegistration[] registrations)
        {
            _registrations = registrations;
        }

        internal int InspectCalls { get; private set; }

        internal int DeployCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        internal string? QueriedPackageFamilyName { get; private set; }

        internal string? QueriedUserSecurityId { get; private set; }

        internal WindowsPackageDeploymentRequest? DeploymentRequest { get; private set; }

        internal string? RemovedPackageFullName { get; private set; }

        internal Exception? DeploymentException { get; init; }

        internal bool BlockDeployment { get; init; }

        internal bool BlockRemoval { get; init; }

        internal TaskCompletionSource DeploymentStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource RemovalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(
            string userSecurityId,
            string packageFamilyName)
        {
            InspectCalls++;
            QueriedUserSecurityId = userSecurityId;
            QueriedPackageFamilyName = packageFamilyName;
            return _registrations;
        }

        public Task DeployAsync(WindowsPackageDeploymentRequest request)
        {
            DeployCalls++;
            DeploymentRequest = request;
            DeploymentStarted.TrySetResult();
            if (DeploymentException is not null)
            {
                return Task.FromException(DeploymentException);
            }

            return BlockDeployment ? _deploymentCompletion.Task : Task.CompletedTask;
        }

        public Task RemoveAsync(string packageFullName)
        {
            RemoveCalls++;
            RemovedPackageFullName = packageFullName;
            RemovalStarted.TrySetResult();
            return BlockRemoval ? _removalCompletion.Task : Task.CompletedTask;
        }

        internal void CompleteDeployment() => _deploymentCompletion.TrySetResult();

        internal void CompleteRemoval() => _removalCompletion.TrySetResult();
    }
}
