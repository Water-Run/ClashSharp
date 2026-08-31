using System.IO.Pipes;
using System.Security.Principal;
using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperClient : IAsyncDisposable
{
    Stream Transport { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    void VerifyServer(int expectedParentProcessId);
}

internal interface IWindowsMachineHelperClientFactory
{
    IWindowsMachineHelperClient Create(InstallerMachineHelperBootstrap bootstrap);
}

internal sealed class WindowsMachineHelperClientFactory : IWindowsMachineHelperClientFactory
{
    private readonly WindowsMachineHelperPipeIdentity _identity;

    internal WindowsMachineHelperClientFactory()
        : this(new WindowsMachineHelperPipeIdentity())
    {
    }

    internal WindowsMachineHelperClientFactory(WindowsMachineHelperPipeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
    }

    public IWindowsMachineHelperClient Create(InstallerMachineHelperBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        var stream = new NamedPipeClientStream(
            ".",
            bootstrap.Invocation.BuildSessionPipeName(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        return new WindowsMachineHelperClient(stream, _identity);
    }
}

internal sealed class WindowsMachineHelperClient : IWindowsMachineHelperClient
{
    private readonly WindowsMachineHelperPipeIdentity _identity;
    private readonly NamedPipeClientStream _stream;

    internal WindowsMachineHelperClient(
        NamedPipeClientStream stream,
        WindowsMachineHelperPipeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(identity);
        _stream = stream;
        _identity = identity;
    }

    public Stream Transport => _stream;

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        _stream.ConnectAsync(cancellationToken);

    public void VerifyServer(int expectedParentProcessId)
    {
        if (!_stream.IsConnected)
        {
            throw new Contracts.InstallerProtocolException(
                "installer.machine_helper.pipe_not_connected");
        }

        _identity.VerifyServer(_stream.SafePipeHandle, expectedParentProcessId);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
