using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Infrastructure.Agent;

namespace ClashSharp.Tests.Integration.Libraries;

/// <summary>
/// Enumerates the loopback MCP listener's request-admission matrix (host, origin, authorization, query, size limits,
/// policy state, unknown paths) against the documented precedence: locality, query rejection, authentication, policy.
/// The server runs in-process on a reserved loopback port with an isolated backend fixture.
/// </summary>
[Collection("MCP HTTP environment")]
public sealed class McpHttpBoundaryMatrixTests
{
    /// <summary>Verifies every Host header value is admitted only for the two loopback names on the bound port.</summary>
    [Fact]
    public async Task HostHeader_IsAdmittedOnlyForLoopbackNamesOnTheBoundPort()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        int port = fixture.Port;
        (string Host, HttpStatusCode Expected)[] cases =
        [
            ($"127.0.0.1:{port}", HttpStatusCode.OK),
            ($"localhost:{port}", HttpStatusCode.OK),
            ($"LOCALHOST:{port}", HttpStatusCode.OK),
            ($"127.0.0.1:{port + 1}", HttpStatusCode.Forbidden),
            ($"localhost:{port + 1}", HttpStatusCode.Forbidden),
            ("127.0.0.1", HttpStatusCode.Forbidden),
            ("localhost", HttpStatusCode.Forbidden),
            ($"[::1]:{port}", HttpStatusCode.Forbidden),
            ($"0.0.0.0:{port}", HttpStatusCode.Forbidden),
            ($"evil.example:{port}", HttpStatusCode.Forbidden),
            ($"127.0.0.1.evil.example:{port}", HttpStatusCode.Forbidden),
            ($"localhost.evil.example:{port}", HttpStatusCode.Forbidden),
        ];

        foreach ((string host, HttpStatusCode expected) in cases)
        {
            using HttpRequestMessage request = fixture.Initialize();
            request.Headers.Host = host;
            HttpStatusCode actual = await SendAsync(fixture, request);

            Assert.True(expected == actual, $"Host '{host}' expected {expected} but was {actual}");
        }

