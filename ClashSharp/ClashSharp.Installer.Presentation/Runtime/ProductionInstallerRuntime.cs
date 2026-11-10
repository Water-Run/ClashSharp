using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Runtime;

/// <summary>
/// Maps a trusted platform backend into the fixed single-product presentation state without ever
/// constructing a target SID, release hash, package identity, or privileged command.
/// </summary>
public sealed class ProductionInstallerRuntime : IInstallerRuntime, IInstallerOwnerTransferRuntime, IInstallerRetiredUninstallRuntime,
    IInstallerOriginalRestorationRuntime, IInstallerPreferencesRuntime, IInstallerApplicationLaunchRuntime, IDisposable
{
    private readonly IInstallerRuntimeBackend _backend;
    private bool _disposed;

    /// <summary>Creates a production presentation runtime over one trusted platform backend.</summary>
    /// <param name="backend">Backend that owns exact request construction and mutation authority.</param>
    /// <param name="text">Localized text shared with the window; Simplified Chinese when omitted.</param>
    public ProductionInstallerRuntime(IInstallerRuntimeBackend backend, Presentation.InstallerText? text = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _text = text ?? new Presentation.InstallerText();
    }

    private readonly Presentation.InstallerText _text;

    private string T(string source) => _text[source];

    private string F(string source, params object?[] arguments) => _text.Format(source, arguments);

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
                removalPathAvailable))
        {
            PendingUserPreferences = inspection.PendingUserPreferences,
            IsUpgrade = canExecute && product.ProductState == InstallerProductState.Installed
                && allowedOperations.Contains(InstallerOperation.Repair) && IsNewerRelease(inspection),
            CanRestoreOriginal = canExecute && SupportsOriginalRestoration
                && inspection.DurableTransaction?.Journal is
                {
                    InstallTransparentProxyService: true,
                    Operation: InstallerOperation.Repair, AllowReassociation: false,
                    Phase: InstallerTransactionPhase.Prepared or InstallerTransactionPhase.MachineReserved
                },
        };
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
        // Platform inspection and lock acquisition can block before the first asynchronous await.
        // Keep the caller's dispatcher available for progress, cancellation, and window messages.
        return Task.Run(() => _backend.ExecuteAsync(operation, progress, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public bool SupportsOwnerTransfer => _backend is IInstallerOwnerTransferRuntimeBackend { SupportsOwnerTransfer: true };

    /// <inheritdoc />
    public bool SupportsRetiredUninstall => _backend is IInstallerRetiredUninstallRuntimeBackend { SupportsRetiredUninstall: true };

    /// <inheritdoc />
    public bool SupportsOriginalRestoration => _backend is IInstallerOriginalRestorationRuntimeBackend { SupportsOriginalRestoration: true };

    /// <inheritdoc />
    public bool SupportsApplicationLaunch => _backend is IInstallerApplicationLaunchBackend { SupportsApplicationLaunch: true };

    /// <inheritdoc />
    public Task LaunchApplicationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is not IInstallerApplicationLaunchBackend { SupportsApplicationLaunch: true } launcher)
        {
            throw new InstallerProtocolException("installer.launch.unavailable");
        }
        return Task.Run(() => launcher.LaunchApplicationAsync(cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public bool SupportsInstallPreferences => _backend is IInstallerPreferencesRuntimeBackend { SupportsInstallPreferences: true };

    /// <inheritdoc />
    public Task<InstallerExecutionResult> InstallWithPreferencesAsync(InstallerPreferences preferences,
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(progress);
        preferences.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (_backend is not IInstallerPreferencesRuntimeBackend { SupportsInstallPreferences: true } backend)
        {
            throw new InstallerProtocolException("installer.preferences.unavailable");
        }
        return Task.Run(() => backend.InstallWithPreferencesAsync(preferences, progress, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<InstallerExecutionResult> TransferWithPreferencesAsync(InstallerPreferences preferences,
        Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(progress);
        preferences.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (_backend is not IInstallerOwnerTransferRuntimeBackend { SupportsOwnerTransfer: true }
            || _backend is not IInstallerPreferencesRuntimeBackend { SupportsInstallPreferences: true } backend)
        {
            throw new InstallerProtocolException("installer.owner_transfer.unavailable");
        }
        return Task.Run(() => backend.TransferWithPreferencesAsync(preferences, (offer, token) =>
        {
            offer.Validate();
            return confirm(new(offer.IsRecovery), token);
        }, progress, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<InstallerExecutionResult> CompletePreferencesAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (_backend is not IInstallerPreferencesRuntimeBackend { SupportsInstallPreferences: true } backend)
        {
            throw new InstallerProtocolException("installer.preferences.unavailable");
        }
        return Task.Run(() => backend.CompletePreferencesAsync(progress, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<InstallerExecutionResult> RestoreOriginalAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (_backend is not IInstallerOriginalRestorationRuntimeBackend { SupportsOriginalRestoration: true } recovery)
        {
            throw new InstallerProtocolException("installer.recovery.capability_unavailable");
        }
        return Task.Run(() => recovery.RestoreOriginalAsync(progress, cancellationToken), cancellationToken);
    }

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
        return Task.Run(() => retired.UninstallRetiredAccountAsync(progress, cancellationToken), cancellationToken);
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
        return Task.Run(() => transfer.TransferAndExecuteAsync((offer, token) =>
        {
            offer.Validate();
            return confirm(new(offer.IsRecovery), token);
        }, progress, cancellationToken), cancellationToken);
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

    private (string Title, string Detail) Describe(
        InstallerRuntimeInspection inspection,
        InstallerProductStateDecision product,
        bool canExecute,
        bool removalPathAvailable)
    {
        if (inspection.Environment.IsApplicationRunning)
        {
            return (
                T("请先关闭 Clash#"),
                T("关闭正在运行的应用后，选择“重新检查”继续。"));
        }

        if (!canExecute)
        {
            InstallerPlatformGuidance guidance = InstallerPlatformGuidance.For(inspection.Environment.BlockingDiagnosticCode, _text);
            return (guidance.Title, guidance.Detail);
        }

        if (!inspection.Environment.IsSupported && removalPathAvailable)
        {
            return (
                T("仅安全卸载可用"),
                F("{0}仍可在此卸载已安装的 Clash#。",
                    InstallerPlatformGuidance.For(inspection.Environment.BlockingDiagnosticCode, _text).Requirement));
        }
        if (inspection.PendingUserPreferences && product.ProductState == InstallerProductState.Installed)
        {
            return (T("安装选项待完成"), T("应用已经安装。可选择“完成安装选项”继续设置快捷入口和启动选项。"));
        }

        return product.ProductState switch
        {
            InstallerProductState.Available => (
                T("可以安装"),
                T("为当前账户安装 Clash#，并配置所需的系统组件。")),
            InstallerProductState.Installed when IsNewerRelease(inspection) => (
                T("可以更新"),
                F("将 Clash# 从 {0} 更新到 {1}。现有设置和数据将保留。", inspection.Environment.InstalledPackageVersion, inspection.ReleaseVersion)),
            InstallerProductState.Installed when IsOlderRelease(inspection) => (
                T("已安装更新的版本"),
                F("此电脑已安装 Clash# {0}，比此安装包的 {1} 更新。安装器不会降级；如需修复，请使用 {0} 或更新版本的安装包。也可以在此卸载。",
                    inspection.Environment.InstalledPackageVersion, inspection.ReleaseVersion)),
            InstallerProductState.Installed => (
                T("已安装"),
                T("可修复当前安装，或从此电脑移除 Clash#。")),
            InstallerProductState.RecoveryRequired when inspection.DurableTransaction?.Journal.Phase == InstallerTransactionPhase.OriginalRestored => (
                T("原安装恢复待收尾"), T("原安装已经恢复。请选择“继续”核验结果并完成收尾。")),
            InstallerProductState.RecoveryRequired => (
                T("需要继续未完成的操作"),
                T("上次操作尚未完成。请使用此安装器继续，完成后再执行其他操作。")),
            _ => throw new InstallerProtocolException(
                "installer.runtime.product_state_invalid"),
        };
    }

    private static bool IsNewerRelease(InstallerRuntimeInspection inspection) =>
        Version.TryParse(inspection.Environment.InstalledPackageVersion, out Version? installed)
        && Version.TryParse(inspection.ReleaseVersion, out Version? release) && release > installed;

    // The coordinator rejects every non-removal operation over a newer installed package, so the
    // presentation must not offer one; removal stays available for recovery.
    private static bool IsOlderRelease(InstallerRuntimeInspection inspection) =>
        Version.TryParse(inspection.Environment.InstalledPackageVersion, out Version? installed)
        && Version.TryParse(inspection.ReleaseVersion, out Version? release) && release < installed;

    private IReadOnlyList<InstallerCapabilityStatus> BuildCapabilities(
        InstallerRuntimeInspection inspection,
        bool platformAllowsVisibleAction,
        bool removalPathAvailable) =>
    [
        new(
            T("系统要求"),
            inspection.Environment.IsSupported
                ? T("当前 Windows 桌面环境和 x64 系统符合安装要求。")
                : removalPathAvailable
                    ? T("当前系统不符合安装要求，仍可卸载已安装的 Clash#。")
                    : InstallerPlatformGuidance.For(inspection.Environment.BlockingDiagnosticCode, _text).Requirement,
            platformAllowsVisibleAction),
        new(
            T("安装器签名"),
            T("安装器签名已通过验证，版本信息与此安装包一致。"),
            true),
        new(
            T("安装状态"),
            inspection.Environment.InstalledPackageVersion is null
                ? T("当前账户尚未安装 Clash#。")
                : inspection.Environment.IsApplicationRunning
                    ? T("Clash# 正在运行，请关闭应用后继续。")
                    : IsOlderRelease(inspection)
                        ? F("当前账户已安装更新的 Clash# {0}，此安装包不能降级。", inspection.Environment.InstalledPackageVersion)
                        : T("当前账户已安装 Clash#，应用已关闭。"),
            !inspection.Environment.IsApplicationRunning),
        new(
            T("未完成的操作"),
            inspection.DurableTransaction is null
                ? T("没有需要继续的安装或维护操作。")
                : T("发现上次未完成的操作，可使用此安装包继续。"),
            true),
        new(
            T("所需权限"),
            T("配置系统组件时需要管理员权限；若 Windows 显示提示，请确认后继续。"),
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
            InstallerProductState.Installed when inspection.Environment.IsSupported && !IsOlderRelease(inspection) =>
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
