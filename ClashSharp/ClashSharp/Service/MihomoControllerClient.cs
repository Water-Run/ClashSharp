using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Wraps the local mihomo external-controller API used by Clash# runtime pages.</summary>
/// <remarks>
/// Invariants: Requests target the configured local external-controller base URI.
/// Thread safety: Stateless aside from the wrapped HTTP client and safe for concurrent requests.
/// Side effects: Performs local HTTP requests against mihomo.
/// </remarks>
public sealed class MihomoControllerClient
{
    private const int MaximumStreamMessageBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan ServicePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Binds every App-owned controller socket to the exact Job root generation.</summary>
    private static readonly MihomoAppControllerTransport SharedAppControllerTransport = new(
        new MihomoCoreAppProcessIdentitySource(MihomoCoreService.Instance),
        WindowsTcpOwnerVerifier.Instance);

    /// <summary>Shared HTTP client for singleton usage.</summary>
    private static readonly HttpClient SharedHttpClient = CreateLocalHttpClient();

    /// <summary>Verified transport used by production WebSocket handshakes.</summary>
    private static readonly HttpMessageInvoker SharedWebSocketInvoker = new(
        CreateLocalHttpMessageHandler());

    /// <summary>Shared singleton instance.</summary>
    /// <value>A non-null controller client.</value>
    public static MihomoControllerClient Instance { get; } = new(
        SharedHttpClient,
        MihomoControllerEndpoint.BaseUri,
        static () => AppSettingsService.Instance.MihomoControllerSecret,
        static () => SharedAppControllerTransport.Capture() is not null,
        new MihomoControllerServiceBroker(MihomoServiceManager.Instance),
        SharedAppControllerTransport,
        SharedWebSocketInvoker);

    /// <summary>Wrapped HTTP client.</summary>
    private readonly HttpClient _httpClient;

    /// <summary>Controller base URI ending with a slash.</summary>
    private readonly Uri _baseUri;

    /// <summary>Returns the bearer secret matching the currently generated runtime configuration.</summary>
    private readonly Func<string> _getControllerSecret;

    /// <summary>Reports whether this App process currently owns the direct controller.</summary>
    private readonly Func<bool>? _isAppCoreRunning;

    /// <summary>Typed broker used only when the Windows service owns mihomo.</summary>
    private readonly IMihomoControllerServiceBroker? _serviceBroker;

    /// <summary>Authenticates the App-owned controller and mixed listener against its Job root.</summary>
    private readonly MihomoAppControllerTransport? _appControllerTransport;

    /// <summary>Routes production WebSocket handshakes through the same PID-bound connector.</summary>
    private readonly HttpMessageInvoker? _webSocketInvoker;

    /// <summary>Initializes a controller client using the default local endpoint.</summary>
    public MihomoControllerClient()
        : this(
            SharedHttpClient,
            MihomoControllerEndpoint.BaseUri,
            static () => AppSettingsService.Instance.MihomoControllerSecret,
            static () => SharedAppControllerTransport.Capture() is not null,
            new MihomoControllerServiceBroker(MihomoServiceManager.Instance),
            SharedAppControllerTransport,
            SharedWebSocketInvoker)
    {
    }

    /// <summary>Initializes a controller client.</summary>
    /// <param name="httpClient">HTTP client used for requests. Must not be null.</param>
    /// <param name="baseUri">External-controller base URI. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public MihomoControllerClient(HttpClient httpClient, Uri baseUri)
        : this(httpClient, baseUri, static () => string.Empty)
    {
    }

    /// <summary>Initializes a controller client with a fixed bearer secret.</summary>
    /// <param name="httpClient">HTTP client used for requests. Must not be null.</param>
    /// <param name="baseUri">External-controller base URI. Must not be null.</param>
    /// <param name="controllerSecret">Bearer secret sent with every request. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public MihomoControllerClient(HttpClient httpClient, Uri baseUri, string controllerSecret)
        : this(httpClient, baseUri, () => controllerSecret ?? throw new ArgumentNullException(nameof(controllerSecret)))
    {
        ArgumentNullException.ThrowIfNull(controllerSecret);
    }

    /// <summary>Initializes a controller client with a dynamic bearer-secret source.</summary>
    private MihomoControllerClient(HttpClient httpClient, Uri baseUri, Func<string> getControllerSecret)
        : this(
            httpClient,
            baseUri,
            getControllerSecret,
            isAppCoreRunning: null,
            serviceBroker: null,
            appControllerTransport: null,
            webSocketInvoker: null,
            ownerAware: false)
    {
    }

