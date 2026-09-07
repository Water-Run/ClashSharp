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
                "安装与修复仅支持 Windows 11 或更高版本的原生 x64 客户端系统；未执行任何系统更改。");
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
            "Windows 11+ x64 / 安全卸载",
            inspection.Environment.IsSupported
                ? "已确认 Windows 11+ 客户端、原生 x64 系统与 x64 安装器进程。"
                : removalPathAvailable
                    ? "平台不满足安装要求；仅保留不依赖安装目标版本的安全卸载路径。"
                    : "安装与修复需要 Windows 11+ 原生 x64 客户端。",
            platformAllowsVisibleAction),
        new(
            "签名安装器与内嵌清单",
            "可信 backend 已验证当前 Installer 映像，并绑定严格内嵌发布身份。",
            true),
        new(
            "当前用户包与进程",
            inspection.Environment.InstalledPackageVersion is null
                ? "未发现目标包注册，也未把无关同名进程视为产品实例。"
                : inspection.Environment.IsApplicationRunning
                    ? "目标包身份已确认，但应用仍在运行。"
                    : "目标包身份已确认，未发现其应用进程。",
            !inspection.Environment.IsApplicationRunning),
        new(
            "受保护恢复状态",
            inspection.DurableTransaction is null
                ? "未发现待恢复事务。"
                : "已读取并绑定同一用户、同一发布的待恢复事务。",
            true),
        new(
            "认证提权事务",
            "执行时由同一签名 Installer 的 PID 绑定 Helper 与受保护日志完成最终验证。",
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
