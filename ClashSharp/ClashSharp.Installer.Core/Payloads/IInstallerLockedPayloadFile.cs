namespace ClashSharp.Installer.Payloads;

/// <summary>
/// Describes one already-open ordinary file whose platform handle remains owned by its release lease.
/// </summary>
public interface IInstallerLockedPayloadFile
{
    /// <summary>Gets the exact manifest entry proven from the open file object.</summary>
    InstallerPayloadFileEntry ManifestEntry { get; }

    /// <summary>
    /// Gets the absolute published path. The path has authority only while the owning lease is alive
    /// and after its revalidation method succeeds.
    /// </summary>
    string FullPath { get; }
}