    /// <summary>Initializes an owner-aware controller facade.</summary>
    internal MihomoControllerClient(
        HttpClient httpClient,
        Uri baseUri,
        Func<string> getControllerSecret,
        Func<bool> isAppCoreRunning,
        IMihomoControllerServiceBroker serviceBroker)
        : this(
            httpClient,
            baseUri,
            getControllerSecret,
            isAppCoreRunning ?? throw new ArgumentNullException(nameof(isAppCoreRunning)),
            serviceBroker ?? throw new ArgumentNullException(nameof(serviceBroker)),
            appControllerTransport: null,
            webSocketInvoker: null,
            ownerAware: true)
    {
    }

    /// <summary>Initializes an owner-aware facade with an injectable App listener identity boundary.</summary>
    internal MihomoControllerClient(
        HttpClient httpClient,
        Uri baseUri,
        Func<string> getControllerSecret,
        Func<bool> isAppCoreRunning,
        IMihomoControllerServiceBroker serviceBroker,
        MihomoAppControllerTransport appControllerTransport,
        HttpMessageInvoker? webSocketInvoker = null)
        : this(
            httpClient,
            baseUri,
            getControllerSecret,
            isAppCoreRunning ?? throw new ArgumentNullException(nameof(isAppCoreRunning)),
            serviceBroker ?? throw new ArgumentNullException(nameof(serviceBroker)),
            appControllerTransport ?? throw new ArgumentNullException(nameof(appControllerTransport)),
            webSocketInvoker,
            ownerAware: true)
    {
    }

    private MihomoControllerClient(
        HttpClient httpClient,
        Uri baseUri,
        Func<string> getControllerSecret,
        Func<bool>? isAppCoreRunning,
        IMihomoControllerServiceBroker? serviceBroker,
        MihomoAppControllerTransport? appControllerTransport,
        HttpMessageInvoker? webSocketInvoker,
        bool ownerAware)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(baseUri);
        _baseUri = EnsureTrailingSlash(baseUri);
        _getControllerSecret = getControllerSecret ?? throw new ArgumentNullException(nameof(getControllerSecret));
        if (ownerAware != (isAppCoreRunning is not null && serviceBroker is not null))
        {
            throw new ArgumentException(
                "The owner-state source and service broker must be supplied together.");
        }

