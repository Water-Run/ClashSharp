using System.IO.Pipes;
using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperServer : IAsyncDisposable
{
    Stream Transport { get; }

    Task WaitForConnectionAsync(CancellationToken cancellationToken);

    void VerifyClient(int expectedHelperProcessId);
}

internal interface IWindowsMachineHelperServerFactory
{
    IWindowsMachineHelperServer Create(InstallerMachineHelperBootstrap bootstrap);
}

internal sealed class WindowsMachineHelperServerFactory : IWindowsMachineHelperServerFactory
{
    private readonly WindowsMachineHelperPipeIdentity _identity;

    internal WindowsMachineHelperServerFactory()
        : this(new WindowsMachineHelperPipeIdentity())
    {
    }

    internal WindowsMachineHelperServerFactory(WindowsMachineHelperPipeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
    }

    public IWindowsMachineHelperServer Create(InstallerMachineHelperBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        NamedPipeServerStream stream = WindowsMachineHelperPipeSecurity.CreateServerStream(
            bootstrap,
            WindowsMachineHelperPipeSecurity.GetCurrentLogonSid());
        return new WindowsMachineHelperServer(stream, _identity);
    }
}

internal sealed class WindowsMachineHelperServer : IWindowsMachineHelperServer
{
    private readonly WindowsMachineHelperPipeIdentity _identity;
    private readonly NamedPipeServerStream _stream;

    internal WindowsMachineHelperServer(
        NamedPipeServerStream stream,
        WindowsMachineHelperPipeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(identity);
        _stream = stream;
        _identity = identity;
    }

    public Stream Transport => _stream;

    public Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
        _stream.WaitForConnectionAsync(cancellationToken);

    public void VerifyClient(int expectedHelperProcessId) =>
        _identity.VerifyClient(_stream.SafePipeHandle, expectedHelperProcessId);

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
