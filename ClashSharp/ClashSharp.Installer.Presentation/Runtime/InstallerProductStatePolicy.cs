using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Runtime;

/// <summary>One of the three product-card states selected from trusted installed facts.</summary>
/// <param name="ProductState">The only visible product state.</param>
/// <param name="RecoveryOperation">The exact operation to resume only for recovery.</param>
public sealed record InstallerProductStateDecision(
    InstallerProductState ProductState,
    InstallerOperation? RecoveryOperation)
{
    /// <summary>Validates the fixed state/operation relationship.</summary>
    public void Validate()
    {
        if (!InstallerProductStatePolicy.IsValid(ProductState, RecoveryOperation))
        {
            throw new InstallerProtocolException(
                "installer.runtime.product_state_invalid");
        }
    }
}

/// <summary>
/// Collapses trusted package and durable-transaction facts into the single-card state machine.
/// </summary>
public static class InstallerProductStatePolicy
{
    internal static bool IsValid(
        InstallerProductState productState,
        InstallerOperation? recoveryOperation) => productState switch
        {
            InstallerProductState.Available or InstallerProductState.Installed =>
                recoveryOperation is null,
            InstallerProductState.RecoveryRequired =>
                recoveryOperation is { } operation && Enum.IsDefined(operation),
            _ => false,
        };

    /// <summary>Selects Available, Installed, or exact durable RecoveryRequired state.</summary>
    /// <param name="installedPackageVersion">Canonical installed MSIX version, when present.</param>
    /// <param name="durableTransaction">Exact protected transaction, when recovery is pending.</param>
    /// <returns>The validated single-card state and optional recovery operation.</returns>
    public static InstallerProductStateDecision Decide(
        string? installedPackageVersion,
        InstallerTransactionSnapshot? durableTransaction)
    {
        if (installedPackageVersion is not null)
        {
            InstallerProtocolValidation.ValidatePackageVersion(installedPackageVersion);
        }

        durableTransaction?.Validate();
        InstallerProductStateDecision decision = durableTransaction is not null
            ? new(
                InstallerProductState.RecoveryRequired,
                durableTransaction.Journal.Operation)
            : installedPackageVersion is null
                ? new(InstallerProductState.Available, null)
                : new(InstallerProductState.Installed, null);
        decision.Validate();
        return decision;
    }
}