        Assert.Equal(0, fixture.Backend.Calls);
    }

    /// <summary>Verifies the Origin header is either absent or exactly one of the two loopback origins of this port.</summary>
    [Fact]
    public async Task OriginHeader_MustBeAbsentOrExactlyALoopbackOrigin()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        int port = fixture.Port;
        string[] admitted = [$"http://127.0.0.1:{port}", $"http://localhost:{port}", $"HTTP://LOCALHOST:{port}"];
        string[] refused =
        [
            $"http://127.0.0.1:{port}/", $"https://127.0.0.1:{port}", $"http://127.0.0.1:{port + 1}", $"http://127.0.0.1", "null",
            "http://evil.example", $"http://127.0.0.1:{port}.evil.example", $"http://evil.example:{port}", $"http://[::1]:{port}", "file://", "*",
        ];

        using (HttpRequestMessage absent = fixture.Initialize())
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(fixture, absent));
        }

        foreach (string origin in admitted)
        {
            using HttpRequestMessage request = fixture.Initialize();
            request.Headers.TryAddWithoutValidation("Origin", origin);
            Assert.Equal(HttpStatusCode.OK, await SendAsync(fixture, request));
        }

        foreach (string origin in refused)
        {
            using HttpRequestMessage request = fixture.Initialize();
            request.Headers.TryAddWithoutValidation("Origin", origin);
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(fixture, request));
        }

        using HttpRequestMessage twice = fixture.Initialize();
        twice.Headers.TryAddWithoutValidation("Origin", [$"http://127.0.0.1:{port}", $"http://127.0.0.1:{port}"]);
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(fixture, twice));
        Assert.Equal(0, fixture.Backend.Calls);
    }

    /// <summary>Verifies only a single well-formed Bearer header carrying the current secret authenticates.</summary>
    [Fact]
    public async Task Authorization_AuthenticatesOnlyASingleExactBearerSecret()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string secret = fixture.Backend.Secret;
        string wrong = string.Concat(secret[..63], secret[63] == '0' ? "1" : "0");
        (string Name, string[] Values, bool Accepted)[] cases =
        [
            ("exact", [$"Bearer {secret}"], true),
            ("scheme case", [$"bearer {secret}"], true),
            ("scheme upper", [$"BEARER {secret}"], true),
            ("wrong last digit", [$"Bearer {wrong}"], false),
            ("uppercase secret", [$"Bearer {secret.ToUpperInvariant()}"], false),
            ("padded", [$"Bearer  {secret}"], false),
            ("no space", [$"Bearer{secret}"], false),
            ("basic scheme", [$"Basic {secret}"], false),
            ("token scheme", [$"Token {secret}"], false),
            ("secret only", [secret], false),
            ("short", [$"Bearer {secret[..63]}"], false),
            ("long", [$"Bearer {secret}0"], false),
            ("two headers", [$"Bearer {secret}", $"Bearer {secret}"], false),
            ("good then bad", [$"Bearer {secret}", $"Bearer {wrong}"], false),
        ];

        foreach ((string name, string[] values, bool accepted) in cases)
        {
            using HttpRequestMessage request = fixture.Initialize(authenticated: false);
            foreach (string value in values)
            {
                request.Headers.TryAddWithoutValidation("Authorization", value);
            }

            HttpResponseMessage response = await fixture.Http.SendAsync(request);
            using (response)
            {
                Assert.True(
                    accepted ? response.StatusCode == HttpStatusCode.OK : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest,
                    $"{name}: {response.StatusCode}");
                if (!accepted && response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Assert.Contains("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme, StringComparison.Ordinal);
                }
            }
        }

        using HttpRequestMessage none = fixture.Initialize(authenticated: false);
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(fixture, none));
        Assert.Equal(0, fixture.Backend.Calls);
    }

    /// <summary>Verifies the documented precedence of checks and that unauthenticated requests learn nothing about paths.</summary>
    [Fact]
    public async Task Precedence_IsLocalityThenQueryThenAuthenticationThenPolicy()
    {
        await using Fixture fixture = await Fixture.StartAsync();

        using (HttpRequestMessage badOriginNoAuth = fixture.Initialize(authenticated: false))
        {
            badOriginNoAuth.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(fixture, badOriginNoAuth));
        }

        using (HttpRequestMessage queryNoAuth = fixture.Initialize(authenticated: false, path: "/mcp?x=1"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(fixture, queryNoAuth));
        }

        using (HttpRequestMessage queryWithAuth = fixture.Initialize(path: "/mcp?access_token=" + fixture.Backend.Secret))
        {
            Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(fixture, queryWithAuth));
        }

        foreach (string path in new[] { "/", "/other", "/mcp/extra", "/MCP-not" })
        {
            using HttpRequestMessage unauthenticated = fixture.Initialize(authenticated: false, path: path);
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(fixture, unauthenticated));
            using HttpRequestMessage authenticated = fixture.Initialize(path: path);
            HttpStatusCode status = await SendAsync(fixture, authenticated);
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest or HttpStatusCode.OK, $"{path}: {status}");
        }

        fixture.Backend.Policy = new McpAccessPolicy(false, McpAccessScope.ReadStatus);
        using (HttpRequestMessage disabled = fixture.Initialize())
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await SendAsync(fixture, disabled));
        }

        using (HttpRequestMessage disabledNoAuth = fixture.Initialize(authenticated: false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(fixture, disabledNoAuth));
        }

        fixture.Backend.Policy = new McpAccessPolicy(true, McpAccessScope.ReadStatus);
        using (HttpRequestMessage enabled = fixture.Initialize())
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(fixture, enabled));
        }
    }

    /// <summary>Verifies oversized bodies and headers are refused by the listener before any handler runs.</summary>
    [Fact]
    public async Task RequestLimits_RefuseOversizedBodiesAndHeaders()
    {
        await using Fixture fixture = await Fixture.StartAsync();

        using (HttpRequestMessage large = fixture.Initialize())
        {
            large.Content = new ByteArrayContent(new byte[McpToolRequest.MaximumArgumentBytes + 64 * 1024]);
            large.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            HttpStatusCode? status = await TrySendAsync(fixture, large);
            Assert.True(status is null or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, status.ToString());
        }

        using (HttpRequestMessage bigHeader = fixture.Initialize())
        {
            bigHeader.Headers.TryAddWithoutValidation("X-Padding", new string('x', 20 * 1024));
            HttpStatusCode? status = await TrySendAsync(fixture, bigHeader);
            Assert.True(status is null or HttpStatusCode.RequestHeaderFieldsTooLarge or HttpStatusCode.BadRequest, status.ToString());
        }

        using HttpRequestMessage normal = fixture.Initialize();
        Assert.Equal(HttpStatusCode.OK, await SendAsync(fixture, normal));
        Assert.Equal(0, fixture.Backend.Calls);
    }

    private static async Task<HttpStatusCode?> TrySendAsync(Fixture fixture, HttpRequestMessage request)
    {
        try
        {
            return await SendAsync(fixture, request);
        }
        catch (HttpRequestException)
        {
            // The listener may close the connection while the oversized message is still being sent.
            return null;
        }
    }

    private static async Task<HttpStatusCode> SendAsync(Fixture fixture, HttpRequestMessage request)
    {
        using HttpResponseMessage response = await fixture.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode;
    }

    private sealed class Backend : IMcpAgentBackend
    {
        private McpAccessPolicy _policy = new(true, McpAccessScope.ReadStatus);
        private readonly McpAccessCredential _credential;
        private int _calls;

        public Backend()
        {
            Secret = McpAccessCredential.CreateSecret();
            _credential = new McpAccessCredential(Secret);
        }

        public string Secret { get; }

        public McpAccessPolicy Policy
        {
            get => Volatile.Read(ref _policy);
            set => Volatile.Write(ref _policy, value);
        }

        public int Calls => Volatile.Read(ref _calls);

        public McpAccessPolicy CapturePolicy() => Policy;

        public bool IsCurrentIdentity(McpClientIdentity identity) => identity == _credential.Identity;

        public bool TryAuthenticate(string secret, [NotNullWhen(true)] out McpClientIdentity? identity) =>
            _credential.TryAuthenticate(secret, out identity);

        public Task<McpToolResponse> ExecuteAsync(McpClientIdentity identity, McpToolRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(McpToolResponse.Error("mcp.unavailable"));
        }
    }

    private sealed class Errors : IApplicationErrorSink
    {
        public ConcurrentQueue<ApplicationError> Records { get; } = new();

        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
        {
            Records.Enqueue(applicationError);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(int port)
        {
            Port = port;
            Backend = new Backend();
            Server = new McpHttpServer(Backend, new Errors());
            Configuration = new McpServerConfiguration(port, Backend.Policy);
            Http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public int Port { get; }

        public Backend Backend { get; }

        public McpHttpServer Server { get; }

        public McpServerConfiguration Configuration { get; }

        public HttpClient Http { get; }

        public static async Task<Fixture> StartAsync()
        {
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Fixture fixture = new(port);
            try
            {
                await fixture.Server.StartAsync(fixture.Configuration, CancellationToken.None);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public HttpRequestMessage Initialize(bool authenticated = true, string path = "/mcp")
        {
            HttpRequestMessage request = new(HttpMethod.Post, new Uri($"http://127.0.0.1:{Port}{path}"))
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Backend.Secret);
            }

            return request;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Server.DisposeAsync();
            }
            finally
            {
                Http.Dispose();
            }
        }
    }
}
