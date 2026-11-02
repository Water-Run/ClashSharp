namespace ClashSharp.Installer.Runtime;

/// <summary>Optional launch command for an independently verified installed application.</summary>
public interface IInstallerApplicationLaunchRuntime
{
    /// <summary>Gets whether the trusted composition can activate the installed application.</summary>
    bool SupportsApplicationLaunch { get; }

    /// <summary>Activates the exact installed package without selecting a path in presentation code.</summary>
    /// <param name="cancellationToken">Cancels verification before activation begins.</param>
    Task LaunchApplicationAsync(CancellationToken cancellationToken);
}
