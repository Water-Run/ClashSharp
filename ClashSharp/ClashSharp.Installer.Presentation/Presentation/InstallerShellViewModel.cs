using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation;

/// <summary>Coordinates accessible UI state without acquiring package or machine authority.</summary>
public sealed class InstallerShellViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> ProgressMessages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["installer.progress.preflight"] = "正在检查系统与已安装版本…",
            ["installer.progress.prepared"] = "恢复信息已经安全写入。",
            ["installer.progress.machine_prepare"] = "正在预留兼容的系统服务所有权…",
            ["installer.progress.certificate"] = "正在核验并配置用户证书…",
            ["installer.progress.package"] = "正在提交用户应用包…",
            ["installer.progress.machine"] = "正在配置系统服务…",
            ["installer.progress.machine_remove_authorize"] = "正在核验并持久化系统组件移除授权…",
            ["installer.progress.machine_remove"] = "正在移除系统级组件…",
            ["installer.progress.package_remove"] = "正在移除用户应用包…",
            ["installer.progress.certificate_remove"] = "正在安全释放安装器拥有的证书…",
            ["installer.progress.verifying"] = "正在独立验证最终状态…",
            ["installer.progress.completed"] = "最终状态已验证。",
        };

    private readonly IInstallerRuntime _runtime;
    private readonly object _operationSync = new();
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;
    private bool _isBusy;
    private bool _canExecuteMutations;
    private bool _isProgressIndeterminate;
    private int _progressValue;
    private string _statusTitle = "正在准备";
    private string _statusDetail = "正在读取可信安装器运行时的就绪状态。";
    private string _statusBadge = "检查中";
    private string _displayVersion = "—";
    private string _diagnosticCode = "installer.runtime.not_inspected";
    private string _progressStatus = "等待就绪检查";
    private string _primaryActionText = "安装";
    private string _secondaryActionText = string.Empty;
    private bool _hasSecondaryAction;
    private InstallerOperation _primaryOperation = InstallerOperation.Install;
    private InstallerOperation? _secondaryOperation;
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
        CancelCommand = new DelegateCommand(CancelActiveOperation, () => IsBusy);
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
    public bool IsCancelActionVisible => IsBusy;

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

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_operationSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            cancellation = _activeCancellation;
        }

        cancellation?.Cancel();
    }

    private async Task RefreshAsync()
    {
        OperationGeneration? operation = TryBeginOperation();
        if (operation is null)
        {
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressStatus = "正在验证安装器运行时…";
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
            StatusBadge = readiness.CanExecute ? "已验证" : "审查中";
            ProgressStatus = readiness.CanExecute
                ? "所有执行前提均已验证。"
                : "尚未获得变更系统的授权。";
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

    private async Task ExecuteOperationAsync(InstallerOperation? requestedOperation)
    {
        if (!CanExecuteMutations || requestedOperation is null)
        {
            StatusBadge = "已阻止";
            StatusTitle = "执行器尚未就绪";
            StatusDetail = "就绪端口未证明全部前提，安装器未对系统进行任何更改。";
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

        IsBusy = true;
        InvalidateReadiness();
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressStatus = $"正在开始{GetOperationLabel(requestedOperation.Value)}…";
        StatusBadge = "执行中";
        int acceptProgress = 1;

        try
        {
            var progress = new Progress<InstallerProgress>(value =>
            {
                if (Volatile.Read(ref acceptProgress) == 0 || !IsCurrent(generation))
                {
                    return;
                }

                ProgressValue = value.Percent;
                ProgressStatus = ProgressMessages.TryGetValue(value.MessageKey, out string? message)
                    ? message
                    : "正在执行已验证的安装步骤…";
            });

            InstallerExecutionResult result = await _runtime.ExecuteAsync(
                requestedOperation.Value,
                progress,
                generation.Cancellation.Token);
            Interlocked.Exchange(ref acceptProgress, 0);
            if (!IsCurrent(generation))
            {
                return;
            }

            ValidateExecutionResult(result);
            ApplyExecutionResult(result);
        }
        catch (OperationCanceledException) when (generation.Cancellation.IsCancellationRequested)
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
            CompleteOperation(generation);
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
        lock (_operationSync)
        {
            wasCurrent = ReferenceEquals(_activeCancellation, operation.Cancellation);
            if (wasCurrent)
            {
                _activeCancellation = null;
            }
        }

        operation.Cancellation.Dispose();
        if (wasCurrent && !_disposed)
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    private void CancelActiveOperation()
    {
        CancellationTokenSource? cancellation;
        lock (_operationSync)
        {
            cancellation = _activeCancellation;
        }

        cancellation?.Cancel();
    }

    private void SetCancelledIfCurrent(OperationGeneration operation)
    {
        if (!IsCurrent(operation))
        {
            return;
        }

        InvalidateReadiness();
        StatusBadge = "已取消";
        StatusTitle = "操作已取消";
        StatusDetail = "取消请求已被处理；如有持久事务，可信运行时将在下次启动时要求恢复。";
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
        StatusTitle = "无法验证执行环境";
        StatusDetail = "安装器无法证明当前环境安全可用，因此未启用任何系统更改。";
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
        StatusDetail = "安装事务未能验证目标状态。请保留诊断代码，并在恢复同一发布后重试。";
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
                ("已完成", "安装状态已验证", "请求的最终状态已由独立验证器确认。", "操作完成。"),
            InstallerExecutionOutcome.Blocked =>
                ("已阻止", "安全检查阻止了操作", "安装器未能证明全部执行前提，系统未被更改。", "操作未开始。"),
            InstallerExecutionOutcome.Cancelled =>
                ("已取消", "操作已取消", GetRecoveryDetail(result), "操作已取消。"),
            InstallerExecutionOutcome.Uncertain =>
                ("需要恢复", "特权操作状态尚未确认", GetRecoveryDetail(result), "必须重新检查并恢复。"),
            InstallerExecutionOutcome.Failed =>
                ("失败", "操作未完成", GetRecoveryDetail(result), "需要恢复或诊断。"),
            _ =>
                ("失败", "结果协议无效", "运行时返回了未知结果；安装器已保持禁用。", "需要诊断。"),
        };

        if (result.Outcome == InstallerExecutionOutcome.Succeeded)
        {
            ProgressValue = 100;
        }
    }

    private static string GetRecoveryDetail(InstallerExecutionResult result) => result.RecoveryPending
        ? "已保留精确发布事务；请使用同一安装器发布恢复，勿改用不同载荷。"
        : "没有待恢复的持久事务，系统未达到请求的已验证状态。";

    private void SetUnhandledCommandFailure()
    {
        InvalidateReadiness();
        StatusBadge = "失败";
        StatusTitle = "界面命令未完成";
        StatusDetail = "命令在进入可信执行器前意外终止，未显示原始异常内容。";
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
    }

    private void NotifyActionVisibility()
    {
        OnPropertyChanged(nameof(IsPrimaryActionVisible));
        OnPropertyChanged(nameof(IsSecondaryActionVisible));
        OnPropertyChanged(nameof(IsCancelActionVisible));
    }

    private void ApplyProductState(InstallerRuntimeReadiness readiness)
    {
        switch (readiness.ProductState)
        {
            case InstallerProductState.Available:
                _primaryOperation = InstallerOperation.Install;
                _secondaryOperation = null;
                PrimaryActionText = "安装";
                SecondaryActionText = string.Empty;
                break;
            case InstallerProductState.Installed:
                _primaryOperation = InstallerOperation.Repair;
                _secondaryOperation = InstallerOperation.Uninstall;
                PrimaryActionText = "修复";
                SecondaryActionText = "卸载";
                break;
            case InstallerProductState.RecoveryRequired:
                _primaryOperation = readiness.RecoveryOperation!.Value;
                _secondaryOperation = null;
                PrimaryActionText = $"继续{GetOperationLabel(_primaryOperation)}";
                SecondaryActionText = string.Empty;
                break;
            default:
                throw new InstallerProtocolException("installer.runtime.readiness_invalid");
        }

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

    private static void ValidateExecutionResult(InstallerExecutionResult result)
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
