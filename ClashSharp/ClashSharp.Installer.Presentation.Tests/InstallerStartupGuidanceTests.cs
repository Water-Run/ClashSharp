using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerStartupGuidanceTests
{
    [Fact]
    public async Task EmbeddedPackageVersionRemainsVisibleWhenInspectionFails()
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => throw new InstallerProtocolException("installer.elevation.authenticode_invalid"),
        };
        using var model = new InstallerShellViewModel(runtime, "1.0.0.0", isInternalCandidate: true);

        Assert.Equal("1.0.0.0", model.DisplayVersion);
        await model.InitializeAsync();

        Assert.Equal("1.0.0.0", model.DisplayVersion);
        Assert.Contains("内部测试版", model.ReleaseChannelText, StringComparison.Ordinal);
        Assert.False(model.CanExecuteMutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInspectionHidesAccountActionsIncludingAfterRefresh(bool previouslyReady)
    {
        var runtime = new AccountRuntime { FailInspection = !previouslyReady };
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        if (previouslyReady)
        {
            Assert.True(model.IsOwnerTransferActionVisible);
            Assert.True(model.IsRetiredUninstallActionVisible);
            runtime.FailInspection = true;
            await model.RefreshCommand.ExecuteAsync();
        }

        Assert.False(model.IsOwnerTransferActionVisible);
        Assert.False(model.IsRetiredUninstallActionVisible);
        Assert.False(model.OwnerTransferCommand.CanExecute(null));
        Assert.False(model.RetiredUninstallCommand.CanExecute(null));

        runtime.FailInspection = false;
        await model.RefreshCommand.ExecuteAsync();
        Assert.True(model.IsOwnerTransferActionVisible);
        Assert.True(model.IsRetiredUninstallActionVisible);
    }

    [Theory]
    [InlineData("installer.elevation.authenticode_invalid", "安装包签名校验失败", "重新获取")]
    [InlineData("installer.elevation.certificate_untrusted", "安装包签名尚未受信任", "提供者")]
    [InlineData("installer.elevation.certificate_expired", "安装包签名证书已过期", "新版本")]
    [InlineData("installer.elevation.authenticode_missing", "安装包缺少有效签名", "完整安装包")]
    [InlineData("installer.release.payload_file_set_invalid", "安装文件不完整或不匹配", "完整解压")]
    [InlineData("installer.release.locked_file_hash_mismatch", "安装文件校验失败", "重新获取")]
    public async Task InspectionFailureExplainsTheCauseAndConcreteNextStep(
        string code, string title, string nextStep)
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Inspect = _ => throw new InstallerProtocolException(code, new IOException("private-file-path")),
        };
        using var model = new InstallerShellViewModel(runtime);

        await model.InitializeAsync();

        Assert.Equal(title, model.StatusTitle);
        Assert.Contains(nextStep, model.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("private-file-path", model.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(code, model.DiagnosticCode);
        Assert.False(model.CanExecuteMutations);
        Assert.False(model.PrimaryActionCommand.CanExecute(null));
    }

    private sealed class AccountRuntime : IInstallerRuntime, IInstallerOwnerTransferRuntime, IInstallerRetiredUninstallRuntime
    {
        internal bool FailInspection { get; set; }
        public bool SupportsOwnerTransfer => true;
        public bool SupportsRetiredUninstall => true;

        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) =>
            FailInspection ? throw new InstallerProtocolException("installer.elevation.certificate_untrusted")
                : Task.FromResult(InstallerPresentationTestData.Readiness());

        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation, IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inspection-only fixture.");

        public Task<InstallerExecutionResult> TransferAndExecuteAsync(Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress> progress, CancellationToken cancellationToken) => throw new InvalidOperationException("Inspection-only fixture.");

        public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inspection-only fixture.");
    }
}