        _isAppCoreRunning = isAppCoreRunning;
        _serviceBroker = serviceBroker;
        _appControllerTransport = appControllerTransport;
        _webSocketInvoker = webSocketInvoker;
    }

    /// <summary>Creates the production client without consulting machine or environment proxy settings.</summary>
    /// <remarks>Local controller credentials must never be forwarded to an HTTP proxy.</remarks>
    internal static HttpClient CreateLocalHttpClient()
    {
        return new HttpClient(CreateLocalHttpMessageHandler())
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>Creates the transport used exclusively for the loopback controller.</summary>
    internal static SocketsHttpHandler CreateLocalHttpMessageHandler()
    {
        return CreateLocalHttpMessageHandler(SharedAppControllerTransport);
    }

    /// <summary>Creates an isolated HTTP/1.1 transport with an injectable identity boundary.</summary>
    internal static SocketsHttpHandler CreateLocalHttpMessageHandler(
        MihomoAppControllerTransport appControllerTransport)
    {
        ArgumentNullException.ThrowIfNull(appControllerTransport);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = appControllerTransport.ConnectAsync,
        };
    }

    /// <summary>Reads active connections from the local mihomo external controller.</summary>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Active connection rows; empty when mihomo reports no active connections.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    /// <exception cref="JsonException">The mihomo API returns invalid JSON.</exception>
    public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcResponse response = await SendServiceCommandAsync(
                route.ServiceBinding,
                MihomoServiceIpcCommand.GetConnections,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapConnections(RequireSuccessful(response).ConnectionSnapshot!);
        }

        using JsonDocument document = await GetJsonAsync("connections", cancellationToken).ConfigureAwait(false);
        return ParseActiveConnections(document.RootElement);
    }

    /// <summary>Checks whether the authenticated controller exposes the expected effective runtime plan.</summary>
    internal async Task<bool> MatchesRuntimeConfigurationAsync(
        RuntimeConfigurationActivationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            return await MatchesServiceRuntimeConfigurationAsync(
                plan,
                route.ServiceBinding,
                cancellationToken).ConfigureAwait(false);
        }

        MihomoAppProcessIdentity? appIdentity = _appControllerTransport?.Capture();
        if (_appControllerTransport is not null && appIdentity is null)
        {
            return false;
        }

        using JsonDocument document = await GetJsonAsync("configs", cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("mixed-port", out JsonElement mixedPort)
            || !mixedPort.TryGetInt32(out int actualMixedPort)
            || actualMixedPort != plan.MixedPort
            || !root.TryGetProperty("mode", out JsonElement mode)
            || mode.ValueKind != JsonValueKind.String
            || !string.Equals(
                mode.GetString(),
                MihomoRuntimeConfigurationBuilder.MapToMihomoMode(plan.Mode),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool actualTunEnabled = root.TryGetProperty("tun", out JsonElement tun)
            && tun.ValueKind == JsonValueKind.Object
            && tun.TryGetProperty("enable", out JsonElement enable)
            && enable.ValueKind == JsonValueKind.True;
        if (actualTunEnabled != plan.TunEnabled)
        {
            return false;
        }

        return appIdentity is not { } identity
            || _appControllerTransport!.IsStillCurrent(identity)
                && _appControllerTransport.IsLoopbackListenerOwnedBy(plan.MixedPort, identity)
                && _appControllerTransport.IsStillCurrent(identity);
    }

    /// <summary>Checks readiness against an exact previously observed service runtime.</summary>
    internal async Task<bool> MatchesServiceRuntimeConfigurationAsync(
        RuntimeConfigurationActivationPlan plan,
        MihomoServiceIpcControllerBinding expectedRuntime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expectedRuntime);
        MihomoServiceIpcResponse response = await SendServiceCommandAsync(
            expectedRuntime,
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        MihomoServiceIpcEffectiveConfiguration effective =
            RequireSuccessful(response).EffectiveConfiguration!;
        return plan.TunEnabled
            && effective.ControllerReady
            && effective.MixedPort == 0
            && effective.TunEnabled == plan.TunEnabled
            && effective.Mode == MapRoutingMode(plan.Mode);
    }

    /// <summary>Streams live connection snapshots through the authenticated controller WebSocket.</summary>
    /// <param name="cancellationToken">Cancels connection establishment and message reads.</param>
    /// <returns>Connection snapshots until the server closes the socket.</returns>
    /// <exception cref="WebSocketException">The controller WebSocket cannot be reached or closes abnormally.</exception>
    /// <exception cref="InvalidDataException">The controller sends a binary or oversized message.</exception>
    /// <exception cref="JsonException">The controller sends invalid JSON.</exception>
    public async IAsyncEnumerable<IReadOnlyList<ActiveConnection>> StreamActiveConnectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MihomoServiceIpcResponse response = await SendServiceCommandAsync(
                    route.ServiceBinding,
                    MihomoServiceIpcCommand.GetConnections,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                yield return MapConnections(RequireSuccessful(response).ConnectionSnapshot!);
                await Task.Delay(ServicePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        using ClientWebSocket socket = CreateLocalWebSocket();
        await ConnectLocalWebSocketAsync(
                socket,
                BuildWebSocketUri("connections"),
                cancellationToken)
            .ConfigureAwait(false);

        while (socket.State == WebSocketState.Open)
        {
            byte[]? payload = await ReceiveTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                yield break;
            }

            yield return ParseActiveConnectionsPayload(payload);
        }
    }

    /// <summary>Streams live mihomo log lines through the authenticated controller WebSocket.</summary>
    /// <param name="cancellationToken">Cancels connection establishment and message reads.</param>
    /// <returns>Normalized log level and message pairs until the server closes the socket.</returns>
    public async IAsyncEnumerable<(string Level, string Message)> StreamLogsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            long cursor = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MihomoServiceIpcResponse response = await SendServiceCommandAsync(
                    route.ServiceBinding,
                    MihomoServiceIpcCommand.GetRuntimeLogs,
                    runtimeLogQuery: new MihomoServiceIpcRuntimeLogQuery
                    {
                        AfterSequence = cursor,
                        MaximumEntries = MihomoServiceIpcProtocol.MaximumRuntimeLogEntries,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                MihomoServiceIpcRuntimeLogSnapshot snapshot =
                    RequireSuccessful(response).RuntimeLogSnapshot!;
                foreach (MihomoServiceIpcRuntimeLogEntry entry in snapshot.Entries)
                {
                    cursor = entry.Sequence;
                    yield return (MapLogLevel(entry.Level), entry.Message);
                }

                if (snapshot.Entries.Count == 0 && snapshot.LatestSequence > cursor)
                {
                    // The bounded service ring can legitimately drop old entries.
                    cursor = snapshot.LatestSequence;
                }

                await Task.Delay(ServicePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        using ClientWebSocket socket = CreateLocalWebSocket();
        await ConnectLocalWebSocketAsync(
                socket,
                BuildWebSocketUri("logs"),
                cancellationToken)
            .ConfigureAwait(false);

        while (socket.State == WebSocketState.Open)
        {
            byte[]? payload = await ReceiveTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                yield break;
            }

            yield return ParseRuntimeLogPayload(payload);
        }
    }

    /// <summary>Parses one REST or WebSocket connection snapshot.</summary>
    internal static IReadOnlyList<ActiveConnection> ParseActiveConnectionsPayload(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return ParseActiveConnections(document.RootElement);
    }

    /// <summary>Parses one mihomo `/logs` WebSocket message.</summary>
    internal static (string Level, string Message) ParseRuntimeLogPayload(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        string level = GetString(root, "type").Trim().ToLowerInvariant() switch
        {
            "debug" => "Debug",
            "warning" or "warn" => "Warning",
            "error" or "fatal" => "Error",
            _ => "Info",
        };
        string message = MihomoServiceIpcProtocol.NormalizeRuntimeLogMessage(
            GetString(root, "payload"));
        if (message.Length == 0)
        {
            throw new JsonException("Mihomo log message did not contain a payload.");
        }

        return (level, message);
    }

    private static IReadOnlyList<ActiveConnection> ParseActiveConnections(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("connections", out JsonElement connectionsElement)
            || connectionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ActiveConnection> connections = [];
        foreach (JsonElement connectionElement in connectionsElement.EnumerateArray())
        {
            connections.Add(ParseConnection(connectionElement));
        }

        return connections;
    }

    private ClientWebSocket CreateLocalWebSocket()
    {
        ClientWebSocket socket = new();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        string controllerSecret = _getControllerSecret();
        if (!string.IsNullOrWhiteSpace(controllerSecret))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {controllerSecret}");
        }

        return socket;
    }

    private Task ConnectLocalWebSocketAsync(
        ClientWebSocket socket,
        Uri uri,
        CancellationToken cancellationToken)
    {
        return _webSocketInvoker is null
            ? socket.ConnectAsync(uri, cancellationToken)
            : socket.ConnectAsync(uri, _webSocketInvoker, cancellationToken);
    }

    private Uri BuildWebSocketUri(string relativePath)
    {
        Uri httpUri = BuildUri(relativePath);
        UriBuilder builder = new(httpUri)
        {
            Scheme = httpUri.Scheme switch
            {
                "http" => "ws",
                "https" => "wss",
                _ => throw new InvalidOperationException("Mihomo controller URI must use HTTP or HTTPS."),
            },
        };
        return builder.Uri;
    }

    private static async Task<byte[]?> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        while (true)
        {
            WebSocketReceiveResult result = await socket
                .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Mihomo controller sent a non-text WebSocket message.");
            }

            if (message.Length + result.Count > MaximumStreamMessageBytes)
            {
                throw new InvalidDataException("Mihomo controller WebSocket message exceeded the size limit.");
            }

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    /// <summary>Closes one active connection through mihomo.</summary>
    /// <param name="connectionId">Connection id. Must not be null or empty.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    public async Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("Connection id must not be empty.", nameof(connectionId));
        }

        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcResponse response = await SendServiceCommandAsync(
                route.ServiceBinding,
                MihomoServiceIpcCommand.CloseConnection,
                connectionId: connectionId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _ = RequireSuccessful(response);
            return;
        }

        await SendWithoutBodyAsync(
            HttpMethod.Delete,
            $"connections/{Uri.EscapeDataString(connectionId)}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes all active connections through mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcResponse response = await SendServiceCommandAsync(
                route.ServiceBinding,
                MihomoServiceIpcCommand.CloseAllConnections,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _ = RequireSuccessful(response);
            return;
        }

        await SendWithoutBodyAsync(HttpMethod.Delete, "connections", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads selectable runtime proxy groups from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Selectable proxy groups.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    /// <exception cref="JsonException">The mihomo API returns invalid JSON.</exception>
    public async Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcProxyRuntimeSnapshot snapshot = await GetServiceProxyRuntimeAsync(
                route.ServiceBinding,
                cancellationToken).ConfigureAwait(false);
            return MapProxyGroups(snapshot.Groups);
        }

        using JsonDocument document = await GetJsonAsync("proxies", cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("proxies", out JsonElement proxiesElement)
            || proxiesElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<MihomoProxyGroup> groups = [];
        foreach (JsonProperty proxyProperty in proxiesElement.EnumerateObject())
        {
            JsonElement proxyElement = proxyProperty.Value;
            if (proxyElement.ValueKind != JsonValueKind.Object
                || !proxyElement.TryGetProperty("all", out JsonElement candidatesElement)
                || candidatesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            List<string> candidates = [];
            foreach (JsonElement candidateElement in candidatesElement.EnumerateArray())
            {
                if (candidateElement.ValueKind == JsonValueKind.String
                    && candidateElement.GetString() is { Length: > 0 } candidate)
                {
                    candidates.Add(candidate);
                }
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            string name = FirstNonEmpty(GetString(proxyElement, "name"), proxyProperty.Name);
            groups.Add(new MihomoProxyGroup(
                name,
                GetString(proxyElement, "type"),
                FirstNonEmpty(GetString(proxyElement, "now"), candidates[0]),
                candidates));
        }

        return groups;
    }

    /// <summary>Selects one proxy inside a runtime proxy group.</summary>
    /// <param name="groupName">Proxy group name. Must not be null or empty.</param>
    /// <param name="proxyName">Proxy name. Must not be null or empty.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="ArgumentException">A required name is empty.</exception>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    public async Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            throw new ArgumentException("Proxy group name must not be empty.", nameof(groupName));
        }

        if (string.IsNullOrWhiteSpace(proxyName))
        {
            throw new ArgumentException("Proxy name must not be empty.", nameof(proxyName));
        }

        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcResponse response = await SendServiceCommandAsync(
                route.ServiceBinding,
                MihomoServiceIpcCommand.SelectProxy,
                proxySelection: new MihomoServiceIpcProxySelection
                {
                    GroupName = groupName,
                    ProxyName = proxyName,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _ = RequireSuccessful(response);
            return;
        }

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Put,
            $"proxies/{Uri.EscapeDataString(groupName)}");
        request.Content = JsonContent.Create(new Dictionary<string, string> { ["name"] = proxyName });
        using HttpResponseMessage httpResponse = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
    }

    /// <summary>Reads proxy-provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Proxy provider resources.</returns>
    public async Task<IReadOnlyList<MihomoProviderResource>> GetProxyProvidersAsync(CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcProxyRuntimeSnapshot snapshot = await GetServiceProxyRuntimeAsync(
                route.ServiceBinding,
                cancellationToken).ConfigureAwait(false);
            return MapProviders(snapshot.Providers, MihomoServiceIpcProviderKind.Proxy);
        }

        using JsonDocument document = await GetJsonAsync("providers/proxies", cancellationToken).ConfigureAwait(false);
        return ParseProviders(document.RootElement, MihomoProviderKind.Proxy);
    }

    /// <summary>Reads rule-provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Rule provider resources.</returns>
    public async Task<IReadOnlyList<MihomoProviderResource>> GetRuleProvidersAsync(CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcProxyRuntimeSnapshot snapshot = await GetServiceProxyRuntimeAsync(
                route.ServiceBinding,
                cancellationToken).ConfigureAwait(false);
            return MapProviders(snapshot.Providers, MihomoServiceIpcProviderKind.Rule);
        }

        using JsonDocument document = await GetJsonAsync("providers/rules", cancellationToken).ConfigureAwait(false);
        return ParseProviders(document.RootElement, MihomoProviderKind.Rule);
    }

    /// <summary>Reads both proxy-provider and rule-provider resources.</summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Combined provider resources.</returns>
    public async Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken)
    {
        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcProxyRuntimeSnapshot snapshot = await GetServiceProxyRuntimeAsync(
                route.ServiceBinding,
                cancellationToken).ConfigureAwait(false);
            return MapProviders(snapshot.Providers, kind: null);
        }

        return await GetDirectProviderResourcesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MihomoProviderResource>> GetDirectProviderResourcesAsync(
        CancellationToken cancellationToken)
    {
        using JsonDocument proxyDocument = await GetJsonAsync(
            "providers/proxies",
            cancellationToken).ConfigureAwait(false);
        using JsonDocument ruleDocument = await GetJsonAsync(
            "providers/rules",
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MihomoProviderResource> proxyProviders = ParseProviders(
            proxyDocument.RootElement,
            MihomoProviderKind.Proxy);
        IReadOnlyList<MihomoProviderResource> ruleProviders = ParseProviders(
            ruleDocument.RootElement,
            MihomoProviderKind.Rule);

        List<MihomoProviderResource> resources = new(proxyProviders.Count + ruleProviders.Count);
        resources.AddRange(proxyProviders);
        resources.AddRange(ruleProviders);
        return resources;
    }

    /// <summary>Updates one provider resource through the correct mihomo namespace.</summary>
    /// <param name="provider">Provider to update.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="ArgumentException">Provider name is empty.</exception>
    public async Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.Name))
        {
            throw new ArgumentException("Provider name must not be empty.", nameof(provider));
        }

        ControllerRoute route = await ResolveRouteAsync(cancellationToken).ConfigureAwait(false);
        if (route.ServiceBinding is not null)
        {
            MihomoServiceIpcResponse response = await (_serviceBroker
                    ?? throw new InvalidOperationException("controller.service_broker_unavailable"))
                .UpdateProviderAsync(
                    route.ServiceBinding,
                    new MihomoServiceIpcProviderUpdate
                    {
                        Kind = provider.Kind == MihomoProviderKind.Proxy
                            ? MihomoServiceIpcProviderKind.Proxy
                            : MihomoServiceIpcProviderKind.Rule,
                        Name = provider.Name,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            _ = RequireSuccessful(response);
            return;
        }

        string namespacePath = provider.Kind == MihomoProviderKind.Proxy ? "providers/proxies" : "providers/rules";
        await SendWithoutBodyAsync(
            HttpMethod.Put,
            $"{namespacePath}/{Uri.EscapeDataString(provider.Name)}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves exactly one controller owner and fails closed on ambiguity.</summary>
    private async Task<ControllerRoute> ResolveRouteAsync(CancellationToken cancellationToken)
    {
        if (_serviceBroker is null || _isAppCoreRunning is null)
        {
            // Explicit public constructors are direct-controller test/integration seams.
            return ControllerRoute.Direct;
        }

        bool appOwnedBeforeObservation = _isAppCoreRunning();
        MihomoServiceStatus serviceStatus = _serviceBroker.GetLatestStatus();
        if (!serviceStatus.IsKnown)
        {
            serviceStatus = await _serviceBroker
                .GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        bool appOwnedAfterObservation = _isAppCoreRunning();
        if (appOwnedBeforeObservation != appOwnedAfterObservation)
        {
            throw new InvalidOperationException("controller.owner_changed_during_observation");
        }

        bool serviceOwned = serviceStatus.IsKnown
            && serviceStatus.IsScmRunning
            && serviceStatus.HasRunningChild
            && serviceStatus.ProtocolVersion == MihomoServiceIpcProtocol.CurrentVersion
            && serviceStatus.ChildState == MihomoServiceChildState.Running
            && serviceStatus.ServiceSessionId is { } serviceSessionId
            && serviceSessionId != Guid.Empty
            && serviceStatus.ActiveGeneration is >= 1
            && MihomoServiceIpcProtocol.IsCanonicalSha256(
                serviceStatus.ActiveConfigurationHash);

        if (appOwnedAfterObservation)
        {
            if (!serviceStatus.HasReleasedChildOwnership || serviceOwned)
            {
                throw new InvalidOperationException("controller.owner_ambiguous");
            }

            return ControllerRoute.Direct;
        }

        if (!serviceOwned)
        {
            throw new InvalidOperationException("controller.owner_unavailable");
        }

        return new ControllerRoute(new MihomoServiceIpcControllerBinding
        {
            ServiceSessionId = serviceStatus.ServiceSessionId!.Value,
            Generation = serviceStatus.ActiveGeneration!.Value,
            ConfigurationHash = serviceStatus.ActiveConfigurationHash!,
        });
    }

    /// <summary>Sends one typed capability; no arbitrary HTTP shape crosses this boundary.</summary>
    private Task<MihomoServiceIpcResponse> SendServiceCommandAsync(
        MihomoServiceIpcControllerBinding expectedRuntime,
        MihomoServiceIpcCommand command,
        string? connectionId = null,
        MihomoServiceIpcProxySelection? proxySelection = null,
        MihomoServiceIpcRuntimeLogQuery? runtimeLogQuery = null,
        CancellationToken cancellationToken = default)
    {
        return (_serviceBroker
                ?? throw new InvalidOperationException("controller.service_broker_unavailable"))
            .SendAsync(
                command,
                expectedRuntime,
                connectionId,
                proxySelection,
                runtimeLogQuery,
                cancellationToken);
    }

    private static MihomoServiceIpcResponse RequireSuccessful(MihomoServiceIpcResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? validationError = response.Validate();
        if (validationError is not null)
        {
            throw new InvalidDataException(
                $"The service controller broker returned invalid data ({validationError}).");
        }

        if (!response.Succeeded)
        {
            throw new InvalidOperationException(
                response.ErrorCode ?? "service.controller.command_failed");
        }

        return response;
    }

    private async Task<MihomoServiceIpcProxyRuntimeSnapshot> GetServiceProxyRuntimeAsync(
        MihomoServiceIpcControllerBinding expectedRuntime,
        CancellationToken cancellationToken)
    {
        MihomoServiceIpcResponse response = await SendServiceCommandAsync(
            expectedRuntime,
            MihomoServiceIpcCommand.GetProxyRuntimeSnapshot,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return RequireSuccessful(response).ProxyRuntimeSnapshot!;
    }

    private static IReadOnlyList<ActiveConnection> MapConnections(
        MihomoServiceIpcConnectionSnapshot snapshot)
    {
        List<ActiveConnection> connections = new(snapshot.Connections.Count);
        foreach (MihomoServiceIpcConnection connection in snapshot.Connections)
        {
            connections.Add(new ActiveConnection(
                connection.Id,
                connection.ProcessName,
                connection.Host,
                connection.RuleName,
                connection.RulePayload,
                connection.ProxyName,
                connection.UploadBytes,
                connection.DownloadBytes,
                connection.StartedAt));
        }

        return connections;
    }

    private static IReadOnlyList<MihomoProxyGroup> MapProxyGroups(
        IReadOnlyList<MihomoServiceIpcProxyGroup> groups)
    {
        List<MihomoProxyGroup> result = new(groups.Count);
        foreach (MihomoServiceIpcProxyGroup group in groups)
        {
            result.Add(new MihomoProxyGroup(
                group.Name,
                group.Type,
                group.CurrentSelection,
                group.Candidates));
        }

        return result;
    }

    private static IReadOnlyList<MihomoProviderResource> MapProviders(
        IReadOnlyList<MihomoServiceIpcProvider> providers,
        MihomoServiceIpcProviderKind? kind)
    {
        List<MihomoProviderResource> result = [];
        foreach (MihomoServiceIpcProvider provider in providers)
        {
            if (kind is not null && provider.Kind != kind)
            {
                continue;
            }

            result.Add(new MihomoProviderResource(
                provider.Name,
                provider.Kind == MihomoServiceIpcProviderKind.Proxy
                    ? MihomoProviderKind.Proxy
                    : MihomoProviderKind.Rule,
                provider.VehicleType,
                provider.Behavior,
                provider.ItemCount,
                provider.UpdatedAt));
        }

        return result;
    }

    private static MihomoServiceIpcRoutingMode MapRoutingMode(ClashSharpMode mode)
    {
        return mode switch
        {
            ClashSharpMode.Disabled or ClashSharpMode.Standby =>
                MihomoServiceIpcRoutingMode.Direct,
            ClashSharpMode.RuleTakeover => MihomoServiceIpcRoutingMode.Rule,
            ClashSharpMode.FullTakeover => MihomoServiceIpcRoutingMode.Global,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static string MapLogLevel(MihomoServiceIpcRuntimeLogLevel level)
    {
        return level switch
        {
            MihomoServiceIpcRuntimeLogLevel.Debug => "Debug",
            MihomoServiceIpcRuntimeLogLevel.Warning => "Warning",
            MihomoServiceIpcRuntimeLogLevel.Error => "Error",
            _ => "Info",
        };
    }

    /// <summary>Sends an HTTP request that does not require a request body.</summary>
    private async Task SendWithoutBodyAsync(HttpMethod method, string relativePath, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, relativePath);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads and parses one JSON endpoint.</summary>
    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, relativePath);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds an absolute request URI.</summary>
    private Uri BuildUri(string relativePath)
    {
        return new Uri(_baseUri, relativePath);
    }

    /// <summary>Creates one authenticated request for the app-owned local controller.</summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        HttpRequestMessage request = new(method, BuildUri(relativePath));
        string controllerSecret = _getControllerSecret();
        if (!string.IsNullOrWhiteSpace(controllerSecret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", controllerSecret);
        }

        return request;
    }

    /// <summary>Parses provider resources from one provider response root.</summary>
    private static IReadOnlyList<MihomoProviderResource> ParseProviders(JsonElement root, MihomoProviderKind kind)
    {
        if (!root.TryGetProperty("providers", out JsonElement providersElement)
            || providersElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<MihomoProviderResource> resources = [];
        foreach (JsonProperty providerProperty in providersElement.EnumerateObject())
        {
            JsonElement providerElement = providerProperty.Value;
            if (providerElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            resources.Add(new MihomoProviderResource(
                FirstNonEmpty(GetString(providerElement, "name"), providerProperty.Name),
                kind,
                GetString(providerElement, "vehicleType"),
                GetString(providerElement, "behavior"),
                ParseProviderItemCount(providerElement, kind),
                ParseUpdatedAt(providerElement)));
        }

        return resources;
    }

    /// <summary>Parses a provider item count.</summary>
    private static int ParseProviderItemCount(JsonElement providerElement, MihomoProviderKind kind)
    {
        if (kind == MihomoProviderKind.Proxy
            && providerElement.TryGetProperty("proxies", out JsonElement proxiesElement)
            && proxiesElement.ValueKind == JsonValueKind.Array)
        {
            return proxiesElement.GetArrayLength();
        }

        if (providerElement.TryGetProperty("ruleCount", out JsonElement ruleCountElement)
            && ruleCountElement.ValueKind == JsonValueKind.Number
            && ruleCountElement.TryGetInt32(out int ruleCount))
        {
            return Math.Max(0, ruleCount);
        }

        if (providerElement.TryGetProperty("rules", out JsonElement rulesElement)
            && rulesElement.ValueKind == JsonValueKind.Array)
        {
            return rulesElement.GetArrayLength();
        }

        return 0;
    }

    /// <summary>Parses one mihomo connection JSON object.</summary>
    private static ActiveConnection ParseConnection(JsonElement connectionElement)
    {
        JsonElement metadata = TryGetObject(connectionElement, "metadata");
        string host = FirstNonEmpty(
            GetString(metadata, "host"),
            GetString(metadata, "destinationIP"),
            GetString(metadata, "remoteDestination"),
            GetString(connectionElement, "host"));
        string processName = FirstNonEmpty(
            GetString(metadata, "process"),
            GetFileName(GetString(metadata, "processPath")),
            GetString(connectionElement, "process"),
            "unknown");
        string proxyName = ParseProxyName(connectionElement);
        string ruleName = FirstNonEmpty(GetString(connectionElement, "rule"), "MATCH");
        string rulePayload = GetString(connectionElement, "rulePayload");

        return new ActiveConnection(
            FirstNonEmpty(GetString(connectionElement, "id"), Guid.NewGuid().ToString("N")),
            processName,
            FirstNonEmpty(host, "unknown"),
            ruleName,
            rulePayload,
            proxyName,
            Math.Max(0, GetInt64(connectionElement, "upload")),
            Math.Max(0, GetInt64(connectionElement, "download")),
            ParseStartedAt(GetString(connectionElement, "start")));
    }

    /// <summary>Parses the selected proxy chain display text.</summary>
    private static string ParseProxyName(JsonElement connectionElement)
    {
        if (!connectionElement.TryGetProperty("chains", out JsonElement chainsElement)
            || chainsElement.ValueKind != JsonValueKind.Array)
        {
            return FirstNonEmpty(GetString(connectionElement, "chain"), "DIRECT");
        }

        List<string> chains = [];
        foreach (JsonElement chainElement in chainsElement.EnumerateArray())
        {
            string chain = chainElement.ValueKind == JsonValueKind.String ? chainElement.GetString() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(chain))
            {
                chains.Add(chain);
            }
        }

        return chains.Count == 0 ? "DIRECT" : string.Join(" / ", chains);
    }

    /// <summary>Attempts to read a named child object.</summary>
    private static JsonElement TryGetObject(JsonElement element, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement child)
            && child.ValueKind == JsonValueKind.Object
                ? child
                : default;
    }

    /// <summary>Reads a string property from a JSON object.</summary>
    private static string GetString(JsonElement element, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>Reads an integer property from a JSON object.</summary>
    private static long GetInt64(JsonElement element, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out long value)
                ? value
                : 0;
    }

    /// <summary>Parses an ISO timestamp reported by mihomo.</summary>
    private static DateTimeOffset ParseStartedAt(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : DateTimeOffset.Now;
    }

    /// <summary>Parses provider update time reported by mihomo.</summary>
    private static DateTimeOffset? ParseUpdatedAt(JsonElement element)
    {
        string value = GetString(element, "updatedAt");
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
    }

    /// <summary>Returns the first non-empty value.</summary>
    private static string FirstNonEmpty(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>Extracts a file name from a process path.</summary>
    private static string GetFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
    }

    /// <summary>Ensures the URI can safely resolve relative paths.</summary>
    private static Uri EnsureTrailingSlash(Uri uri)
    {
        string text = uri.AbsoluteUri;
        return text.EndsWith('/') ? uri : new Uri(text + "/", UriKind.Absolute);
    }

    private readonly record struct ControllerRoute(
        MihomoServiceIpcControllerBinding? ServiceBinding)
    {
        internal static ControllerRoute Direct => new(null);
    }
}
