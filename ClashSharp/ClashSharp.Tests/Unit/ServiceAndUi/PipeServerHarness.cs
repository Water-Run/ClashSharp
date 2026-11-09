using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;
using ClashSharp.Tests.Unit.MihomoService;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>Runs the real ACL-protected pipe server over a childless supervisor on a private pipe name.</summary>
internal sealed class PipeServerHarness : IAsyncDisposable
{
    private readonly MihomoServiceTemporaryDirectory _directory = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly MihomoChildSupervisor _supervisor;
    private readonly Task _serverTask;

    private PipeServerHarness(string token)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier sid = identity.User
            ?? throw new InvalidOperationException("The test identity has no user SID.");
        Token = token;
        Options = MihomoServiceTestSupport.CreateOptions(_directory.Path, sid, token);
        Logs = new MihomoServiceLogBuffer(Options);
        MihomoRuntimeLogBuffer runtimeLogs = new(Logs);
        _supervisor = new MihomoChildSupervisor(
            Options,
            new MihomoGenerationStore(Options, protectDirectory: false),
            new MihomoEffectiveConfigurationMaterializer(protectDirectory: false),
            new FakeMihomoChildProcessLauncher([]),
            new FakeMihomoControllerReadinessProbe(),
            Logs,
            runtimeLogs,
            startupObservationDelay: TimeSpan.Zero,
            restartBackoffs: [TimeSpan.Zero],
            stopTimeout: TimeSpan.FromSeconds(1),
            serviceVersion: "harness");
        MihomoServicePipeServer server = new(
            Options,
            new MihomoServiceCommandProcessor(
                Options,
                _supervisor,
                Logs,
                new MihomoServiceControllerBroker(
                    _supervisor,
                    new MihomoNamedPipeControllerTransportFactory(),
                    runtimeLogs,
                    Logs)),
            Logs);
        _serverTask = server.RunAsync(_stopping.Token);
    }

    internal string Token { get; }

    internal MihomoServiceOptions Options { get; }

    internal MihomoServiceLogBuffer Logs { get; }

    /// <summary>Gets the server loop task; it must stay pending for the life of the service.</summary>
    internal Task ServerTask => _serverTask;

    internal static bool CanRun()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User is { } sid && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
    }

    internal static PipeServerHarness Start()
    {
        string token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            .PadRight(64, '0')
            .ToLowerInvariant();
        return new PipeServerHarness(token);
    }

    internal async Task<NamedPipeClientStream> ConnectAsync()
    {
        NamedPipeClientStream client = new(
            ".",
            Options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(deadline.Token);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        return client;
    }

    internal MihomoServiceIpcRequest NewRequest(
        MihomoServiceIpcCommand command = MihomoServiceIpcCommand.Hello)
    {
        return new MihomoServiceIpcRequest
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = Token,
            Command = command,
        };
    }

    /// <summary>Performs one complete framed exchange on a fresh connection.</summary>
    internal async Task<MihomoServiceIpcResponse> ExchangeAsync(MihomoServiceIpcRequest request)
    {
        await using NamedPipeClientStream client = await ConnectAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await MihomoServiceIpcFrameCodec.WriteRequestAsync(client, request, deadline.Token);
        return await MihomoServiceIpcFrameCodec.ReadResponseAsync(client, deadline.Token);
    }

    /// <summary>Sends raw bytes, closes the write side by disposing, and returns once the server has consumed the connection.</summary>
    internal async Task SendRawAndCloseAsync(byte[] bytes)
    {
        NamedPipeClientStream client = await ConnectAsync();
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            if (bytes.Length > 0)
            {
                await client.WriteAsync(bytes, deadline.Token);
                await client.FlushAsync(deadline.Token);
            }
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    internal static byte[] Frame(int declaredLength, byte[] payload)
    {
        byte[] bytes = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, declaredLength);
        payload.CopyTo(bytes, sizeof(int));
        return bytes;
    }

    internal static byte[] Frame(string json) =>
        Frame(Encoding.UTF8.GetByteCount(json), Encoding.UTF8.GetBytes(json));

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        try
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidDataException)
        {
            // A faulted loop is asserted by the tests that care; disposal must still release resources.
        }

        await _supervisor.DisposeAsync();
        _stopping.Dispose();
        _directory.Dispose();
    }
}
