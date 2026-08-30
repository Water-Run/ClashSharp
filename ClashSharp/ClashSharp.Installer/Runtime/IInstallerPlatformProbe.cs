using ClashSharp.Installer.Platform;

namespace ClashSharp.Installer.Runtime;

/// <summary>Captures native platform facts without granting mutation authority.</summary>
public interface IInstallerPlatformProbe
{
    /// <summary>Inspects the current kernel, Windows product type, and native architectures.</summary>
    InstallerPlatformFacts Inspect(CancellationToken cancellationToken);
}
