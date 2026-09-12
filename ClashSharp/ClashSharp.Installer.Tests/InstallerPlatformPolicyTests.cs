using ClashSharp.Installer.Platform;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerPlatformPolicyTests
{
    [Theory]
    [InlineData(InstallerPlatformPolicy.MinimumWindowsBuild)]
    [InlineData(22631)]
    [InlineData(26100)]
    [InlineData(int.MaxValue)]
    public void WindowsElevenAndFutureNativeX64BuildsAreSupported(int buildNumber)
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(buildNumber));

        Assert.True(result.IsSupported);
        Assert.Equal("installer.environment.supported", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(21999)]
    public void BuildsBeforeWindowsElevenFailClosed(int buildNumber)
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(buildNumber));

        Assert.False(result.IsSupported);
        Assert.Equal("installer.environment.windows_11_required", result.DiagnosticCode);
    }

    [Fact]
    public void NonWindowsKernelIsRejectedBeforeOtherFactsAreConsidered()
    {
        InstallerPlatformFacts facts = Facts(
            int.MaxValue,
            isWindows: false,
            isWorkstation: false,
            operatingSystemArchitecture: InstallerCpuArchitecture.Unknown,
            processArchitecture: InstallerCpuArchitecture.Unknown);

        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(facts);

        Assert.False(result.IsSupported);
        Assert.Equal("installer.environment.windows_required", result.DiagnosticCode);
    }

    [Fact]
    public void WindowsServerWithoutDesktopExperienceIsRejected()
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(
            26100,
            isWorkstation: false));

        Assert.False(result.IsSupported);
        Assert.Equal("installer.environment.desktop_experience_required", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(26100)]
    [InlineData(int.MaxValue)]
    public void Server2025DesktopExperiencePassesThePlatformGate(int buildNumber)
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(
            buildNumber, isWorkstation: false) with
        { IsServerDesktopExperience = true });

        Assert.True(result.IsSupported);
        Assert.Equal("installer.environment.supported", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17763)]
    [InlineData(20348)]
    [InlineData(26099)]
    public void OlderServerDesktopBuildsRemainUnsupported(int buildNumber)
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(
            buildNumber, isWorkstation: false) with
        { IsServerDesktopExperience = true });

        Assert.False(result.IsSupported);
        Assert.Equal("installer.environment.windows_server_2025_required", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(InstallerCpuArchitecture.Arm64, InstallerCpuArchitecture.X64, "installer.environment.x64_os_required")]
    [InlineData(InstallerCpuArchitecture.X86, InstallerCpuArchitecture.X86, "installer.environment.x64_os_required")]
    [InlineData(InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X86, "installer.environment.x64_process_required")]
    public void ServerDesktopStillRequiresNativeX64(
        InstallerCpuArchitecture operatingSystemArchitecture,
        InstallerCpuArchitecture processArchitecture,
        string diagnosticCode)
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(
            26100, isWorkstation: false,
            operatingSystemArchitecture: operatingSystemArchitecture,
            processArchitecture: processArchitecture) with
        { IsServerDesktopExperience = true });

        Assert.False(result.IsSupported);
        Assert.Equal(diagnosticCode, result.DiagnosticCode);
    }

    [Theory]
    [InlineData(InstallerCpuArchitecture.Unknown)]
    [InlineData(InstallerCpuArchitecture.X86)]
    [InlineData(InstallerCpuArchitecture.Arm)]
    [InlineData(InstallerCpuArchitecture.Arm64)]
    [InlineData((InstallerCpuArchitecture)999)]
    public void NonX64OperatingSystemArchitectureIsRejected(
        InstallerCpuArchitecture architecture)
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(
            26100,
            operatingSystemArchitecture: architecture));

        Assert.False(result.IsSupported);
        Assert.Equal("installer.environment.x64_os_required", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(InstallerCpuArchitecture.Unknown)]
    [InlineData(InstallerCpuArchitecture.X86)]
    [InlineData(InstallerCpuArchitecture.Arm)]
    [InlineData(InstallerCpuArchitecture.Arm64)]
    [InlineData((InstallerCpuArchitecture)999)]
    public void NonX64InstallerProcessIsRejected(InstallerCpuArchitecture architecture)
    {
        InstallerPlatformAssessment result = InstallerPlatformPolicy.Evaluate(Facts(
            26100,
            processArchitecture: architecture));

        Assert.False(result.IsSupported);
        Assert.Equal("installer.environment.x64_process_required", result.DiagnosticCode);
    }

    [Fact]
    public void MissingFactsAreRejectedByThePolicyBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => InstallerPlatformPolicy.Evaluate(null!));
    }

    private static InstallerPlatformFacts Facts(
        int buildNumber,
        bool isWindows = true,
        bool isWorkstation = true,
        InstallerCpuArchitecture operatingSystemArchitecture = InstallerCpuArchitecture.X64,
        InstallerCpuArchitecture processArchitecture = InstallerCpuArchitecture.X64) =>
        new(
            isWindows,
            isWorkstation,
            buildNumber,
            operatingSystemArchitecture,
            processArchitecture);
}
