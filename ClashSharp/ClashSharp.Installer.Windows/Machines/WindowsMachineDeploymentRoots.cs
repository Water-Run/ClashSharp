using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Derives the two fixed machine-owned roots without depending on a target user profile. This
/// narrower capability is used only to prove that an uninstall has already removed all protected
/// machine state after the target profile itself no longer exists.
/// </summary>
internal sealed record WindowsMachineDeploymentRoots(
    string ProgramFilesRoot,
    string CommonApplicationDataRoot,
    string MachineRoot,
    string ServiceDataRoot)
{
    internal static WindowsMachineDeploymentRoots Create(
        string programFilesRoot,
        string commonApplicationDataRoot)
    {
        string programFiles = WindowsMachineDeploymentPlan.CanonicalRoot(
            programFilesRoot,
            "installer.machine.program_files_invalid");
        string programData = WindowsMachineDeploymentPlan.CanonicalRoot(
            commonApplicationDataRoot,
            "installer.machine.common_application_data_invalid");
        if (string.Equals(programFiles, programData, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.machine.root_identity_invalid");
        }

        return new WindowsMachineDeploymentRoots(
            programFiles,
            programData,
            WindowsMachineDeploymentPlan.Descendant(
                programFiles,
                "ClashSharp",
                "Service"),
            WindowsMachineDeploymentPlan.Descendant(
                programData,
                "ClashSharp",
                "MihomoService"));
    }

    internal void Validate()
    {
        WindowsMachineDeploymentRoots expected = Create(
            ProgramFilesRoot,
            CommonApplicationDataRoot);
        if (!string.Equals(MachineRoot, expected.MachineRoot, StringComparison.Ordinal)
            || !string.Equals(
                ServiceDataRoot,
                expected.ServiceDataRoot,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.deployment_roots_invalid");
        }
    }
}
