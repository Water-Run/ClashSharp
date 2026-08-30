namespace ClashSharp.Installer.Platform;

/// <summary>Stable processor architectures used by installer platform authorization.</summary>
public enum InstallerCpuArchitecture
{
    /// <summary>The architecture could not be proven.</summary>
    Unknown = 0,

    /// <summary>32-bit Intel-compatible architecture.</summary>
    X86 = 1,

    /// <summary>64-bit AMD64 architecture.</summary>
    X64 = 2,

    /// <summary>32-bit ARM architecture.</summary>
    Arm = 3,

    /// <summary>64-bit ARM architecture.</summary>
    Arm64 = 4,
}
