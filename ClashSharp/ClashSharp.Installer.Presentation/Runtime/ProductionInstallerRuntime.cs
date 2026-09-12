using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Runtime;

/// <summary>
/// Maps a trusted platform backend into the fixed single-product presentation state without ever
/// constructing a target SID, release hash, package identity, or privileged command.
/// </summary>
public sealed class ProductionInstallerRuntime : IInstallerRuntime, IInstallerOwnerTransferRuntime, IInstallerRetiredUninstallRuntime, IDisposable
{
    private readonly IInstallerRuntimeBackend _backend;
    private bool _disposed;

    /// <summary>Creates a production presentation runtime over one trusted platform backend.</summary>
    /// <param name="backend">Backend that owns exact request construction and mutation authority.</param>
    public ProductionInstallerRuntime(IInstallerRuntimeBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    /// <inheritdoc />
    public async Task<InstallerRuntimeReadiness> InspectReadinessAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        InstallerRuntimeInspection inspection = await _backend
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InstallerProtocolException(
                "installer.runtime.inspection_result_missing");
        inspection.Validate();
        InstallerProductStateDecision product = InstallerProductStatePolicy.Decide(
            inspection.Environment.InstalledPackageVersion,
            inspection.DurableTransaction);
        bool removalPathAvailable = product.ProductState == InstallerProductState.Installed
            || product.RecoveryOperation == InstallerOperation.Uninstall;
        IReadOnlyList<InstallerOperation> allowedOperations = SelectAllowedOperations(
            inspection,
            product);
        bool platformAllowsVisibleAction = inspection.Environment.IsSupported
            || removalPathAvailable;
        bool canExecute = allowedOperations.Count > 0;
        string diagnosticCode = inspection.Environment.IsApplicationRunning
            ? "installer.application_running"
            : !platformAllowsVisibleAction
                ? inspection.Environment.BlockingDiagnosticCode!
                : product.ProductState == InstallerProductState.RecoveryRequired
                    ? "installer.runtime.recovery_required"
                    : "installer.runtime.ready";
        (string title, string detail) = Describe(
            inspection,
            product,
            canExecute,
            removalPathAvailable);

        return new InstallerRuntimeReadiness(
            canExecute,
            diagnosticCode,
            title,
            detail,
            inspection.ReleaseVersion,
            product.ProductState,
            product.RecoveryOperation,
            allowedOperations,
            BuildCapabilities(
                inspection,
                platformAllowsVisibleAction,
                removalPathAvailable));
    }

    /// <inheritdoc />
    public Task<InstallerExecutionResult> ExecuteAsync(
        InstallerOperation operation,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(progress);
        if (!Enum.IsDefined(operation))
        {
            throw new InstallerProtocolException(
                "installer.request.operation_invalid");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _backend.ExecuteAsync(operation, progress, cancellationToken);
    }

    /// <inheritdoc />
    public bool SupportsOwnerTransfer => _backend is IInstallerOwnerTransferRuntimeBackend { SupportsOwnerTransfer: true };

    /// <inheritdoc />
    public bool SupportsRetiredUninstall => _backend is IInstallerRetiredUninstallRuntimeBackend { SupportsRetiredUninstall: true };

    /// <inheritdoc />
    public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (_backend is not IInstallerRetiredUninstallRuntimeBackend { SupportsRetiredUninstall: true } retired)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.unavailable");
        }
        return retired.UninstallRetiredAccountAsync(progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task<InstallerExecutionResult> TransferAndExecuteAsync(
        Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (_backend is not IInstallerOwnerTransferRuntimeBackend { SupportsOwnerTransfer: true } transfer)
        {
            throw new InstallerProtocolException("installer.owner_transfer.unavailable");
        }
        return transfer.TransferAndExecuteAsync((offer, token) =>
        {
            offer.Validate();
            return confirm(new(offer.IsRecovery), token);
        }, progress, cancellationToken);
    }

    /// <summary>Stops future calls and releases the trusted backend composition.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _backend.Dispose();
        _disposed = true;
    }

