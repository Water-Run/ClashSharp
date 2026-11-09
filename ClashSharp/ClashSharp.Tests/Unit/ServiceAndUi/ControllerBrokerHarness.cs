using System.IO.Pipes;
using System.Net;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

internal sealed record RecordedControllerCall(
    HttpMethod Method,
    string Path,
    byte[] Body,
    int MaximumResponseBytes);

/// <summary>Transport factory whose responses come from a delegate and whose calls are recorded.</summary>
internal sealed class ScriptedControllerTransportFactory : IMihomoControllerTransportFactory
{
    private readonly object _gate = new();
    private readonly List<RecordedControllerCall> _calls = [];

    internal Func<RecordedControllerCall, MihomoControllerHttpResponse> Handler { get; set; } =
        _ => new MihomoControllerHttpResponse(HttpStatusCode.NoContent, ReadOnlyMemory<byte>.Empty);

    internal RecordedControllerCall[] Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IMihomoControllerTransport Create(MihomoControllerAuthority authority, int expectedProcessId) =>
        new Transport(this);

    internal static MihomoControllerHttpResponse Json(string json) =>
        new(HttpStatusCode.OK, Encoding.UTF8.GetBytes(json));

    private sealed class Transport(ScriptedControllerTransportFactory owner) : IMihomoControllerTransport
    {
        public Task<MihomoControllerHttpResponse> SendAsync(
            HttpMethod method,
            string relativePath,
            ReadOnlyMemory<byte>? jsonContent,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            RecordedControllerCall call = new(
                method,
                relativePath,
                jsonContent?.ToArray() ?? [],
                maximumResponseBytes);
            lock (owner._gate)
            {
                owner._calls.Add(call);
            }

            return Task.FromResult(owner.Handler(call));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Transport factory that runs the real pipe transport against a private fake controller pipe.</summary>
internal sealed class RealPipeTransportFactory(string pipeName) : IMihomoControllerTransportFactory
{
    public IMihomoControllerTransport Create(MihomoControllerAuthority authority, int expectedProcessId) =>
        new MihomoNamedPipeControllerTransport(
            new MihomoControllerAuthority($@"\\.\pipe\{pipeName}", new string('c', 64)),
            Environment.ProcessId);
}

/// <summary>Minimal HTTP/1.1 controller that records every request line and answers 204.</summary>
internal sealed class FakeControllerPipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly List<string> _requestLines = [];

    internal FakeControllerPipeServer()
    {
        PipeName = "ClashSharp.Test.Controller." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        _loop = Task.Run(ServeAsync);
    }

    internal string PipeName { get; }

    internal string[] RequestLines
    {
        get
        {
            lock (_gate)
            {
                return [.. _requestLines];
            }
        }
    }

    private async Task ServeAsync()
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await _pipe.WaitForConnectionAsync(_stopping.Token);
                int count = 0;
                while (count < buffer.Length)
                {
                    int read = await _pipe.ReadAsync(buffer.AsMemory(count), _stopping.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    count += read;
                    if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                if (count > 0)
                {
                    string head = Encoding.ASCII.GetString(buffer, 0, count);
                    lock (_gate)
                    {
                        _requestLines.Add(head[..head.IndexOf("\r\n", StringComparison.Ordinal)]);
                    }

                    byte[] response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    await _pipe.WriteAsync(response, _stopping.Token);
                    await _pipe.FlushAsync(_stopping.Token);
                }

                try
                {
                    _pipe.WaitForPipeDrain();
                }
                catch (IOException)
                {
                    // The client may already have closed its end.
                }

                try
                {
                    _pipe.Disconnect();
                }
                catch (InvalidOperationException)
                {
                    // Already disconnected by the peer.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _pipe.DisposeAsync();
        try
        {
            await _loop.WaitAsync(TestTimeouts.Completion);
        }
        catch (TimeoutException)
        {
        }

        _stopping.Dispose();
    }
}
