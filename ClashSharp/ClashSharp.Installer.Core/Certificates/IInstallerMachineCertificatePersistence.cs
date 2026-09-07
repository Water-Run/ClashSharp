using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Certificates;

/// <summary>
/// Access to one fixed machine-certificate document in a pinned SYSTEM/Administrators-only root.
/// The authenticated helper retains global installer exclusion throughout mutation and reconciliation.
/// </summary>
public interface IInstallerMachineCertificatePersistence
{
    /// <summary>Returns bounded owned bytes, or confirmed absence under the verified root.</summary>
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Atomically writes the fixed document with private ACLs and durable flushes.</summary>
    Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Deletes only the fixed ordinary private document.</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>Read-only all-user and provisioned MSIX references to the exact publisher.</summary>
public interface IInstallerMachineCertificateReferences
{
    /// <summary>
    /// Returns true when any registered, staged, or provisioned package may need this publisher's
    /// trust. An incomplete or failed inventory must throw; it must never be reported as absence.
    /// </summary>
    Task<bool> HasReferencesAsync(InstallerReleaseManifest manifest, CancellationToken cancellationToken);
}
