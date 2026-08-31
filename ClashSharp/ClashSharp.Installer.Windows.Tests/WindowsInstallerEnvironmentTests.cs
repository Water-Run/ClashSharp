using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerEnvironmentTests
{
    [Fact]
    public async Task ExactCurrentUserPackageAndProcessFactsReachCoordinatorPreflight()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var packageManager = new RecordingPackageManager
        {
            Registrations = [Registration(fixture, isHealthy: true)],
        };
        var processInspector = new RecordingProcessInspector(isRunning: true);
        var environment = Create(
            fixture,
            packageManager,
            processInspector,
            SupportedFacts(),
            TargetSid);

        InstallerEnvironmentSnapshot snapshot = await environment.InspectAsync(
            request,
            CancellationToken.None);

        Assert.True(snapshot.IsSupported);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, snapshot.InstalledPackageVersion);
        Assert.True(snapshot.IsApplicationRunning);
        Assert.Null(snapshot.BlockingDiagnosticCode);
        Assert.Equal(string.Empty, packageManager.UserSecurityId);
        Assert.Equal(fixture.Manifest.PackageIdentity.PackageFamilyName, packageManager.PackageFamilyName);
        Assert.Equal(1, processInspector.CallCount);
    }

    [Fact]
    public async Task MissingPackageSkipsProcessEnumeration()
    {
        using var fixture = Fixture();
        var processInspector = new RecordingProcessInspector(isRunning: true);
        var environment = Create(
            fixture,
            new RecordingPackageManager(),
            processInspector,
            SupportedFacts(),
            TargetSid);

        InstallerEnvironmentSnapshot snapshot = await environment.InspectAsync(
            fixture.Request(targetSid: TargetSid),
            CancellationToken.None);

        Assert.Null(snapshot.InstalledPackageVersion);
        Assert.False(snapshot.IsApplicationRunning);
        Assert.Equal(0, processInspector.CallCount);
    }

    [Fact]
    public async Task UnsupportedWindowsStillReportsPackageStateForSafeRemovalDecisions()
    {
        using var fixture = Fixture();
        var environment = Create(
            fixture,
            new RecordingPackageManager
            {
                Registrations = [Registration(fixture, isHealthy: true)],
            },
            new RecordingProcessInspector(isRunning: false),
            SupportedFacts() with { BuildNumber = 21_999 },
            TargetSid);

        InstallerEnvironmentSnapshot snapshot = await environment.InspectAsync(
            fixture.Request(InstallerOperation.Uninstall, TargetSid),
            CancellationToken.None);

        Assert.False(snapshot.IsSupported);
        Assert.Equal("installer.environment.windows_11_required", snapshot.BlockingDiagnosticCode);
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, snapshot.InstalledPackageVersion);
    }

    [Fact]
    public async Task NonWindowsFactStopsBeforeWindowsPackageApis()
    {
        using var fixture = Fixture();
        var packageManager = new RecordingPackageManager
        {
            Failure = new InvalidOperationException("must not be called"),
        };
        var environment = Create(
            fixture,
            packageManager,
            new RecordingProcessInspector(isRunning: false),
            SupportedFacts() with { IsWindows = false },
            TargetSid);

        InstallerEnvironmentSnapshot snapshot = await environment.InspectAsync(
            fixture.Request(targetSid: TargetSid),
            CancellationToken.None);

        Assert.False(snapshot.IsSupported);
        Assert.Equal("installer.environment.windows_required", snapshot.BlockingDiagnosticCode);
        Assert.Null(packageManager.UserSecurityId);
    }

    [Fact]
    public async Task TargetSidAndReleaseMismatchFailBeforePackageInspection()
    {
        using var fixture = Fixture();
        var packageManager = new RecordingPackageManager();
        var environment = Create(
            fixture,
            packageManager,
            new RecordingProcessInspector(isRunning: false),
            SupportedFacts(),
            TargetSid);

        InstallerProtocolException sid = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            environment.InspectAsync(
                fixture.Request(targetSid: "S-1-5-21-100-200-300-2002"),
                CancellationToken.None));
        Assert.Equal("installer.environment.target_user_mismatch", sid.DiagnosticCode);

        InstallerProtocolException release = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            environment.InspectAsync(
                fixture.Request(targetSid: TargetSid) with
                {
                    InstallerPayloadSha256 = new string('0', 64),
                },
                CancellationToken.None));
        Assert.Equal("installer.release.identity_mismatch", release.DiagnosticCode);
        Assert.Null(packageManager.UserSecurityId);
    }

    [Fact]
    public async Task PackageApiFailureIsSanitizedAndCancellationWins()
    {
        using var fixture = Fixture();
        var packageManager = new RecordingPackageManager
        {
            Failure = new InvalidOperationException("sensitive"),
        };
        var environment = Create(
            fixture,
            packageManager,
            new RecordingProcessInspector(isRunning: false),
            SupportedFacts(),
            TargetSid);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => environment.InspectAsync(
                fixture.Request(targetSid: TargetSid),
                CancellationToken.None));
        Assert.Equal("installer.package.inspection_failed", failure.DiagnosticCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.InspectAsync(
            fixture.Request(targetSid: TargetSid),
            cancellation.Token));
    }

    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    private static WindowsPayloadFixture Fixture() => new(
        createPayload: false,
        removeCurrentUserCertificateOnDispose: false);

    private static WindowsInstallerEnvironment Create(
        WindowsPayloadFixture fixture,
        IWindowsPackageManagerFacade packageManager,
        IWindowsPackageProcessInspector processInspector,
        InstallerPlatformFacts facts,
        string currentSid) => new(
            fixture.Manifest,
            new StaticPlatformProbe(facts),
            packageManager,
            processInspector,
            () => currentSid);

    private static InstallerPlatformFacts SupportedFacts() => new(
        IsWindows: true,
        IsWorkstation: true,
        BuildNumber: InstallerPlatformPolicy.MinimumWindowsBuild,
        OperatingSystemArchitecture: InstallerCpuArchitecture.X64,
        ProcessArchitecture: InstallerCpuArchitecture.X64);

    private static WindowsPackageRegistration Registration(
        WindowsPayloadFixture fixture,
        bool isHealthy)
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

    private sealed class StaticPlatformProbe : IInstallerPlatformProbe
    {
        private readonly InstallerPlatformFacts _facts;

        internal StaticPlatformProbe(InstallerPlatformFacts facts)
        {
            _facts = facts;
        }

        public InstallerPlatformFacts Inspect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _facts;
        }
    }

    private sealed class RecordingPackageManager : IWindowsPackageManagerFacade
    {
        internal IReadOnlyList<WindowsPackageRegistration> Registrations { get; init; } = [];

        internal Exception? Failure { get; init; }

        internal string? UserSecurityId { get; private set; }

        internal string? PackageFamilyName { get; private set; }

        public IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(
            string userSecurityId,
            string packageFamilyName)
        {
            UserSecurityId = userSecurityId;
            PackageFamilyName = packageFamilyName;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Registrations;
        }

        public Task DeployAsync(WindowsPackageDeploymentRequest request) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string packageFullName) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProcessInspector : IWindowsPackageProcessInspector
    {
        private readonly bool _isRunning;

        internal RecordingProcessInspector(bool isRunning)
        {
            _isRunning = isRunning;
        }

        internal int CallCount { get; private set; }

        public bool IsApplicationRunning(
            InstallerReleaseManifest manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifest.Validate();
            CallCount++;
            return _isRunning;
        }
    }
}
