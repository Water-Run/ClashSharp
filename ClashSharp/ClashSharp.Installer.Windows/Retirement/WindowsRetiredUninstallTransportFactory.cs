using System.IO.Pipes;
using System.Security.Principal;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Retirement;

internal interface IWindowsRetiredUninstallProcessLauncher
{
    Task<IWindowsElevatedHelperProcess> StartAsync(string executablePath,
        InstallerRetiredUninstallBootstrap bootstrap, CancellationToken cancellationToken);
}

internal interface IWindowsRetiredUninstallServerFactory
{
    IWindowsMachineHelperServer Create(InstallerRetiredUninstallBootstrap bootstrap);
}

internal interface IWindowsRetiredUninstallClientFactory
{
    IWindowsMachineHelperClient Create(InstallerRetiredUninstallBootstrap bootstrap);
}

/// <summary>Reuses the exact logon DACL and PID verification adapters on a separate pipe namespace.</summary>
internal sealed class WindowsRetiredUninstallTransportFactory : IWindowsRetiredUninstallServerFactory, IWindowsRetiredUninstallClientFactory
{
    IWindowsMachineHelperServer IWindowsRetiredUninstallServerFactory.Create(InstallerRetiredUninstallBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        NamedPipeServerStream stream = WindowsMachineHelperPipeSecurity.CreateServerStream(
            bootstrap.BuildSessionPipeName(), WindowsMachineHelperPipeSecurity.GetCurrentLogonSid());
        return new WindowsMachineHelperServer(stream, new WindowsMachineHelperPipeIdentity());
    }

    IWindowsMachineHelperClient IWindowsRetiredUninstallClientFactory.Create(InstallerRetiredUninstallBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        var stream = new NamedPipeClientStream(".", bootstrap.BuildSessionPipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        return new WindowsMachineHelperClient(stream, new WindowsMachineHelperPipeIdentity());
    }
}
