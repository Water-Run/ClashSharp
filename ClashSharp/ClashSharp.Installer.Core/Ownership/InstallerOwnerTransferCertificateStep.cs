namespace ClashSharp.Installer.Ownership;

/// <summary>One copy-before-removal boundary of certificate ownership transfer.</summary>
public enum InstallerOwnerTransferCertificateAction
{
    /// <summary>Copies the exact previous active ledger to its private account archive.</summary>
    PreservePrevious,

    /// <summary>Atomically activates the target ledger, or removes an already archived previous ledger.</summary>
    ActivateNext,

    /// <summary>Removes the duplicate target archive only after observing its exact active copy.</summary>
    ReleaseActivatedArchive,
}

/// <summary>A mutation and its complete required three-slot postcondition.</summary>
/// <param name="Action">The only next mutation authorized by the pure state policy.</param>
/// <param name="After">Exact state that must be reread before the boundary is accepted.</param>
public sealed record InstallerOwnerTransferCertificateStep(
    InstallerOwnerTransferCertificateAction Action,
    InstallerOwnerTransferCertificateState After);