    private static (string Title, string Detail) Describe(
        InstallerRuntimeInspection inspection,
        InstallerProductStateDecision product,
        bool canExecute,
        bool removalPathAvailable)
    {
        if (inspection.Environment.IsApplicationRunning)
        {
            return (
                "请先关闭 ClashSharp",
                "关闭正在运行的应用后，选择“重新检查”继续。");
        }

        if (!canExecute)
        {
            return (
                "当前系统不支持此操作",
                "安装与修复需要 Windows 11+ 或 Windows Server 2025+ 桌面体验，且系统与进程均为 x64；不支持 Server Core。");
        }

        if (!inspection.Environment.IsSupported && removalPathAvailable)
        {
            return (
                "仅安全卸载可用",
                "此电脑不满足安装要求，仍可移除已安装的 ClashSharp。");
        }

        return product.ProductState switch
        {
            InstallerProductState.Available => (
                "可以安装",
                "为当前账户安装 ClashSharp，并配置所需的系统组件。"),
            InstallerProductState.Installed => (
                "已安装",
                "可修复当前安装，或从此电脑移除 ClashSharp。"),
            InstallerProductState.RecoveryRequired => (
                "需要继续未完成的操作",
                "上次操作尚未完成。请使用此安装器继续，完成后再执行其他操作。"),
            _ => throw new InstallerProtocolException(
                "installer.runtime.product_state_invalid"),
        };
    }

    private static IReadOnlyList<InstallerCapabilityStatus> BuildCapabilities(
        InstallerRuntimeInspection inspection,
        bool platformAllowsVisibleAction,
        bool removalPathAvailable) =>
    [
        new(
            "系统要求",
            inspection.Environment.IsSupported
                ? "当前 Windows 桌面环境和 x64 系统符合安装要求。"
                : removalPathAvailable
                    ? "当前系统不符合安装要求，仍可卸载已安装的 ClashSharp。"
                    : "安装与修复需要 Windows 11+ 或 Windows Server 2025+ 桌面体验，且系统与进程均为 x64。",
            platformAllowsVisibleAction),
        new(
            "安装器签名",
            "安装器签名已通过验证，版本信息与此安装包一致。",
            true),
        new(
            "安装状态",
            inspection.Environment.InstalledPackageVersion is null
                ? "当前账户尚未安装 ClashSharp。"
                : inspection.Environment.IsApplicationRunning
                    ? "ClashSharp 正在运行，请关闭应用后继续。"
                    : "当前账户已安装 ClashSharp，应用已关闭。",
            !inspection.Environment.IsApplicationRunning),
        new(
            "未完成的操作",
            inspection.DurableTransaction is null
                ? "没有需要继续的安装或维护操作。"
                : "发现上次未完成的操作，可使用此安装包继续。",
            true),
        new(
            "所需权限",
            "配置系统组件时需要管理员权限；若 Windows 显示提示，请确认后继续。",
            true),
    ];

    private static IReadOnlyList<InstallerOperation> SelectAllowedOperations(
        InstallerRuntimeInspection inspection,
        InstallerProductStateDecision product)
    {
        if (inspection.Environment.IsApplicationRunning)
        {
            return [];
        }

        return product.ProductState switch
        {
            InstallerProductState.Available when inspection.Environment.IsSupported =>
                [InstallerOperation.Install],
            InstallerProductState.Installed when inspection.Environment.IsSupported =>
                [InstallerOperation.Repair, InstallerOperation.Uninstall],
            InstallerProductState.Installed =>
                [InstallerOperation.Uninstall],
            InstallerProductState.RecoveryRequired
                when inspection.Environment.IsSupported
                    || product.RecoveryOperation == InstallerOperation.Uninstall =>
                [product.RecoveryOperation!.Value],
            InstallerProductState.Available or InstallerProductState.RecoveryRequired => [],
            _ => throw new InstallerProtocolException(
                "installer.runtime.product_state_invalid"),
        };
    }
}
