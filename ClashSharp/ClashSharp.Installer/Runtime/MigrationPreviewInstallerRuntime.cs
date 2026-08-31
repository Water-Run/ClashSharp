using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Windows.Platform;

namespace ClashSharp.Installer.Runtime;

/// <summary>
/// Exposes the WPF shell while the production composition gate awaits signed Windows evidence.
/// It never mutates the machine and therefore remains safe if a command is invoked indirectly.
/// </summary>
public sealed class MigrationPreviewInstallerRuntime : IInstallerRuntime
{
    private readonly IInstallerPlatformProbe _platformProbe;

    /// <summary>Creates a fail-closed preview backed by the native Windows platform probe.</summary>
    public MigrationPreviewInstallerRuntime()
        : this(new WindowsInstallerPlatformProbe())
    {
    }

    /// <summary>Creates a preview over an explicit, testable platform probe.</summary>
    /// <param name="platformProbe">Native platform fact source.</param>
    public MigrationPreviewInstallerRuntime(IInstallerPlatformProbe platformProbe)
    {
        ArgumentNullException.ThrowIfNull(platformProbe);
        _platformProbe = platformProbe;
    }

    /// <inheritdoc />
    public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallerPlatformFacts facts = _platformProbe.Inspect(cancellationToken);
        InstallerPlatformAssessment platform = InstallerPlatformPolicy.Evaluate(facts);
        return Task.FromResult(CreateReadiness(platform));
    }

    /// <inheritdoc />
    public Task<InstallerExecutionResult> ExecuteAsync(
        InstallerOperation operation,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(progress);
        InstallerPlatformAssessment platform = InstallerPlatformPolicy.Evaluate(
            _platformProbe.Inspect(cancellationToken));
        return Task.FromResult(new InstallerExecutionResult(
            InstallerExecutionOutcome.Blocked,
            platform.IsSupported
                ? "installer.runtime.production_gate_closed"
                : platform.DiagnosticCode,
            LastDurablePhase: null,
            RecoveryPending: false));
    }

    private static InstallerRuntimeReadiness CreateReadiness(
        InstallerPlatformAssessment platform)
    {
        string platformDetail = platform.IsSupported
            ? "已确认 Windows 11+ 客户端、原生 x64 系统与 x64 安装器进程。"
            : "仅支持 Windows 11 或更高版本的原生 x64 客户端系统。";

        return new InstallerRuntimeReadiness(
            CanExecute: false,
            DiagnosticCode: platform.IsSupported
                ? "installer.runtime.production_gate_closed"
                : platform.DiagnosticCode,
            StatusTitle: platform.IsSupported ? "发布验证尚未完成" : "当前系统不受支持",
            StatusDetail: platform.IsSupported
                ? "生产 parent/helper/runtime 已完成组合；默认发布门保持关闭，直至同一签名候选的 Windows VM 证据闭环。"
                : "安装器已在任何发布载荷或系统变更之前阻止执行。需要 Windows 11+ x64 客户端。",
            DisplayVersion: "等待可信发布载荷",
            ProductState: InstallerProductState.Available,
            RecoveryOperation: null,
            AllowedOperations: [],
            Capabilities:
            [
                new("Windows 11+ x64", platformDetail, platform.IsSupported),
                new("发布签名与固定清单", "内嵌清单、包内机器文件哈希与候选生成链已实现，尚未完成正式签名发布验证。", false),
                new("MSIX 用户包事务", "当前用户适配器与 production runtime 已组合；默认发布门关闭，仍待 Windows VM 验证。", false),
                new("系统服务与证书事务", "helper、认证 pipe、authority、SCM/payload 与目标用户证书事务已组合；默认 parent/helper authority 均禁用，仍待签名 VM 证据。", false),
                new("最终状态独立验证", "需在 Windows VM 中完成故障注入与恢复证明。", false),
            ]);
    }
}
