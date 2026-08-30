using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Contracts;

/// <summary>Inspects Windows and installed-package state without mutating it.</summary>
public interface IInstallerEnvironment
{
    /// <summary>Captures preflight facts for the exact target user.</summary>
    Task<InstallerEnvironmentSnapshot> InspectAsync(
        InstallerRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Establishes release trust from immutable, already-open payload handles.</summary>
public interface IInstallerReleaseVerifier
{
    /// <summary>
    /// Verifies the signed installer release. Uninstall must remain possible when the package payload is absent.
    /// </summary>
    Task<IInstallerReleaseLease> VerifyAsync(
        InstallerRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Executes repeatable target-user certificate ownership, import, or removal.</summary>
public interface IInstallerCertificateMutation
{
    /// <summary>
    /// Applies the exact release certificate mutation through durable write-ahead ownership state.
    /// </summary>
    Task ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);
}

/// <summary>Executes repeatable per-user package deployment or removal.</summary>
public interface IInstallerPackageMutation
{
    /// <summary>Applies the requested package mutation and verifies its immediate result.</summary>
    Task ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken);
}

/// <summary>Executes repeatable machine-wide service, payload, and ownership changes.</summary>
public interface IInstallerMachineMutation
{
    /// <summary>
    /// Reserves install/repair ownership or durably authorizes owner-checked uninstall removal.
    /// </summary>
    Task<InstallerTransactionSnapshot> PrepareAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Independently verifies the exact target-user package result and commits PackageCommitted.
    /// </summary>
    Task<InstallerTransactionSnapshot> CommitPackageAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken);

    /// <summary>Applies the requested machine mutation and verifies its immediate result.</summary>
    Task<InstallerTransactionSnapshot> ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken);
}

/// <summary>Independently proves the complete requested final state.</summary>
public interface IInstallerFinalVerifier
{
    /// <summary>
    /// Verifies package and machine state and returns the exact helper-committed Verified journal.
    /// </summary>
    Task<InstallerTransactionSnapshot> VerifyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableState,
        CancellationToken cancellationToken);
}
