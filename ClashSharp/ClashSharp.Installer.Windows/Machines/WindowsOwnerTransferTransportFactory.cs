using System.IO.Pipes;
using System.Security.Principal;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsOwnerTransferProcessLauncher
{
    Task<IWindowsElevatedHelperProcess> StartAsync(string executablePath,
        InstallerOwnerTransferBootstrap bootstrap, CancellationToken cancellationToken);
}

internal interface IWindowsOwnerTransferServerFactory
{
    IWindowsMachineHelperServer Create(InstallerOwnerTransferBootstrap bootstrap);
}

internal interface IWindowsOwnerTransferClientFactory
{
    IWindowsMachineHelperClient Create(InstallerOwnerTransferBootstrap bootstrap);
}

/// <summary>Reuses the exact logon DACL and PID verification adapters on a separate pipe namespace.</summary>
internal sealed class WindowsOwnerTransferTransportFactory : IWindowsOwnerTransferServerFactory, IWindowsOwnerTransferClientFactory
{
    IWindowsMachineHelperServer IWindowsOwnerTransferServerFactory.Create(InstallerOwnerTransferBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        NamedPipeServerStream stream = WindowsMachineHelperPipeSecurity.CreateServerStream(
            bootstrap.BuildSessionPipeName(), WindowsMachineHelperPipeSecurity.GetCurrentLogonSid());
        return new WindowsMachineHelperServer(stream, new WindowsMachineHelperPipeIdentity());
    }

    IWindowsMachineHelperClient IWindowsOwnerTransferClientFactory.Create(InstallerOwnerTransferBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        var stream = new NamedPipeClientStream(".", bootstrap.BuildSessionPipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        return new WindowsMachineHelperClient(stream, new WindowsMachineHelperPipeIdentity());
    }
}
