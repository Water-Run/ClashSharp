using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation;

public sealed partial class InstallerShellViewModel
{
    private string RetiredUninstallRecoveryDetail =>
        T("若卸载尚未完成，请使用同一个安装器，在“安装详情与诊断”中再次选择“卸载此账户副本”继续。");
    private TaskCompletionSource<bool>? _retiredUninstallDecision;
    private bool _isRetiredUninstallConfirmationVisible;

    /// <summary>Gets the distinct account-copy removal or recovery action.</summary>
    public AsyncDelegateCommand RetiredUninstallCommand { get; }

    /// <summary>Gets explicit consent to start account-copy removal and authentication.</summary>
    public DelegateCommand ConfirmRetiredUninstallCommand { get; }

    /// <summary>Gets the cancellation action that leaves the backend uninvoked.</summary>
    public DelegateCommand DeclineRetiredUninstallCommand { get; }

    /// <summary>Gets whether the trusted runtime supplies the idle dedicated uninstall action.</summary>
    public bool IsRetiredUninstallActionVisible =>
        !_accountActionsBlocked && CanStartOperation && _runtime is IInstallerRetiredUninstallRuntime { SupportsRetiredUninstall: true };

    /// <summary>Gets whether explicit account-copy removal consent is pending.</summary>
    public bool IsRetiredUninstallConfirmationVisible
    {
        get => _isRetiredUninstallConfirmationVisible;
        private set
        {
            if (SetProperty(ref _isRetiredUninstallConfirmationVisible, value))
            {
                OnPropertyChanged(nameof(IsCancelActionVisible));
                OnPropertyChanged(nameof(IsOperationSummaryVisible));
                ConfirmRetiredUninstallCommand.NotifyCanExecuteChanged();
                DeclineRetiredUninstallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task<bool> ConfirmRetiredUninstallAsync(OperationGeneration generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(generation) || _retiredUninstallDecision is not null || _ownerTransferDecision is not null)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.confirmation_unavailable");
        }
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _retiredUninstallDecision = decision;
        IsRetiredUninstallConfirmationVisible = true;
        ProgressStatus = T("等待确认，尚未开始卸载。");
        using CancellationTokenRegistration registration = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
        try
        {
            return await decision.Task;
        }
        finally
        {
            _retiredUninstallDecision = null;
            if (!_disposed)
            {
                IsRetiredUninstallConfirmationVisible = false;
                ProgressStatus = cancellationToken.IsCancellationRequested ? T("正在收尾…") : T("正在检查此账户副本…");
            }
        }
    }

    private string DescribeRetiredUninstallProgress(string messageKey) => messageKey switch
    {
        "installer.progress.preflight" => T("正在检查此账户的应用…"),
        "installer.progress.prepared" => T("正在读取卸载进度…"),
        "installer.progress.machine_prepare" or "installer.progress.machine_remove_authorize"
            or "installer.progress.machine_remove" => T("正在核验共享服务保持原状…"),
        "installer.progress.package_remove" => T("正在卸载此账户的应用包…"),
        "installer.progress.certificate_remove" => T("正在核验此账户的证书清理结果…"),
        "installer.progress.verifying" => T("正在核验此账户副本已移除…"),
        "installer.progress.completed" => T("此账户副本已卸载。"),
        _ => T("正在处理此账户副本…"),
    };

    private void ApplyRetiredUninstallResult(InstallerExecutionResult result)
    {
        if (result.Outcome == InstallerExecutionOutcome.Succeeded)
        {
            StatusTitle = T("已卸载此账户副本");
            StatusDetail = T("此账户的应用副本已移除，其他账户使用的共享服务保持原状。");
            ProgressStatus = T("此账户副本已卸载。");
        }
        else
        {
            StatusDetail = DescribeRetiredUninstallFailure(result.DiagnosticCode);
        }
    }

    private string DescribeRetiredUninstallFailure(string code) => code switch
    {
        "installer.retired_uninstall.current_owner" => T("此账户仍在使用共享服务。请重新检查后使用普通卸载。"),
        "installer.retired_uninstall.ordinary_state_pending" => T("请先由共享服务的使用账户完成未结束的安装或卸载。"),
        "installer.owner_transfer.pending" => T("请先由接收服务的账户完成未结束的账户切换。"),
        _ => RetiredUninstallRecoveryDetail,
    };
}
