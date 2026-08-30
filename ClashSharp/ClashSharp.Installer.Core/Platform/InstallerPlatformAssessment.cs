namespace ClashSharp.Installer.Platform;

/// <summary>Fail-closed result of evaluating native platform facts.</summary>
/// <param name="IsSupported">Whether the platform is an authorized installation target.</param>
/// <param name="DiagnosticCode">Stable machine-readable policy result.</param>
public sealed record InstallerPlatformAssessment(bool IsSupported, string DiagnosticCode);
