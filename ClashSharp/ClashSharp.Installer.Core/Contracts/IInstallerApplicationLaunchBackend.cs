namespace ClashSharp.Installer.Contracts;

/// <summary>Activates the exact installed package after independently verifying its registration.</summary>
public interface IInstallerApplicationLaunchBackend
{
    /// <summary>Gets whether the platform backend supports normal registered application launch.</summary>
    bool SupportsApplicationLaunch { get; }

    /// <summary>Verifies the installed release and activates its normal user interface.</summary>
    /// <param name="cancellationToken">Cancels verification before activation begins.</param>
    Task LaunchApplicationAsync(CancellationToken cancellationToken);
}
