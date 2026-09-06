namespace ClashSharp.Installer.Payloads;

/// <summary>Read-only payload integrity evidence with no installation authority or retained handles.</summary>
/// <param name="PackageVersion">Exact package version from the embedded manifest.</param>
/// <param name="PayloadSha256">Exact primary package digest from the embedded manifest.</param>
/// <param name="FileCount">Number of verified sibling payload files.</param>
/// <param name="TotalBytes">Total length of the verified sibling payload files.</param>
/// <param name="MachineFileCount">Number of verified machine files inside the primary package.</param>
public sealed record InstallerPayloadAuditResult(
    string PackageVersion,
    string PayloadSha256,
    int FileCount,
    long TotalBytes,
    int MachineFileCount);
