using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Transactions;

internal sealed class WindowsInstallerDirectoryCleanupLayout
{
    internal const string LedgerFileName = "ClashSharp.InstallerDirectories.v1.json";
    private readonly IReadOnlyDictionary<InstallerDirectoryRole, string> _paths;

    internal WindowsInstallerDirectoryCleanupLayout(string programFiles, string programData)
    {
        WindowsMachineDeploymentRoots roots = WindowsMachineDeploymentRoots.Create(programFiles, programData);
        ProgramData = roots.CommonApplicationDataRoot;
        LedgerPath = Path.Combine(ProgramData, LedgerFileName);
        string productData = Path.Combine(ProgramData, "ClashSharp");
        _paths = new Dictionary<InstallerDirectoryRole, string>
        {
            [InstallerDirectoryRole.ProgramFilesProduct] = Path.Combine(roots.ProgramFilesRoot, "ClashSharp"),
            [InstallerDirectoryRole.ProgramDataProduct] = productData,
            [InstallerDirectoryRole.InstallerRoot] = Path.Combine(productData, "Installer"),
            [InstallerDirectoryRole.InstallerVersion] = Path.Combine(productData, "Installer", "v2"),
            [InstallerDirectoryRole.AuthorityRoot] = Path.Combine(productData, "InstallerAuthority"),
            [InstallerDirectoryRole.AuthorityVersion] = Path.Combine(productData, "InstallerAuthority", "v1"),
        };
    }

    internal string ProgramData { get; }
    internal string LedgerPath { get; }
    internal string GetPath(InstallerDirectoryRole role) => _paths.TryGetValue(role, out string? path)
        ? path : throw WindowsInstallerDirectoryLedger.Failure("role_invalid");

    internal bool TryGetRole(string path, out InstallerDirectoryRole role)
    {
        foreach (KeyValuePair<InstallerDirectoryRole, string> entry in _paths)
        {
            if (string.Equals(path, entry.Value, StringComparison.OrdinalIgnoreCase)) { role = entry.Key; return true; }
        }
        role = default;
        return false;
    }

    internal static WindowsInstallerDirectoryCleanupLayout CreateDefault() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    internal static InstallerDirectoryRole[] DeletionOrder =>
    [
        InstallerDirectoryRole.ProgramFilesProduct,
        InstallerDirectoryRole.InstallerVersion, InstallerDirectoryRole.InstallerRoot,
        InstallerDirectoryRole.AuthorityVersion, InstallerDirectoryRole.AuthorityRoot,
        InstallerDirectoryRole.ProgramDataProduct,
    ];
}
