namespace ClashSharp.Installer.Platform;

/// <summary>Authorizes only native x64 Windows 11 client environments.</summary>
public static class InstallerPlatformPolicy
{
    /// <summary>The first Windows 11 build accepted by the installer.</summary>
    public const int MinimumWindowsBuild = 22000;

    /// <summary>Evaluates native facts in a deterministic fail-closed order.</summary>
    /// <param name="facts">Facts captured by the Windows platform adapter.</param>
    /// <returns>A stable support decision and diagnostic code.</returns>
    public static InstallerPlatformAssessment Evaluate(InstallerPlatformFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.IsWindows)
        {
            return Blocked("installer.environment.windows_required");
        }

        if (!facts.IsWorkstation)
        {
            return Blocked("installer.environment.windows_client_required");
        }

        if (facts.BuildNumber < MinimumWindowsBuild)
        {
            return Blocked("installer.environment.windows_11_required");
        }

        if (facts.OperatingSystemArchitecture != InstallerCpuArchitecture.X64)
        {
            return Blocked("installer.environment.x64_os_required");
        }

        if (facts.ProcessArchitecture != InstallerCpuArchitecture.X64)
        {
            return Blocked("installer.environment.x64_process_required");
        }

        return new InstallerPlatformAssessment(
            IsSupported: true,
            DiagnosticCode: "installer.environment.supported");
    }

    private static InstallerPlatformAssessment Blocked(string diagnosticCode) =>
        new(IsSupported: false, DiagnosticCode: diagnosticCode);
}
