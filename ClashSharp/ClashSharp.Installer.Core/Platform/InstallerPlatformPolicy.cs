namespace ClashSharp.Installer.Platform;

/// <summary>Authorizes native x64 Windows 11 and Windows Server 2025 desktop environments.</summary>
public static class InstallerPlatformPolicy
{
    /// <summary>The first Windows 11 build accepted by the installer.</summary>
    public const int MinimumWindowsBuild = 22000;

    /// <summary>The first Windows Server 2025 build accepted with Desktop Experience.</summary>
    public const int MinimumWindowsServerBuild = 26100;

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

        if (!facts.IsWorkstation && !facts.IsServerDesktopExperience)
        {
            return Blocked("installer.environment.desktop_experience_required");
        }

        if (facts.IsWorkstation && facts.BuildNumber < MinimumWindowsBuild)
        {
            return Blocked("installer.environment.windows_11_required");
        }

        if (!facts.IsWorkstation && facts.BuildNumber < MinimumWindowsServerBuild)
        {
            return Blocked("installer.environment.windows_server_2025_required");
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
