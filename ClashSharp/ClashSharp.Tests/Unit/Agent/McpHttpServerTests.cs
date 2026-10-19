using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Infrastructure.Agent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ClashSharp.Tests.Unit.Agent;

/// <summary>Exercises the real official MCP client and HTTP listener; the product backend is an isolated fixture.</summary>
[Collection("MCP HTTP environment")]
public sealed class McpHttpServerTests
{
    [Fact]
    public async Task AmbientHostingConfiguration_CannotReplaceTheLoopbackEndpoint()
    {
        string? previousUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        string? previousPreference = Environment.GetEnvironmentVariable("ASPNETCORE_PREFERHOSTINGURLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable("ASPNETCORE_PREFERHOSTINGURLS", "true");
            await using Fixture fixture = await Fixture.StartAsync();
            Assert.Equal(fixture.Configuration.Endpoint, fixture.Server.BoundEndpoint);
            await using McpClient client = await fixture.CreateClientAsync();
            Assert.False((await client.CallToolAsync("clashsharp_get_status")).IsError ?? false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previousUrls);
            Environment.SetEnvironmentVariable("ASPNETCORE_PREFERHOSTINGURLS", previousPreference);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task OfficialClient_SeesCurrentGrantsAndCannotInvokeHiddenTools(string? protocolVersion)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await using McpClient client = await fixture.CreateClientAsync(protocolVersion);
        Assert.Equal("clashsharp_get_status", Assert.Single(await client.ListToolsAsync()).Name);
        CallToolResult allowed = await client.CallToolAsync("clashsharp_get_status");
        Assert.False(allowed.IsError ?? false);
        Assert.Equal(1, fixture.Backend.Calls);

        CallToolResult denied = await client.CallToolAsync("clashsharp_update_settings",
            new Dictionary<string, object?> { ["operation_id"] = Guid.NewGuid().ToString(), ["changes"] = new { RateUnit = "Bits" } });
        Assert.True(denied.IsError);
        Assert.Equal("mcp.access_denied", denied.StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Equal(1, fixture.Backend.Calls);

        fixture.Backend.Policy = new(true, McpAccessScope.ModifyConfiguration);
        string[] names = (await client.ListToolsAsync()).Select(tool => tool.Name).Order().ToArray();
        Assert.Equal(["clashsharp_retry_settings_application", "clashsharp_save_profile", "clashsharp_update_settings"], names);
        Assert.True((await client.CallToolAsync("clashsharp_get_status")).IsError);
        Assert.Equal(1, fixture.Backend.Calls);
    }

    [Theory]
    [InlineData(false, null, null, HttpStatusCode.Unauthorized)]
    [InlineData(true, "http://untrusted.example", null, HttpStatusCode.Forbidden)]
    [InlineData(true, "null", null, HttpStatusCode.Forbidden)]
    [InlineData(true, null, "untrusted.example", HttpStatusCode.Forbidden)]
    public async Task HttpBoundary_RejectsMissingCredentialsAndForeignOriginsOrHosts(
        bool authenticated, string? origin, string? host, HttpStatusCode expected)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpRequestMessage request = fixture.InitializeRequest(authenticated);
        if (origin is not null) { request.Headers.TryAddWithoutValidation("Origin", origin); }
        if (host is not null) { request.Headers.Host = host + ":" + fixture.Configuration.Port; }

        using HttpResponseMessage response = await fixture.Http.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, fixture.Backend.Calls);
    }

    [Fact]
    public async Task CredentialRotation_RejectsOldCredentialOnTheSameEndpoint()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        using HttpRequestMessage oldRequest = fixture.InitializeRequest(authenticated: true);
        fixture.Backend.RotateCredential();

