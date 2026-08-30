namespace ClashSharp.Installer.Contracts;

/// <summary>Contains immutable preflight facts used to authorize an installer request.</summary>
/// <param name="IsSupported">Whether the operating environment meets minimum requirements.</param>
/// <param name="InstalledPackageVersion">Canonical installed package version, when present.</param>
/// <param name="IsApplicationRunning">Whether the target package currently owns a running process.</param>
/// <param name="BlockingDiagnosticCode">Stable blocking reason when the environment is unsupported.</param>
public sealed record InstallerEnvironmentSnapshot(
    bool IsSupported,
    string? InstalledPackageVersion,
    bool IsApplicationRunning,
    string? BlockingDiagnosticCode);
