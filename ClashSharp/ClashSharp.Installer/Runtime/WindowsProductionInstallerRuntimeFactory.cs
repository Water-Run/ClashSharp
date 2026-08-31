using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Runtime;

/// <summary>Composes the production presentation runtime without exposing Windows capabilities.</summary>
internal static class WindowsProductionInstallerRuntimeFactory
{
    internal static IInstallerRuntime Create(EmbeddedInstallerReleaseManifest release)
    {
        ArgumentNullException.ThrowIfNull(release);
        string executablePath = Environment.ProcessPath
            ?? throw new InstallerProtocolException(
                "installer.runtime.executable_path_missing");
        WindowsInstallerParentEngine backend = WindowsInstallerParentEngine.CreateDefault(
            release.Bytes,
            executablePath);
        try
        {
            return new ProductionInstallerRuntime(backend);
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }
}
