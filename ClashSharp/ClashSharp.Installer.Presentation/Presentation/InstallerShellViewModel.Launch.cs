using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation;

public sealed partial class InstallerShellViewModel
{
    private const string MaintenanceHint = "以后需要修复、更新或卸载 Clash#，请重新运行完整安装目录中的 Setup.exe。";
    private bool _canLaunchApplication;

    /// <summary>Gets the explicit command that launches the verified installed application.</summary>
    public AsyncDelegateCommand LaunchApplicationCommand { get; }

    /// <summary>Gets whether a verified installed application can be opened from this shell state.</summary>
    public bool IsLaunchApplicationVisible => _canLaunchApplication && CanStartOperation
        && _runtime is IInstallerApplicationLaunchRuntime { SupportsApplicationLaunch: true };

    private void OfferInstalledApplication(InstallerExecutionResult result, bool installedResult)
    {
        _canLaunchApplication = installedResult && result.Outcome == InstallerExecutionOutcome.Succeeded
            && _runtime is IInstallerApplicationLaunchRuntime { SupportsApplicationLaunch: true };
        if (_canLaunchApplication)
        {
            StatusTitle = _hasPendingUserPreferences ? StatusTitle : "Clash# 安装已完成";
            StatusDetail = "可以选择“启动 Clash#”立即打开应用。" + MaintenanceHint
                + (result.PreferencesWarnings == InstallerPreferencesWarnings.None ? string.Empty : " " + StatusDetail);
        }
    }

    private async Task LaunchInstalledApplicationAsync()
    {
        if (!IsLaunchApplicationVisible || _runtime is not IInstallerApplicationLaunchRuntime launcher) { return; }
        OperationGeneration? generation = TryBeginOperation();
        if (generation is null) { return; }
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressStatus = "正在启动 Clash#…";
        try
        {
            await launcher.LaunchApplicationAsync(generation.Cancellation.Token);
            if (!IsCurrent(generation)) { return; }
            StatusBadge = "已启动";
            StatusTitle = "Clash# 已启动";
            StatusDetail = MaintenanceHint;
            DiagnosticCode = "installer.launch.completed";
        }
        catch (OperationCanceledException exception) when (generation.Cancellation.IsCancellationRequested && !InstallerExceptionPolicy.IsProcessFatal(exception))
        {
            if (IsCurrent(generation)) { ProgressStatus = "启动已取消。"; }
        }
        catch (Exception exception) when (InstallerPresentationExceptionPolicy.IsRecoverable(exception))
        {
            if (IsCurrent(generation))
            {
                StatusBadge = "已安装";
                StatusTitle = "应用已安装，启动未完成";
                StatusDetail = "请重试启动，或从 Windows 开始菜单打开 Clash#。" + MaintenanceHint;
                DiagnosticCode = exception is InstallerProtocolException protocol ? protocol.DiagnosticCode : "installer.launch.failed";
            }
        }
        finally { CompleteOperation(generation); }
    }
}
