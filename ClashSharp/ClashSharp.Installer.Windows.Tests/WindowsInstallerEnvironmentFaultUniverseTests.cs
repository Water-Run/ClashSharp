using System.ComponentModel;
using System.Security;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Universe and fault tests of the read-only environment inspection: the snapshot follows the platform policy and the
/// registration state for every combination, short-circuits when Windows is absent, never asks about a running
/// process for a package that is not installed, and every identity or package-manager failure class maps to one
/// stable code with its cause preserved.
/// </summary>
public sealed class WindowsInstallerEnvironmentFaultUniverseTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    public static TheoryData<string> Failures =>
    [
        nameof(IOException), nameof(UnauthorizedAccessException), nameof(SecurityException),
        nameof(InvalidOperationException), "Win32Exception5", "Win32Exception1450", nameof(TimeoutException),
        nameof(NotSupportedException), nameof(FormatException),
    ];

    [Fact]
    public async Task SnapshotFollowsThePlatformPolicyAndRegistrationStateForEveryCombination()
    {
        using var fixture = Fixture();
        InstallerPlatformFacts[] platforms =
        [
            new(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64),
            new(true, true, 22000, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64),
            new(true, true, 19045, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64),
            new(true, true, 26100, InstallerCpuArchitecture.Arm64, InstallerCpuArchitecture.X64),
            new(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X86),
            new(true, false, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64),
            new(false, false, 0, InstallerCpuArchitecture.Unknown, InstallerCpuArchitecture.Unknown),
        ];
        foreach (InstallerPlatformFacts facts in platforms)
        {
            InstallerPlatformAssessment assessment = InstallerPlatformPolicy.Evaluate(facts);
            foreach (string installed in new[] { "absent", "current", "older", "newer" })
            {
                foreach (bool running in new[] { false, true })
                {
                    var packages = new Packages(installed switch
                    {
                        "absent" => [],
                        "current" => [Registration(fixture, fixture.Manifest.ExpectedPackageVersion)],
                        "older" => [Registration(fixture, "1.2.3.3")],
                        _ => [Registration(fixture, "1.2.3.5")],
                    });
                    var processes = new Processes(running);
                    var environment = new WindowsInstallerEnvironment(
                        fixture.Manifest,
                        new Probe(facts),
                        packages,
                        processes,
                        () => TargetSid);

                    InstallerEnvironmentSnapshot snapshot = await environment.InspectAsync(Request(fixture), CancellationToken.None);

                    string label = $"{facts}/{installed}/{running}";
                    if (!facts.IsWindows)
                    {
                        Assert.False(snapshot.IsSupported, label);
                        Assert.Equal(assessment.DiagnosticCode, snapshot.BlockingDiagnosticCode);
                        Assert.Null(snapshot.InstalledPackageVersion);
                        Assert.False(snapshot.IsApplicationRunning);
                        Assert.Equal(0, packages.Queries);
                        Assert.Equal(0, processes.Calls);
                        continue;
                    }

                    Assert.Equal(assessment.IsSupported, snapshot.IsSupported);
                    Assert.Equal(assessment.IsSupported, snapshot.BlockingDiagnosticCode is null);
                    Assert.Equal(1, packages.Queries);
                    Assert.Equal(
                        installed switch
                        {
                            "absent" => null,
                            "current" => fixture.Manifest.ExpectedPackageVersion,
                            "older" => "1.2.3.3",
                            _ => "1.2.3.5",
                        },
                        snapshot.InstalledPackageVersion);
                    Assert.Equal(installed != "absent" && running, snapshot.IsApplicationRunning);
                    Assert.Equal(installed == "absent" ? 0 : 1, processes.Calls);
                    // The snapshot must always satisfy the contract the coordinator validates.
                    new InstallerRuntimeInspection(snapshot, null, fixture.Manifest.ExpectedPackageVersion).Validate();
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task EveryIdentityOrPackageManagerFailureClassMapsToOneStableCodeAndKeepsItsCause(string failure)
    {
        using var fixture = Fixture();
        var supported = new Probe(new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64));

        Exception identityCause = CreateFailure(failure);
        InstallerProtocolException identity = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsInstallerEnvironment(fixture.Manifest, supported, new Packages([]), new Processes(false), () => throw identityCause)
                .InspectAsync(Request(fixture), CancellationToken.None));
        Assert.Equal("installer.environment.current_user_invalid", identity.DiagnosticCode);
        Assert.Same(identityCause, identity.InnerException);

        Exception packageCause = CreateFailure(failure);
        InstallerProtocolException package = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsInstallerEnvironment(fixture.Manifest, supported, new Packages([]) { Failure = packageCause }, new Processes(false), () => TargetSid)
                .InspectAsync(Request(fixture), CancellationToken.None));
        Assert.Equal("installer.package.inspection_failed", package.DiagnosticCode);
        Assert.Same(packageCause, package.InnerException);
    }

    [Fact]
    public async Task IdentityFailuresThatAreNotExceptionsAndFatalFailuresAreHandledExactly()
    {
        using var fixture = Fixture();
        var supported = new Probe(new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64));

        foreach (string? sid in new[] { null, string.Empty, "not-a-sid", "S-1-5-18", "S-1-5-32-544" })
        {
            InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                new WindowsInstallerEnvironment(fixture.Manifest, supported, new Packages([]), new Processes(false), () => sid)
                    .InspectAsync(Request(fixture), CancellationToken.None));
            Assert.True(
                failure.DiagnosticCode is "installer.environment.current_user_invalid" or "installer.request.target_sid_invalid",
                $"'{sid}' -> {failure.DiagnosticCode}");
        }

        InstallerProtocolException mismatch = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsInstallerEnvironment(fixture.Manifest, supported, new Packages([]), new Processes(false), () => "S-1-5-21-9-9-9-1001")
                .InspectAsync(Request(fixture), CancellationToken.None));
        Assert.Equal("installer.environment.target_user_mismatch", mismatch.DiagnosticCode);

        var fatal = new FatalTestException("sentinel");
        Assert.Same(fatal, await Record.ExceptionAsync(() =>
            new WindowsInstallerEnvironment(fixture.Manifest, supported, new Packages([]) { Failure = fatal }, new Processes(false), () => TargetSid)
                .InspectAsync(Request(fixture), CancellationToken.None)));
        Assert.Same(fatal, await Record.ExceptionAsync(() =>
            new WindowsInstallerEnvironment(fixture.Manifest, supported, new Packages([]), new Processes(false), () => throw fatal)
                .InspectAsync(Request(fixture), CancellationToken.None)));
    }

    [Fact]
    public async Task ReleaseIdentityMismatchAndCancellationFailBeforeAnyProbe()
    {
        using var fixture = Fixture();
        var probe = new Probe(new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64));
        var environment = new WindowsInstallerEnvironment(fixture.Manifest, probe, new Packages([]), new Processes(false), () => TargetSid);
        InstallerRequest request = Request(fixture);

        foreach (InstallerRequest other in new[]
        {
            request with { ExpectedPackageVersion = "9.9.9.9" },
            request with { InstallerPayloadSha256 = new string('e', 64) },
        })
        {
            Assert.Equal(
                "installer.release.identity_mismatch",
                (await Assert.ThrowsAsync<InstallerProtocolException>(() => environment.InspectAsync(other, CancellationToken.None))).DiagnosticCode);
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.InspectAsync(request, cancellation.Token));
        Assert.Equal(0, probe.Calls);
    }

    private static Exception CreateFailure(string kind) => kind switch
    {
        nameof(IOException) => new IOException("io"),
        nameof(UnauthorizedAccessException) => new UnauthorizedAccessException("denied"),
        nameof(SecurityException) => new SecurityException("security"),
        nameof(InvalidOperationException) => new InvalidOperationException("state"),
        "Win32Exception5" => new Win32Exception(5),
        "Win32Exception1450" => new Win32Exception(1450),
        nameof(TimeoutException) => new TimeoutException("slow"),
        nameof(NotSupportedException) => new NotSupportedException("nope"),
        nameof(FormatException) => new FormatException("format"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);

    private static InstallerRequest Request(WindowsPayloadFixture fixture) => fixture.Request(InstallerOperation.Install, TargetSid);

    private static WindowsPackageRegistration Registration(WindowsPayloadFixture fixture, string version)
    {
        InstallerPackageIdentity identity = fixture.Manifest.PackageIdentity;
        return new WindowsPackageRegistration(
            identity.Name,
            identity.Publisher,
            identity.PublisherId,
            version,
            identity.Architecture,
            identity.ResourceId,
            identity.PackageFullName.Replace("_" + fixture.Manifest.ExpectedPackageVersion + "_", "_" + version + "_", StringComparison.Ordinal),
            identity.PackageFamilyName,
            true,
            false,
            false,
            false,
            false,
            false,
            false);
    }

    private sealed class Probe(InstallerPlatformFacts facts) : IInstallerPlatformProbe
    {
        internal int Calls { get; private set; }

        public InstallerPlatformFacts Inspect(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return facts;
        }
    }

    private sealed class Packages(IReadOnlyList<WindowsPackageRegistration> registrations) : IWindowsPackageManagerFacade
    {
        internal Exception? Failure { get; init; }

        internal int Queries { get; private set; }

        public IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(string userSecurityId, string packageFamilyName)
        {
            Queries++;
            return Failure is null ? registrations : throw Failure;
        }

        public Task DeployAsync(WindowsPackageDeploymentRequest request) => throw new InvalidOperationException("read-only");

        public Task RemoveAsync(string packageFullName) => throw new InvalidOperationException("read-only");
    }

    private sealed class Processes(bool running) : IWindowsPackageProcessInspector
    {
        internal int Calls { get; private set; }

        public bool IsApplicationRunning(InstallerReleaseManifest manifest, CancellationToken cancellationToken)
        {
            Calls++;
            return running;
        }
    }
}
