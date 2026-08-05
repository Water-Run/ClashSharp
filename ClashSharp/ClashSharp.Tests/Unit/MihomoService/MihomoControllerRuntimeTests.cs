using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.Principal;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.MihomoService;

public sealed class MihomoControllerRuntimeTests
{
    [Fact]
    public void NamedPipeTransport_UsesAnonymousNonInheritableClientSecurityContext()
    {
        Assert.Equal(
            TokenImpersonationLevel.Anonymous,
            MihomoNamedPipeControllerTransport.ControllerImpersonationLevel);
        using NamedPipeClientStream client = MihomoNamedPipeControllerTransport
            .CreateClientStream("ClashSharp.Test.Unconnected." + Guid.NewGuid().ToString("N"));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task NamedPipeTransport_RejectsWrongServerPidBeforeWritingHttpBytes()
    {
        string pipeName = "ClashSharp.Test.Controller." + Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using MihomoNamedPipeControllerTransport transport = new(
            new MihomoControllerAuthority($@"\\.\pipe\{pipeName}", new string('a', 64)),
            Environment.ProcessId + 1);

        Task<MihomoControllerHttpResponse> request = transport.SendAsync(
            HttpMethod.Get,
            "/version",
            null,
            4096,
            CancellationToken.None);
        await server.WaitForConnectionAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[1];
        int bytesRead = await server.ReadAsync(buffer, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => request);
        Assert.IsType<MihomoControllerServerIdentityException>(failure.InnerException);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public async Task NamedPipeTransport_UsesExactHttp11WithPrivateBearerAndNoNetworkFallback()
    {
        string pipeName = "ClashSharp.Test.Controller." + Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using MihomoNamedPipeControllerTransport transport = new(
            new MihomoControllerAuthority($@"\\.\pipe\{pipeName}", new string('b', 64)),
            Environment.ProcessId);
        Task<string> serverTask = ServeOneHttpResponseAsync(
            server,
            "{\"meta\":true,\"version\":\"test\"}");

        MihomoControllerHttpResponse response = await transport.SendAsync(
            HttpMethod.Get,
            "/version",
            null,
            4096,
            CancellationToken.None);
        string request = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"meta\":true,\"version\":\"test\"}",
            Encoding.UTF8.GetString(response.Content.Span));
        Assert.StartsWith("GET /version HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains(
            $"Authorization: Bearer {new string('b', 64)}\r\n",
            request,
            StringComparison.Ordinal);
        Assert.Contains("Host: localhost", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedPipeTransport_RejectsMissingOrNonCanonicalControllerSecret()
    {
        string pipePath = $@"\\.\pipe\ClashSharp.Test.Controller.{Guid.NewGuid():N}";
        string[] invalidSecrets =
        [
            string.Empty,
            new string('a', 63),
            new string('A', 64),
            new string('g', 64),
        ];

        foreach (string secret in invalidSecrets)
        {
            Assert.Throws<ArgumentException>(() =>
                new MihomoNamedPipeControllerTransport(
                    new MihomoControllerAuthority(pipePath, secret),
                    Environment.ProcessId));
        }
    }

    [Fact]
    public async Task ReadinessProbe_RequiresVersionAndExactConfigurationProjection()
    {
        FakeControllerTransport transport = new(
        [
            JsonResponse("{\"meta\":true,\"version\":\"1.19.27\"}"),
            JsonResponse("{\"mixed-port\":0,\"mode\":\"rule\",\"tun\":{\"enable\":true},\"secret\":\"must-not-project\"}"),
        ]);
        MihomoControllerReadinessProbe probe = new(new SingleControllerTransportFactory(transport));
        FakeMihomoChildProcess process = new("ready", 4321);

        MihomoServiceIpcEffectiveConfiguration ready = await probe.WaitUntilReadyAsync(
            new MihomoControllerAuthority(
                @"\\.\pipe\ClashSharp.Mihomo.Controller.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                new string('b', 64)),
            process,
            new MihomoRuntimeConfigurationPlan(
                0,
                MihomoServiceIpcRoutingMode.Rule,
                TunEnabled: true),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(ready.ControllerReady);
        Assert.Equal(0, ready.MixedPort);
        Assert.Equal(MihomoServiceIpcRoutingMode.Rule, ready.Mode);
        Assert.True(ready.TunEnabled);
        Assert.Equal(["/version", "/configs"], transport.Paths);
        Assert.Null(ready.Validate());
    }

    [Fact]
    public async Task ReadinessProbe_UnauthorizedPrivateControllerNeverBecomesReady()
    {
        FakeControllerTransport transport = new(
            [],
            new MihomoControllerHttpResponse(
                HttpStatusCode.Unauthorized,
                Encoding.UTF8.GetBytes("{\"message\":\"Unauthorized\"}")));
        MihomoControllerReadinessProbe probe = new(new SingleControllerTransportFactory(transport));
        FakeMihomoChildProcess process = new("unauthorized", 4321);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => probe.WaitUntilReadyAsync(
                new MihomoControllerAuthority(
                    @"\\.\pipe\ClashSharp.Mihomo.Controller.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    new string('b', 64)),
                process,
                new MihomoRuntimeConfigurationPlan(
                    0,
                    MihomoServiceIpcRoutingMode.Rule,
                    TunEnabled: true),
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.Equal("The controller version probe returned an unexpected status.", failure.Message);
        Assert.Equal("/version", Assert.Single(transport.Paths));
    }

    private static MihomoControllerHttpResponse JsonResponse(string content) =>
        new(HttpStatusCode.OK, Encoding.UTF8.GetBytes(content));

    private static async Task<string> ServeOneHttpResponseAsync(
        NamedPipeServerStream server,
        string responseBody)
    {
        await server.WaitForConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        byte[] buffer = new byte[4096];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await server.ReadAsync(buffer.AsMemory(count), CancellationToken.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            count += read;
            if (Encoding.ASCII.GetString(buffer, 0, count)
                .Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string request = Encoding.ASCII.GetString(buffer, 0, count);
        byte[] body = Encoding.UTF8.GetBytes(responseBody);
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await server.WriteAsync(headers, CancellationToken.None).ConfigureAwait(false);
        await server.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
        await server.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return request;
    }

    private sealed class FakeControllerTransport : IMihomoControllerTransport
    {
        private readonly ConcurrentQueue<MihomoControllerHttpResponse> _responses;
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly MihomoControllerHttpResponse? _fallbackResponse;

        internal FakeControllerTransport(
            IEnumerable<MihomoControllerHttpResponse> responses,
            MihomoControllerHttpResponse? fallbackResponse = null)
        {
            _responses = new ConcurrentQueue<MihomoControllerHttpResponse>(responses);
            _fallbackResponse = fallbackResponse;
        }

        internal IReadOnlyList<string> Paths => _paths.ToArray();

        public Task<MihomoControllerHttpResponse> SendAsync(
            HttpMethod method,
            string relativePath,
            ReadOnlyMemory<byte>? jsonContent,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, method);
            Assert.Null(jsonContent);
            _paths.Enqueue(relativePath);
            if (_responses.TryDequeue(out MihomoControllerHttpResponse? response))
            {
                return Task.FromResult(response);
            }

            return Task.FromResult(
                _fallbackResponse
                ?? throw new InvalidOperationException("No fake controller response remains."));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleControllerTransportFactory : IMihomoControllerTransportFactory
    {
        private readonly IMihomoControllerTransport _transport;

        internal SingleControllerTransportFactory(IMihomoControllerTransport transport)
        {
            _transport = transport;
        }

        public IMihomoControllerTransport Create(
            MihomoControllerAuthority authority,
            int expectedProcessId) => _transport;
    }
}
