using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Runtime;

/// <summary>
/// Owns security-sensitive request construction and bridges the WPF shell to Windows execution.
/// The presentation layer never supplies a SID, release hash, or package identity.
/// </summary>
public interface IInstallerRuntime
{
    /// <summary>Returns immutable facts that decide whether a user action may be enabled.</summary>
    Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken);

    /// <summary>Executes the exact release and target resolved by the trusted runtime.</summary>
    Task<InstallerExecutionResult> ExecuteAsync(
        InstallerOperation operation,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>One visible prerequisite in the migration/readiness report.</summary>
/// <param name="Name">Localized capability name.</param>
/// <param name="Detail">Concise readiness detail.</param>
/// <param name="IsReady">Whether this prerequisite is independently proven.</param>
public sealed record InstallerCapabilityStatus(string Name, string Detail, bool IsReady)
{
    /// <summary>Gets an accessible state string for the capability.</summary>
    public string StateText => IsReady ? "已就绪" : "未接通";
}

/// <summary>The only product-card states accepted from trusted installed-state inspection.</summary>
public enum InstallerProductState
{
    /// <summary>No target-user package is installed; the only mutation is Install.</summary>
    Available,

    /// <summary>The target-user package is installed; Repair and Uninstall are available.</summary>
    Installed,

    /// <summary>An exact durable transaction must resume before ordinary maintenance.</summary>
    RecoveryRequired,
}

/// <summary>Fail-closed snapshot consumed by the WPF shell.</summary>
/// <param name="CanExecute">Whether mutation controls may be enabled.</param>
/// <param name="DiagnosticCode">Stable diagnostic code suitable for support workflows.</param>
/// <param name="StatusTitle">Localized high-level status.</param>
/// <param name="StatusDetail">Localized explanation that does not expose raw exceptions.</param>
/// <param name="DisplayVersion">Trusted release version, or a migration placeholder.</param>
/// <param name="ProductState">One of the three fixed product-card states.</param>
/// <param name="RecoveryOperation">Exact operation only when a durable recovery is required.</param>
/// <param name="Capabilities">Independently evaluated prerequisites.</param>
public sealed record InstallerRuntimeReadiness(
    bool CanExecute,
    string DiagnosticCode,
    string StatusTitle,
    string StatusDetail,
    string DisplayVersion,
    InstallerProductState ProductState,
    InstallerOperation? RecoveryOperation,
    IReadOnlyList<InstallerCapabilityStatus> Capabilities);
