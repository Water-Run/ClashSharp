using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation;

/// <summary>Coordinates accessible UI state without acquiring package or machine authority.</summary>
public sealed partial class InstallerShellViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> ProgressMessages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["installer.progress.preflight"] = "正在检查系统与已安装版本…",
            ["installer.progress.prepared"] = "正在准备操作…",
            ["installer.progress.machine_prepare"] = "正在准备系统组件…",
            ["installer.progress.certificate"] = "正在核验并配置用户证书…",
            ["installer.progress.package"] = "正在安装应用…",
            ["installer.progress.machine"] = "正在配置系统服务…",
            ["installer.progress.machine_remove_authorize"] = "正在准备移除系统组件…",
            ["installer.progress.machine_remove"] = "正在移除系统级组件…",
            ["installer.progress.package_remove"] = "正在移除用户应用包…",
            ["installer.progress.certificate_remove"] = "正在安全释放安装器拥有的证书…",
            ["installer.progress.verifying"] = "正在检查完成状态…",
            ["installer.progress.completed"] = "操作完成。",
        };

    private readonly IInstallerRuntime _runtime;
    private readonly object _operationSync = new();
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;
    private bool _runtimeDisposePending;
    private bool _isBusy;
    private bool _isCancellationRequested;
    private bool _canExecuteMutations;
    private bool _isProgressIndeterminate;
    private int _progressValue;
    private string _statusTitle = "正在准备";
    private string _statusDetail = "正在检查系统与已安装版本。";
    private string _statusBadge = "检查中";
    private string _displayVersion = "—";
    private string _diagnosticCode = "installer.runtime.not_inspected";
    private string _progressStatus = "等待就绪检查";
    private string _primaryActionText = "安装";
    private string _secondaryActionText = string.Empty;
    private bool _hasSecondaryAction;
    private InstallerOperation _primaryOperation = InstallerOperation.Install;
    private InstallerOperation? _secondaryOperation;
    private TaskCompletionSource<bool>? _ownerTransferDecision;
    private bool _isOwnerTransferConfirmationVisible;
    private string _ownerTransferConfirmationTitle = string.Empty;
    private IReadOnlyList<InstallerCapabilityStatus> _capabilities = Array.Empty<InstallerCapabilityStatus>();

    /// <summary>Initializes the shell against an explicit readiness/execution port.</summary>
    public InstallerShellViewModel(IInstallerRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        RefreshCommand = new AsyncDelegateCommand(
            RefreshAsync,
            () => !IsBusy,
            SetUnhandledCommandFailure);
        PrimaryActionCommand = new AsyncDelegateCommand(
            ExecutePrimaryOperationAsync,
            () => !IsBusy && CanExecuteMutations,
            SetUnhandledCommandFailure);
        SecondaryActionCommand = new AsyncDelegateCommand(
            ExecuteSecondaryOperationAsync,
            () => !IsBusy && CanExecuteMutations && HasSecondaryAction,
            SetUnhandledCommandFailure);
        CancelCommand = new DelegateCommand(CancelActiveOperation, () => IsBusy && !IsCancellationRequested);
        OwnerTransferCommand = new AsyncDelegateCommand(
            () => ExecuteOperationAsync(null, ownerTransfer: true), () => IsOwnerTransferActionVisible, SetUnhandledCommandFailure);
        ConfirmOwnerTransferCommand = new DelegateCommand(
            () => _ownerTransferDecision?.TrySetResult(true), () => IsOwnerTransferConfirmationVisible);
        DeclineOwnerTransferCommand = new DelegateCommand(
            () => _ownerTransferDecision?.TrySetResult(false), () => IsOwnerTransferConfirmationVisible);
        RetiredUninstallCommand = new AsyncDelegateCommand(
            () => ExecuteOperationAsync(null, retiredUninstall: true), () => IsRetiredUninstallActionVisible, SetUnhandledCommandFailure);
        ConfirmRetiredUninstallCommand = new DelegateCommand(
            () => _retiredUninstallDecision?.TrySetResult(true), () => IsRetiredUninstallConfirmationVisible);
        DeclineRetiredUninstallCommand = new DelegateCommand(
            () => _retiredUninstallDecision?.TrySetResult(false), () => IsRetiredUninstallConfirmationVisible);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the command that re-evaluates trusted runtime readiness.</summary>
    public AsyncDelegateCommand RefreshCommand { get; }

    /// <summary>Gets the operation recommended by trusted installed-state inspection.</summary>
    public AsyncDelegateCommand PrimaryActionCommand { get; }

    /// <summary>Gets the optional second operation authorized for the same product card.</summary>
    public AsyncDelegateCommand SecondaryActionCommand { get; }

    /// <summary>Gets the command that cooperatively cancels the active generation.</summary>
    public DelegateCommand CancelCommand { get; }

    /// <summary>Gets the distinct user-initiated account transfer action.</summary>
    public AsyncDelegateCommand OwnerTransferCommand { get; }

    /// <summary>Gets the explicit decision to accept the currently displayed offer.</summary>
    public DelegateCommand ConfirmOwnerTransferCommand { get; }

    /// <summary>Gets the decision to decline without creating transfer state.</summary>
    public DelegateCommand DeclineOwnerTransferCommand { get; }

    /// <summary>Gets whether this trusted composition supplies an idle dedicated transfer action.</summary>
    public bool IsOwnerTransferActionVisible => !IsBusy && _runtime is IInstallerOwnerTransferRuntime { SupportsOwnerTransfer: true };

    /// <summary>Gets whether the current operation is waiting for explicit user consent.</summary>
    public bool IsOwnerTransferConfirmationVisible
    {
        get => _isOwnerTransferConfirmationVisible;
        private set
        {
            if (SetProperty(ref _isOwnerTransferConfirmationVisible, value))
            {
                OnPropertyChanged(nameof(IsCancelActionVisible));
                ConfirmOwnerTransferCommand.NotifyCanExecuteChanged();
                DeclineOwnerTransferCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets the localized new-transfer or recovery confirmation heading.</summary>
    public string OwnerTransferConfirmationTitle
    {
        get => _ownerTransferConfirmationTitle;
        private set => SetProperty(ref _ownerTransferConfirmationTitle, value);
    }

    /// <summary>Gets whether any readiness or mutation task owns the single-flight gate.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyActionVisibility();
                NotifyCommandAvailability();
            }
        }
    }

    /// <summary>Gets whether the trusted idle state exposes its one primary mutation.</summary>
    public bool IsPrimaryActionVisible => !IsBusy && CanExecuteMutations;

    /// <summary>Gets whether the trusted installed state exposes its secondary mutation.</summary>
    public bool IsSecondaryActionVisible => IsPrimaryActionVisible && HasSecondaryAction;

    /// <summary>Gets whether the single active generation exposes cancellation as its only action.</summary>
    public bool IsCancelActionVisible => IsBusy && !IsOwnerTransferConfirmationVisible && !IsRetiredUninstallConfirmationVisible;

    /// <summary>Gets whether this generation is waiting for a requested cancellation to finish.</summary>
    public bool IsCancellationRequested
    {
        get => _isCancellationRequested;
        private set
        {
            if (SetProperty(ref _isCancellationRequested, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether the trusted runtime proved every mutation prerequisite.</summary>
    public bool CanExecuteMutations
    {
        get => _canExecuteMutations;
        private set
        {
            if (SetProperty(ref _canExecuteMutations, value))
            {
                NotifyActionVisibility();
                NotifyCommandAvailability();
            }
        }
    }

    /// <summary>Gets whether the progress bar represents an unbounded inspection.</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>Gets completed progress in the inclusive zero-to-one-hundred range.</summary>
    public int ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, Math.Clamp(value, 0, 100));
    }

    /// <summary>Gets the current high-level readiness or outcome.</summary>
    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    /// <summary>Gets the sanitized explanation of the current state.</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    /// <summary>Gets a short visible state independent of color.</summary>
    public string StatusBadge
    {
        get => _statusBadge;
        private set => SetProperty(ref _statusBadge, value);
    }

    /// <summary>Gets the trusted release version or an explicit placeholder.</summary>
    public string DisplayVersion
    {
        get => _displayVersion;
        private set => SetProperty(ref _displayVersion, value);
    }

    /// <summary>Gets a stable code suitable for support and automated diagnosis.</summary>
    public string DiagnosticCode
    {
        get => _diagnosticCode;
        private set => SetProperty(ref _diagnosticCode, value);
    }

    /// <summary>Gets localized progress text.</summary>
    public string ProgressStatus
    {
        get => _progressStatus;
        private set => SetProperty(ref _progressStatus, value);
    }

    /// <summary>Gets the trusted primary operation label.</summary>
    public string PrimaryActionText
    {
        get => _primaryActionText;
        private set => SetProperty(ref _primaryActionText, value);
    }

    /// <summary>Gets the label for the optional second installed-state operation.</summary>
    public string SecondaryActionText
    {
        get => _secondaryActionText;
        private set => SetProperty(ref _secondaryActionText, value);
    }

    /// <summary>Gets whether the trusted readiness snapshot authorizes a second operation.</summary>
    public bool HasSecondaryAction
    {
        get => _hasSecondaryAction;
        private set
        {
            if (SetProperty(ref _hasSecondaryAction, value))
            {
                OnPropertyChanged(nameof(IsSecondaryActionVisible));
                NotifyCommandAvailability();
            }
        }
    }

    /// <summary>Gets the visible list of independently checked capabilities.</summary>
    public IReadOnlyList<InstallerCapabilityStatus> Capabilities
    {
        get => _capabilities;
        private set => SetProperty(ref _capabilities, value);
    }

    /// <summary>Performs the initial readiness inspection.</summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>
    /// Requests cooperative cancellation for window shutdown. A privileged operation may continue
    /// until its authenticated helper reaches a terminal result.
    /// </summary>
    public void RequestCancellation()
    {
        CancellationTokenSource? cancellation;
        lock (_operationSync)
        {
            if (_disposed || _activeCancellation is null || IsCancellationRequested)
            {
                return;
            }

            cancellation = _activeCancellation;
            IsCancellationRequested = true;
        }

        StatusBadge = "正在取消";
        StatusTitle = "正在取消操作";
        StatusDetail = "已请求取消，正在等待当前操作安全结束。";
        ProgressStatus = "正在等待操作收尾…";
        IsProgressIndeterminate = true;
        cancellation.Cancel();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        IDisposable? runtimeLifetime;
        lock (_operationSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            cancellation = _activeCancellation;
            _runtimeDisposePending = cancellation is not null;
            runtimeLifetime = _runtimeDisposePending ? null : _runtime as IDisposable;
        }

        cancellation?.Cancel();
        runtimeLifetime?.Dispose();
    }

    private async Task RefreshAsync()
    {
        OperationGeneration? operation = TryBeginOperation();
        if (operation is null)
        {
            return;
        }

        IsCancellationRequested = false;
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusTitle = "正在检查安装状态";
        StatusDetail = "正在检查系统与已安装版本。";
        ProgressStatus = "正在检查安装状态…";
        StatusBadge = "检查中";

        try
        {
            InstallerRuntimeReadiness readiness = await _runtime
                .InspectReadinessAsync(operation.Cancellation.Token);
            if (!IsCurrent(operation))
            {
                return;
            }

            ValidateReadiness(readiness);
            CanExecuteMutations = readiness.CanExecute;
            DiagnosticCode = readiness.DiagnosticCode;
            StatusTitle = readiness.StatusTitle;
            StatusDetail = readiness.StatusDetail;
            DisplayVersion = readiness.DisplayVersion;
            Capabilities = readiness.Capabilities.ToArray();
            ApplyProductState(readiness);
            StatusBadge = readiness.CanExecute
                ? readiness.ProductState switch
                {
                    InstallerProductState.Available => "未安装",
                    InstallerProductState.Installed => "已安装",
                    InstallerProductState.RecoveryRequired => "待恢复",
                    _ => throw new InstallerProtocolException("installer.runtime.readiness_invalid"),
                }
                : "暂不可用";
            ProgressStatus = readiness.CanExecute
                ? "准备就绪。"
                : "请查看上方提示。";
            ProgressValue = 0;
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            SetCancelledIfCurrent(operation);
        }
        catch (InstallerProtocolException exception)
        {
            SetInspectionFailureIfCurrent(operation, exception.DiagnosticCode);
        }
        catch (Exception exception)
            when (InstallerPresentationExceptionPolicy.IsRecoverable(exception))
        {
            SetInspectionFailureIfCurrent(operation, "installer.runtime.inspection_failed");
        }
        finally
        {
            CompleteOperation(operation);
        }
    }

    private Task ExecutePrimaryOperationAsync() => ExecuteOperationAsync(_primaryOperation);

    private Task ExecuteSecondaryOperationAsync() => ExecuteOperationAsync(_secondaryOperation);

    private async Task ExecuteOperationAsync(InstallerOperation? requestedOperation, bool ownerTransfer = false, bool retiredUninstall = false)
    {
        IInstallerOwnerTransferRuntime? transfer = ownerTransfer ? _runtime as IInstallerOwnerTransferRuntime : null;
        IInstallerRetiredUninstallRuntime? retired = retiredUninstall ? _runtime as IInstallerRetiredUninstallRuntime : null;
        if (ownerTransfer ? transfer?.SupportsOwnerTransfer != true
            : retiredUninstall ? retired?.SupportsRetiredUninstall != true : !CanExecuteMutations || requestedOperation is null)
        {
            StatusBadge = "已阻止";
            StatusTitle = "暂时无法继续";
            StatusDetail = "当前检查尚未通过，请重新检查安装状态。系统未被更改。";
            DiagnosticCode = requestedOperation is null
                ? "installer.runtime.operation_not_available"
                : "installer.runtime.not_ready";
            return;
        }

        OperationGeneration? generation = TryBeginOperation();
        if (generation is null)
        {
            return;
        }

        IsCancellationRequested = false;
        IsBusy = true;
        InvalidateReadiness();
        IsProgressIndeterminate = ownerTransfer || retiredUninstall;
        ProgressValue = 0;
        ProgressStatus = ownerTransfer ? "正在检查使用账户…" : retiredUninstall ? "等待确认。" : $"正在开始{GetOperationLabel(requestedOperation!.Value)}…";
        StatusTitle = ownerTransfer ? "正在准备切换账户" : retiredUninstall ? "卸载此账户副本" : $"正在{GetOperationLabel(requestedOperation!.Value)}";
        StatusDetail = ownerTransfer ? "完成身份检查后，将在此处请你确认切换。"
            : retiredUninstall ? "移除当前账户已不再使用的 ClashSharp 副本。" : "正在处理应用及所需组件。";
        StatusBadge = "执行中";
        int acceptProgress = 1;
        TaskScheduler confirmationScheduler = SynchronizationContext.Current is null
            ? TaskScheduler.Current : TaskScheduler.FromCurrentSynchronizationContext();
        bool retiredStarted = false;

        try
        {
            var progress = new Progress<InstallerProgress>(value =>
            {
                if (Volatile.Read(ref acceptProgress) == 0 || !IsCurrent(generation) || IsCancellationRequested)
                {
                    return;
                }

                ProgressValue = value.Percent;
                IsProgressIndeterminate = false;
                ProgressStatus = retiredUninstall ? DescribeRetiredUninstallProgress(value.MessageKey)
                    : ProgressMessages.TryGetValue(value.MessageKey, out string? message)
                    ? message
                    : "正在执行当前操作…";
            });

            if (retiredUninstall)
            {
                if (!await ConfirmRetiredUninstallAsync(generation, generation.Cancellation.Token))
                {
                    throw new InstallerUserCancelledException("installer.retired_uninstall.declined");
                }
                generation.Cancellation.Token.ThrowIfCancellationRequested();
                retiredStarted = true;
            }
            InstallerExecutionResult result = ownerTransfer
                ? await transfer!.TransferAndExecuteAsync((confirmation, token) =>
                    Task.Factory.StartNew(() => ConfirmOwnerTransferAsync(confirmation, generation, token),
                        CancellationToken.None, TaskCreationOptions.DenyChildAttach, confirmationScheduler).Unwrap(),
                    progress, generation.Cancellation.Token)
                : retiredUninstall
                    ? await retired!.UninstallRetiredAccountAsync(progress, generation.Cancellation.Token)
                    : await _runtime.ExecuteAsync(requestedOperation!.Value, progress, generation.Cancellation.Token);
            Interlocked.Exchange(ref acceptProgress, 0);
            if (!IsCurrent(generation))
            {
                return;
            }

            ValidateExecutionResult(result, ownerTransfer || retiredUninstall ? null : requestedOperation);
            ApplyExecutionResult(result);
            if (retiredUninstall)
            {
                ApplyRetiredUninstallResult(result);
            }
        }
        catch (OperationCanceledException) when (generation.Cancellation.IsCancellationRequested)
        {
            SetCancelledIfCurrent(generation);
        }
        catch (InstallerUserCancelledException)
        {
            SetCancelledIfCurrent(generation);
        }
        catch (InstallerProtocolException exception)
        {
            SetExecutionFailureIfCurrent(generation, exception.DiagnosticCode);
        }
        catch (Exception exception)
            when (InstallerPresentationExceptionPolicy.IsRecoverable(exception))
        {
            SetExecutionFailureIfCurrent(generation, "installer.runtime.execution_failed");
        }
        finally
        {
            Interlocked.Exchange(ref acceptProgress, 0);
            if (retiredUninstall && IsCurrent(generation) && StatusBadge == "已取消")
            {
                StatusDetail = retiredStarted ? RetiredUninstallRecoveryDetail : "尚未开始卸载此账户副本。";
            }
            else if (retiredUninstall && IsCurrent(generation) && StatusBadge == "失败")
            {
                StatusDetail = DescribeRetiredUninstallFailure(DiagnosticCode);
            }
            CompleteOperation(generation);
        }
    }

    /// <summary>Runs on the captured UI scheduler, owns its cancellation registration, and drains before the generation ends.</summary>
    private async Task<bool> ConfirmOwnerTransferAsync(InstallerOwnerTransferConfirmation confirmation,
        OperationGeneration generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(generation) || _ownerTransferDecision is not null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.confirmation_unavailable");
        }
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ownerTransferDecision = decision;
        OwnerTransferConfirmationTitle = confirmation.IsRecovery ? "继续切换到当前账户？" : "切换到当前账户？";
        IsOwnerTransferConfirmationVisible = true;
        ProgressStatus = "等待确认，尚未开始本次切换。";
        using CancellationTokenRegistration registration = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
        try
        {
            return await decision.Task;
        }
        finally
        {
            _ownerTransferDecision = null;
            if (!_disposed)
            {
                IsOwnerTransferConfirmationVisible = false;
                ProgressStatus = cancellationToken.IsCancellationRequested ? "正在收尾…" : "正在处理账户切换…";
            }
        }
    }

    private OperationGeneration? TryBeginOperation()
    {
        lock (_operationSync)
        {
            if (_disposed || _activeCancellation is not null)
            {
                return null;
            }

            var cancellation = new CancellationTokenSource();
            _activeCancellation = cancellation;
            return new OperationGeneration(++_generation, cancellation);
        }
    }

    private bool IsCurrent(OperationGeneration operation)
    {
        lock (_operationSync)
        {
            return !_disposed
                && operation.Generation == _generation
                && ReferenceEquals(_activeCancellation, operation.Cancellation);
        }
    }

    private void CompleteOperation(OperationGeneration operation)
    {
        bool wasCurrent;
        IDisposable? runtimeLifetime = null;
        lock (_operationSync)
        {
            wasCurrent = ReferenceEquals(_activeCancellation, operation.Cancellation);
            if (wasCurrent)
            {
                _activeCancellation = null;
                if (_runtimeDisposePending)
                {
                    _runtimeDisposePending = false;
                    runtimeLifetime = _runtime as IDisposable;
                }
            }
        }

        operation.Cancellation.Dispose();
        runtimeLifetime?.Dispose();
        if (wasCurrent && !_disposed)
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    private void CancelActiveOperation() => RequestCancellation();

    private void SetCancelledIfCurrent(OperationGeneration operation)
    {
        if (!IsCurrent(operation))
        {
            return;
        }

        InvalidateReadiness();
        StatusBadge = "已取消";
        StatusTitle = "操作已取消";
        StatusDetail = "取消请求已处理。重新检查后可继续未完成的操作。";
        DiagnosticCode = "installer.cancelled";
        ProgressStatus = "操作已取消。";
    }

    private void SetInspectionFailureIfCurrent(OperationGeneration operation, string diagnosticCode)
    {
        if (!IsCurrent(operation))
        {
            return;
        }

        InvalidateReadiness();
        StatusBadge = "已阻止";
        StatusTitle = "无法检查安装状态";
        StatusDetail = "请重新检查；若问题持续，可展开详情查看诊断代码。";
        DiagnosticCode = diagnosticCode;
        ProgressStatus = "就绪检查失败。";
    }

    private void SetExecutionFailureIfCurrent(OperationGeneration operation, string diagnosticCode)
    {
        if (!IsCurrent(operation))
        {
            return;
        }

        InvalidateReadiness();
        StatusBadge = "失败";
        StatusTitle = "操作未完成";
        StatusDetail = "请使用本次操作所用的安装器重新检查，或保留诊断代码以便排查。";
        DiagnosticCode = diagnosticCode;
        ProgressStatus = "需要恢复或诊断。";
    }

    private void ApplyExecutionResult(InstallerExecutionResult result)
    {
        // Every mutation or recovery attempt invalidates the readiness snapshot. A fresh trusted
        // inspection is required before the primary command can be enabled again.
        InvalidateReadiness();
        DiagnosticCode = result.DiagnosticCode;
        (StatusBadge, StatusTitle, StatusDetail, ProgressStatus) = result.Outcome switch
        {
            InstallerExecutionOutcome.Succeeded =>
                ("已完成", "操作已完成", "可以关闭安装器，或重新检查以管理此应用。", "操作完成。"),
            InstallerExecutionOutcome.Blocked =>
                ("已阻止", "暂时无法继续", "当前检查未通过，系统未被更改。请展开详情查看原因。", "操作未开始。"),
            InstallerExecutionOutcome.Cancelled =>
                ("已取消", "操作已取消", GetRecoveryDetail(result), "操作已取消。"),
            InstallerExecutionOutcome.Uncertain =>
                ("需要恢复", "操作状态尚未确认", GetRecoveryDetail(result), "必须重新检查并恢复。"),
            InstallerExecutionOutcome.Failed =>
                ("失败", "操作未完成", GetRecoveryDetail(result), "需要恢复或诊断。"),
            _ =>
                ("失败", "结果协议无效", "运行时返回了未知结果；安装器已保持禁用。", "需要诊断。"),
        };

        if (result.Outcome == InstallerExecutionOutcome.Succeeded)
        {
            ProgressValue = 100;
            if (result.DirectoryCleanupReport is { } cleanup)
            {
                ApplyDirectoryCleanupReport(cleanup);
            }
        }
    }

    private void ApplyDirectoryCleanupReport(InstallerDirectoryCleanupReport cleanup)
    {
        ProgressStatus = "卸载完成。";
        if (!cleanup.HasRetained)
        {
            StatusTitle = "卸载已完成";
            StatusDetail = "自有空目录已清理或已不存在。可以关闭安装器，或重新检查以管理此应用。";
            return;
        }

        StatusTitle = "卸载已完成，部分目录已保留";
        StatusDetail = "以下目录含有其他内容，或无法确认由安装器创建，因此已保留："
            + Environment.NewLine
            + string.Join(Environment.NewLine, cleanup.Entries
                .Where(static entry => entry.Disposition is
                    InstallerDirectoryCleanupDisposition.RetainedNonEmpty
                    or InstallerDirectoryCleanupDisposition.RetainedUnprovenOwnership)
                .Select(static entry => GetDirectoryLabel(entry.Role) + (entry.Disposition ==
                    InstallerDirectoryCleanupDisposition.RetainedNonEmpty
                    ? "：包含其他内容。" : "：无法确认由安装器创建。")))
            + Environment.NewLine + "可以关闭安装器。";
    }

    private static string GetDirectoryLabel(InstallerDirectoryRole role) => role switch
    {
        InstallerDirectoryRole.ProgramFilesProduct => "程序目录（Program Files\\ClashSharp）",
        InstallerDirectoryRole.ProgramDataProduct => "共享数据目录（ProgramData\\ClashSharp）",
        InstallerDirectoryRole.InstallerRoot => "安装记录目录（ProgramData\\ClashSharp\\Installer）",
        InstallerDirectoryRole.InstallerVersion => "安装记录子目录（ProgramData\\ClashSharp\\Installer\\v2）",
        InstallerDirectoryRole.AuthorityRoot => "安装管理目录（ProgramData\\ClashSharp\\InstallerAuthority）",
        InstallerDirectoryRole.AuthorityVersion => "安装管理子目录（ProgramData\\ClashSharp\\InstallerAuthority\\v1）",
        _ => throw new InstallerProtocolException("installer.runtime.result_invalid"),
    };

    private static string GetRecoveryDetail(InstallerExecutionResult result) => result.RecoveryPending
        ? "进度已保留。请使用本次操作所用的安装器重新检查并继续。"
        : "操作尚未完成，请重新检查安装状态。";

    private void SetUnhandledCommandFailure()
    {
        InvalidateReadiness();
        StatusBadge = "失败";
        StatusTitle = "界面命令未完成";
        StatusDetail = "请重新检查后重试，或展开详情查看诊断代码。";
        DiagnosticCode = "installer.presentation.command_failed";
    }

    private void InvalidateReadiness()
    {
        CanExecuteMutations = false;
        _secondaryOperation = null;
        SecondaryActionText = string.Empty;
        HasSecondaryAction = false;
    }

    private void NotifyCommandAvailability()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        PrimaryActionCommand.NotifyCanExecuteChanged();
        SecondaryActionCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OwnerTransferCommand.NotifyCanExecuteChanged();
        RetiredUninstallCommand.NotifyCanExecuteChanged();
    }

    private void NotifyActionVisibility()
    {
        OnPropertyChanged(nameof(IsPrimaryActionVisible));
        OnPropertyChanged(nameof(IsSecondaryActionVisible));
        OnPropertyChanged(nameof(IsCancelActionVisible));
        OnPropertyChanged(nameof(IsOwnerTransferActionVisible));
        OnPropertyChanged(nameof(IsRetiredUninstallActionVisible));
    }

    private void ApplyProductState(InstallerRuntimeReadiness readiness)
    {
        InstallerOperation fallbackOperation = readiness.ProductState switch
        {
            InstallerProductState.Available => InstallerOperation.Install,
            InstallerProductState.Installed => InstallerOperation.Repair,
            InstallerProductState.RecoveryRequired => readiness.RecoveryOperation!.Value,
            _ => throw new InstallerProtocolException(
                "installer.runtime.readiness_invalid"),
        };
        _primaryOperation = readiness.AllowedOperations.Count > 0
            ? readiness.AllowedOperations[0]
            : fallbackOperation;
        _secondaryOperation = readiness.AllowedOperations.Count > 1
            ? readiness.AllowedOperations[1]
            : null;
        PrimaryActionText = readiness.ProductState == InstallerProductState.RecoveryRequired
            ? $"继续{GetOperationLabel(_primaryOperation)}"
            : GetOperationLabel(_primaryOperation);
        SecondaryActionText = _secondaryOperation is { } secondary
            ? GetOperationLabel(secondary)
            : string.Empty;

        HasSecondaryAction = readiness.CanExecute
            && _secondaryOperation is not null;
    }

    private static string GetOperationLabel(InstallerOperation operation) => operation switch
    {
        InstallerOperation.Install => "安装",
        InstallerOperation.Repair => "修复",
        InstallerOperation.Uninstall => "卸载",
        _ => "执行",
    };

    private static void ValidateReadiness(InstallerRuntimeReadiness readiness)
    {
        if (readiness is null
            || !HasValidProductState(readiness)
            || !HasValidAllowedOperations(readiness)
            || !IsValidDiagnosticCode(readiness.DiagnosticCode)
            || !IsValidDisplayText(readiness.StatusTitle, 160)
            || !IsValidDisplayText(readiness.StatusDetail, 1_024)
            || !IsValidDisplayText(readiness.DisplayVersion, 128)
            || readiness.Capabilities is null
            || readiness.Capabilities.Count is < 1 or > 16)
        {
            throw new InstallerProtocolException("installer.runtime.readiness_invalid");
        }

        foreach (InstallerCapabilityStatus? capability in readiness.Capabilities)
        {
            if (capability is null
                || !IsValidDisplayText(capability.Name, 128)
                || !IsValidDisplayText(capability.Detail, 512)
                || (readiness.CanExecute && !capability.IsReady))
            {
                throw new InstallerProtocolException("installer.runtime.readiness_invalid");
            }
        }
    }

    private static bool HasValidProductState(InstallerRuntimeReadiness readiness) =>
        InstallerProductStatePolicy.IsValid(
            readiness.ProductState,
            readiness.RecoveryOperation);

    private static bool HasValidAllowedOperations(InstallerRuntimeReadiness readiness)
    {
        if (readiness.AllowedOperations is null
            || readiness.AllowedOperations.Count > 2
            || readiness.AllowedOperations.Any(static operation => !Enum.IsDefined(operation))
            || readiness.AllowedOperations.Distinct().Count()
                != readiness.AllowedOperations.Count
            || readiness.CanExecute != (readiness.AllowedOperations.Count > 0))
        {
            return false;
        }

        return readiness.ProductState switch
        {
            InstallerProductState.Available =>
                readiness.AllowedOperations.Count == 0
                || readiness.AllowedOperations.SequenceEqual(
                    [InstallerOperation.Install]),
            InstallerProductState.Installed =>
                readiness.AllowedOperations.Count == 0
                || readiness.AllowedOperations.SequenceEqual(
                    [InstallerOperation.Uninstall])
                || readiness.AllowedOperations.SequenceEqual(
                    [InstallerOperation.Repair, InstallerOperation.Uninstall]),
            InstallerProductState.RecoveryRequired =>
                readiness.AllowedOperations.Count == 0
                || readiness.AllowedOperations.SequenceEqual(
                    [readiness.RecoveryOperation!.Value]),
            _ => false,
        };
    }

    private static void ValidateExecutionResult(InstallerExecutionResult result, InstallerOperation? ordinaryOperation)
    {
        if (result is null
            || !Enum.IsDefined(result.Outcome)
            || !IsValidDiagnosticCode(result.DiagnosticCode)
            || (result.LastDurablePhase is { } phase && !Enum.IsDefined(phase))
            || (result.Outcome == InstallerExecutionOutcome.Succeeded
                && (result.RecoveryPending
                    || result.LastDurablePhase != InstallerTransactionPhase.Verified)))
        {
            throw new InstallerProtocolException("installer.runtime.result_invalid");
        }

        if (result.DirectoryCleanupReport is { } cleanup)
        {
            if (result.Outcome != InstallerExecutionOutcome.Succeeded
                || ordinaryOperation != InstallerOperation.Uninstall)
            {
                throw new InstallerProtocolException("installer.runtime.result_invalid");
            }
            cleanup.Validate();
        }
    }

    private static bool IsValidDiagnosticCode(string value) =>
        value is { Length: >= 1 and <= 160 }
        && value.StartsWith("installer.", StringComparison.Ordinal)
        && value.All(static character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_');

    private static bool IsValidDisplayText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private sealed record OperationGeneration(long Generation, CancellationTokenSource Cancellation);
}