        using HttpResponseMessage oldResponse = await fixture.Http.SendAsync(oldRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);
        await using McpClient fresh = await fixture.CreateClientAsync();
        Assert.False((await fresh.CallToolAsync("clashsharp_get_status")).IsError ?? false);
        Assert.Equal(fixture.Configuration.Endpoint, fixture.Server.BoundEndpoint);
    }

    [Fact]
    public async Task UnexpectedBackendFailure_ReportsOnlyValueFreeDiagnostics()
    {
        const string privateText = "private-fixture-credential-not-for-output";
        await using Fixture fixture = await Fixture.StartAsync();
        fixture.Backend.Handler = (_, _) => throw new InvalidOperationException(privateText);
        await using McpClient client = await fixture.CreateClientAsync();

        CallToolResult result = await client.CallToolAsync("clashsharp_get_status");

        Assert.True(result.IsError);
        Assert.Equal("mcp.operation_failed", result.StructuredContent!.Value.GetProperty("error").GetString());
        Assert.DoesNotContain(privateText, result.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        ApplicationError error = Assert.Single(fixture.Errors.Records);
        Assert.DoesNotContain(privateText, error.Exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), error.Exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_WaitsForCompleteBackendWorkAfterTransportCancellation()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource cancellationObserved = NewSignal();
        TaskCompletionSource release = NewSignal();
        fixture.Backend.Handler = async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancellationObserved.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return Success();
        };
        await using McpClient client = await fixture.CreateClientAsync();
        Task<CallToolResult> call = client.CallToolAsync("clashsharp_get_status").AsTask();
        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop = fixture.Server.StopAsync(CancellationToken.None);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            if (stop is not null) { await stop.WaitAsync(TimeSpan.FromSeconds(10)); }
            _ = await Record.ExceptionAsync(async () => await call);
        }
        Assert.Null(fixture.Server.BoundEndpoint);
    }

    [Fact]
    public async Task CancelledStopWait_RetainsShutdownOwnershipUntilDisposalCompletes()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource cancellationObserved = NewSignal();
        TaskCompletionSource release = NewSignal();
        fixture.Backend.Handler = async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancellationObserved.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return Success();
        };
        await using McpClient client = await fixture.CreateClientAsync();
        Task<CallToolResult> call = client.CallToolAsync("clashsharp_get_status").AsTask();
        using CancellationTokenSource waiting = new();
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task stop = fixture.Server.StopAsync(waiting.Token);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await waiting.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            disposal = fixture.Server.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            if (disposal is not null) { await disposal.WaitAsync(TimeSpan.FromSeconds(10)); }
            _ = await Record.ExceptionAsync(async () => await call);
        }
        Assert.Null(fixture.Server.BoundEndpoint);
    }

    [Fact]
    public async Task OccupiedPort_DoesNotPublishRunningStateAndAllowsRetryAfterRelease()
    {
        using TcpListener occupied = new(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        Backend backend = new();
        await using McpHttpServer server = new(backend, new Errors());
        McpServerConfiguration configuration = new(port, backend.Policy);

        await Assert.ThrowsAnyAsync<IOException>(() => server.StartAsync(configuration, CancellationToken.None));
        Assert.Null(server.BoundEndpoint);
        occupied.Stop();
        await server.StartAsync(configuration, CancellationToken.None);
        Assert.Equal(configuration.Endpoint, server.BoundEndpoint);
        await server.StopAsync(CancellationToken.None);
        Assert.Null(server.BoundEndpoint);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static McpToolResponse Success() => new(false, JsonSerializer.SerializeToElement(new { verified = true }));

    private sealed class Backend : IMcpAgentBackend
    {
        private McpAccessPolicy _policy = new(true, McpAccessScope.ReadStatus);
        private McpAccessCredential _credential;
        private int _calls;

        public Backend() { Secret = McpAccessCredential.CreateSecret(); _credential = new(Secret); }
        public string Secret { get; private set; }
        public McpAccessPolicy Policy { get => Volatile.Read(ref _policy); set => Volatile.Write(ref _policy, value); }
        public int Calls => Volatile.Read(ref _calls);
        public Func<McpToolRequest, CancellationToken, Task<McpToolResponse>>? Handler { get; set; }
        public McpAccessPolicy CapturePolicy() => Policy;
        public bool IsCurrentIdentity(McpClientIdentity identity) => identity == Volatile.Read(ref _credential).Identity;
        public bool TryAuthenticate(string secret, [NotNullWhen(true)] out McpClientIdentity? identity) =>
            Volatile.Read(ref _credential).TryAuthenticate(secret, out identity);
        public void RotateCredential() { Secret = McpAccessCredential.CreateSecret(); Volatile.Write(ref _credential, new(Secret)); }

        public async Task<McpToolResponse> ExecuteAsync(McpClientIdentity identity, McpToolRequest request, CancellationToken cancellationToken)
        {
            if (!IsCurrentIdentity(identity)) { return McpToolResponse.Error("mcp.access_denied"); }
            Interlocked.Increment(ref _calls);
            return Handler is { } handler ? await handler(request, cancellationToken) : Success();
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
            Backend = new();
            Errors = new();
            Server = new(Backend, Errors);
            Configuration = new(port, Backend.Policy);
            Http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        }

        public Backend Backend { get; }
        public Errors Errors { get; }
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
            try { await fixture.Server.StartAsync(fixture.Configuration, CancellationToken.None); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<McpClient> CreateClientAsync(string? protocolVersion = null)
        {
            HttpClientTransport transport = new(new()
            {
                Endpoint = Configuration.Endpoint,
                Name = "ClashSharp isolated transport test",
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Backend.Secret },
            }, Http);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            return await McpClient.CreateAsync(transport, new() { ProtocolVersion = protocolVersion }, cancellationToken: timeout.Token);
        }

        public HttpRequestMessage InitializeRequest(bool authenticated)
        {
            HttpRequestMessage request = new(HttpMethod.Post, Configuration.Endpoint)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (authenticated) { request.Headers.Authorization = new("Bearer", Backend.Secret); }
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Server.DisposeAsync(); }
            finally { Http.Dispose(); }
        }
    }
}
