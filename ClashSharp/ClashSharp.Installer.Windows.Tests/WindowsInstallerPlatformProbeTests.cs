using System.Runtime.InteropServices;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Windows.Platform;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerPlatformProbeTests
{
    [Theory]
    [InlineData(3, "Server", true)]
    [InlineData(2, "Server", true)]
    [InlineData(3, "Server Core", false)]
    [InlineData(2, "Server Core", false)]
    [InlineData(3, null, false)]
    [InlineData(3, "", false)]
    [InlineData(3, "Client", false)]
    [InlineData(3, "server", false)]
    [InlineData(3, "Server ", false)]
    [InlineData(0, "Server", false)]
    [InlineData(4, "Server", false)]
    public void ServerAdmissionRequiresBothNativeProductAndExactDesktopInstallation(
        byte productType, string? installationType, bool expectedSupported)
    {
        InstallerPlatformFacts facts = WindowsInstallerPlatformProbe.CreateFacts(
            productType, 26100, 9, Architecture.X64, installationType);

        Assert.False(facts.IsWorkstation);
        Assert.Equal(expectedSupported, facts.IsServerDesktopExperience);
        Assert.Equal(expectedSupported, InstallerPlatformPolicy.Evaluate(facts).IsSupported);
    }

    [Fact]
    public void WorkstationDoesNotBecomeAServerFromTheRegistryValue()
    {
        InstallerPlatformFacts facts = WindowsInstallerPlatformProbe.CreateFacts(
            1, 22000, 9, Architecture.X64, "Server");

        Assert.True(facts.IsWorkstation);
        Assert.False(facts.IsServerDesktopExperience);
        Assert.True(InstallerPlatformPolicy.Evaluate(facts).IsSupported);
    }

    [Theory]
    [InlineData(20348, 9, Architecture.X64, "installer.environment.windows_server_2025_required")]
    [InlineData(26100, 12, Architecture.X64, "installer.environment.x64_os_required")]
    [InlineData(26100, 9, Architecture.X86, "installer.environment.x64_process_required")]
    public void NativeServerFactsPreserveVersionAndArchitectureRejections(
        int build, ushort nativeArchitecture, Architecture processArchitecture, string expectedDiagnostic)
    {
        InstallerPlatformFacts facts = WindowsInstallerPlatformProbe.CreateFacts(
            3, build, nativeArchitecture, processArchitecture, "Server");

        InstallerPlatformAssessment assessment = InstallerPlatformPolicy.Evaluate(facts);
        Assert.False(assessment.IsSupported);
        Assert.Equal(expectedDiagnostic, assessment.DiagnosticCode);
    }
}
