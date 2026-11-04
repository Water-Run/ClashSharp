using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed partial class ProductionInstallerRuntimeTests
{
    [Theory]
    [InlineData("installer.environment.windows_11_required", "Windows 版本过旧，无法安装", "建议升级到 Windows 11")]
    [InlineData("installer.environment.windows_server_2025_required", "Windows Server 版本过旧，无法安装", "升级到 Windows Server 2025")]
    [InlineData("installer.environment.desktop_experience_required", "需要桌面体验", "Server Core")]
    [InlineData("installer.environment.x64_os_required", "需要 x64 版本的 Windows", "ARM64")]
    [InlineData("installer.environment.x64_process_required", "安装器未以 x64 方式运行", "Setup.exe")]
    [InlineData("installer.environment.windows_required", "当前系统不支持安装", InstallerPlatformGuidance.SupportedPlatforms)]
    public async Task UnsupportedPlatformExplainsTheSpecificReasonAndNextStep(string diagnostic, string title, string detailFragment)
    {
        var backend = new RecordingBackend(new InstallerRuntimeInspection(
            new InstallerEnvironmentSnapshot(false, null, false, diagnostic), null, Version));
        using var runtime = new ProductionInstallerRuntime(backend);
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();

        Assert.Equal(title, viewModel.StatusTitle);
        Assert.Contains(detailFragment, viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(diagnostic, viewModel.DiagnosticCode);
        Assert.Equal("暂不可用", viewModel.StatusBadge);
        Assert.False(viewModel.CanExecuteMutations);
        Assert.Contains(viewModel.Capabilities, capability => capability.Name == "系统要求" && !capability.IsReady
            && capability.Detail == InstallerPlatformGuidance.For(diagnostic).Requirement);
        Assert.Empty(backend.Operations);
    }

    [Fact]
    public void WindowsTenGuidanceStatesEndOfSupportAndRecommendsUpgrading()
    {
        InstallerPlatformGuidance guidance = InstallerPlatformGuidance.For("installer.environment.windows_11_required");

        Assert.Contains("Windows 10", guidance.Detail, StringComparison.Ordinal);
        Assert.Contains("2025 年 10 月 14 日结束支持", guidance.Detail, StringComparison.Ordinal);
        Assert.Contains("Mica", guidance.Detail, StringComparison.Ordinal);
        Assert.Contains("建议升级", guidance.Requirement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedPlatformWithAnInstalledCopyNamesTheReasonAndKeepsRemoval()
    {
        var backend = new RecordingBackend(new InstallerRuntimeInspection(
            new InstallerEnvironmentSnapshot(false, Version, false, "installer.environment.windows_11_required"), null, Version));
        using var runtime = new ProductionInstallerRuntime(backend);

        InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(CancellationToken.None);

        Assert.Equal("仅安全卸载可用", readiness.StatusTitle);
        Assert.StartsWith(InstallerPlatformGuidance.For("installer.environment.windows_11_required").Requirement, readiness.StatusDetail, StringComparison.Ordinal);
        Assert.Equal([InstallerOperation.Uninstall], readiness.AllowedOperations);
    }
}
