using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.ServiceProtocol;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsServiceReadinessConnection : IAsyncDisposable
{
    uint ServerProcessId { get; }

    Task<MihomoServiceIpcResponse> ExchangeAsync(
        MihomoServiceIpcRequest request,
        CancellationToken cancellationToken);
}

internal interface IWindowsServiceReadinessConnectionFactory
{
    Task<IWindowsServiceReadinessConnection> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken);
}

/// <summary>Requires a correlated Hello from the exact SCM process before accepting installation.</summary>
internal sealed class WindowsServiceReadinessVerifier
{
    private readonly WindowsServiceConfigurationVerifier _configuration;
    private readonly IWindowsServiceReadinessConnectionFactory _connections;
    private readonly TimeSpan _timeout;

    internal WindowsServiceReadinessVerifier()
        : this(WindowsServiceConfigurationNative.Instance,
            new WindowsServiceReadinessConnectionFactory(), TimeSpan.FromSeconds(15))
    {
    }

    internal WindowsServiceReadinessVerifier(
        IWindowsServiceConfigurationNative configuration,
        IWindowsServiceReadinessConnectionFactory connections,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(connections);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _configuration = new WindowsServiceConfigurationVerifier(configuration);
        _connections = connections;
        _timeout = timeout;
    }

    internal async Task VerifyAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            _configuration.VerifyInstalled(plan, requireRunning: true, deadline.Token);
            await using IWindowsServiceReadinessConnection connection = await _connections
                .ConnectAsync(plan.Association.BuildServicePipeName(), deadline.Token)
                .ConfigureAwait(false);
            // Authenticate the connected handle before disclosing the deployment credential.
            uint serverProcessId = connection.ServerProcessId;
            RequireExactProcess(plan, serverProcessId, deadline.Token);
            var request = new MihomoServiceIpcRequest
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = Guid.NewGuid(),
                AuthenticationToken = plan.Association.AuthenticationToken,
                Command = MihomoServiceIpcCommand.Hello,
            };
            MihomoServiceIpcResponse response = await connection
                .ExchangeAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.ValidateFor(request) is not null
                || !response.Succeeded
                || response.Snapshot is not { ChildState: MihomoServiceChildState.Stopped } snapshot
                || !string.Equals(snapshot.ServiceVersion,
                    plan.Request.ExpectedPackageVersion, StringComparison.Ordinal))
            {
                throw new InstallerProtocolException("installer.machine.service_readiness_failed");
            }

            RequireExactProcess(plan, serverProcessId, deadline.Token);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new InstallerProtocolException("installer.machine.service_readiness_timeout", exception);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException)
        {
            throw new InstallerProtocolException("installer.machine.service_readiness_failed", exception);
        }
    }

    private void RequireExactProcess(
        WindowsMachineDeploymentPlan plan,
        uint serverProcessId,
        CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot service = _configuration.InspectInstalled(
            plan, requireRunning: true, cancellationToken);
        if (serverProcessId == 0 || service.ProcessId != serverProcessId)
        {
            throw new InstallerProtocolException("installer.machine.service_pipe_identity_mismatch");
        }
    }
}

/// <summary>Owns one bounded anonymous-token connection; sends no request during connection or identity checks.</summary>
internal sealed class WindowsServiceReadinessConnectionFactory : IWindowsServiceReadinessConnectionFactory
{
    public async Task<IWindowsServiceReadinessConnection> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new Connection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Connection(NamedPipeClientStream pipe) : IWindowsServiceReadinessConnection
    {
        public uint ServerProcessId => GetServerProcessId(pipe.SafePipeHandle);

        public async Task<MihomoServiceIpcResponse> ExchangeAsync(
            MihomoServiceIpcRequest request,
            CancellationToken cancellationToken)
        {
            await MihomoServiceIpcFrameCodec.WriteRequestAsync(pipe, request, cancellationToken)
                .ConfigureAwait(false);
            return await MihomoServiceIpcFrameCodec.ReadResponseAsync(pipe, cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => pipe.DisposeAsync();
    }

    private static uint GetServerProcessId(SafePipeHandle handle)
    {
        if (!GetNamedPipeServerProcessId(handle, out uint processId))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return processId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
