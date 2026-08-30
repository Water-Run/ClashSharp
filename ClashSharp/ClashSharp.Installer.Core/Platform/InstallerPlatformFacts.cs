namespace ClashSharp.Installer.Platform;

/// <summary>Immutable native operating-system facts used by the platform policy.</summary>
/// <param name="IsWindows">Whether the current kernel is Windows.</param>
/// <param name="IsWorkstation">Whether Windows reports the client/workstation product type.</param>
/// <param name="BuildNumber">Native Windows build number, independent of compatibility shims.</param>
/// <param name="OperatingSystemArchitecture">Native operating-system architecture.</param>
/// <param name="ProcessArchitecture">Architecture of the running installer process.</param>
public sealed record InstallerPlatformFacts(
    bool IsWindows,
    bool IsWorkstation,
    int BuildNumber,
    InstallerCpuArchitecture OperatingSystemArchitecture,
    InstallerCpuArchitecture ProcessArchitecture);
