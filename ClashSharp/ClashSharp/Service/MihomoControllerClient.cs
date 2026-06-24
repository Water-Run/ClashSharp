/*
 * Mihomo Controller Client
 * Wraps the local mihomo external-controller API used by Clash# runtime pages
 *
 * @author: WaterRun
 * @file: Service/MihomoControllerClient.cs
 * @date: 2026-06-24
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Wraps the local mihomo external-controller API used by Clash# runtime pages.</summary>
/// <remarks>
/// Invariants: Requests target the configured local external-controller base URI.
/// Thread safety: Stateless aside from the wrapped HTTP client and safe for concurrent requests.
/// Side effects: Performs local HTTP requests against mihomo.
/// </remarks>
public sealed class MihomoControllerClient
{
    /// <summary>Default local external-controller base URI.</summary>
    private static readonly Uri DefaultBaseUri = new("http://127.0.0.1:9090/");

    /// <summary>Shared HTTP client for singleton usage.</summary>
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Shared singleton instance.</summary>
    /// <value>A non-null controller client.</value>
    public static MihomoControllerClient Instance { get; } = new(SharedHttpClient, DefaultBaseUri);

    /// <summary>Wrapped HTTP client.</summary>
    private readonly HttpClient _httpClient;

    /// <summary>Controller base URI ending with a slash.</summary>
    private readonly Uri _baseUri;

    /// <summary>Initializes a controller client using the default local endpoint.</summary>
    public MihomoControllerClient()
        : this(SharedHttpClient, DefaultBaseUri)
    {
    }

    /// <summary>Initializes a controller client.</summary>
    /// <param name="httpClient">HTTP client used for requests. Must not be null.</param>
    /// <param name="baseUri">External-controller base URI. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public MihomoControllerClient(HttpClient httpClient, Uri baseUri)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(baseUri);
        _baseUri = EnsureTrailingSlash(baseUri);
    }

    /// <summary>Reads active connections from the local mihomo external controller.</summary>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Active connection rows; empty when mihomo reports no active connections.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    /// <exception cref="JsonException">The mihomo API returns invalid JSON.</exception>
    public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetJsonAsync("connections", cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("connections", out JsonElement connectionsElement)
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

    /// <summary>Closes one active connection through mihomo.</summary>
    /// <param name="connectionId">Connection id. Must not be null or empty.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("Connection id must not be empty.", nameof(connectionId));
        }

        return SendWithoutBodyAsync(HttpMethod.Delete, $"connections/{Uri.EscapeDataString(connectionId)}", cancellationToken);
    }

    /// <summary>Closes all active connections through mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes after mihomo acknowledges the request.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken)
    {
        return SendWithoutBodyAsync(HttpMethod.Delete, "connections", cancellationToken);
    }

    /// <summary>Reads selectable runtime proxy groups from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Selectable proxy groups.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    /// <exception cref="JsonException">The mihomo API returns invalid JSON.</exception>
    public async Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
    {
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

        using HttpResponseMessage response = await _httpClient.PutAsJsonAsync(
            BuildUri($"proxies/{Uri.EscapeDataString(groupName)}"),
            new Dictionary<string, string> { ["name"] = proxyName },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads proxy-provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Proxy provider resources.</returns>
    public async Task<IReadOnlyList<MihomoProviderResource>> GetProxyProvidersAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetJsonAsync("providers/proxies", cancellationToken).ConfigureAwait(false);
        return ParseProviders(document.RootElement, MihomoProviderKind.Proxy);
    }

    /// <summary>Reads rule-provider resources from mihomo.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Rule provider resources.</returns>
    public async Task<IReadOnlyList<MihomoProviderResource>> GetRuleProvidersAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetJsonAsync("providers/rules", cancellationToken).ConfigureAwait(false);
        return ParseProviders(document.RootElement, MihomoProviderKind.Rule);
    }

    /// <summary>Reads both proxy-provider and rule-provider resources.</summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Combined provider resources.</returns>
    public async Task<IReadOnlyList<MihomoProviderResource>> GetProviderResourcesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MihomoProviderResource> proxyProviders = await GetProxyProvidersAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MihomoProviderResource> ruleProviders = await GetRuleProvidersAsync(cancellationToken).ConfigureAwait(false);

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
    public Task UpdateProviderAsync(MihomoProviderResource provider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.Name))
        {
            throw new ArgumentException("Provider name must not be empty.", nameof(provider));
        }

        string namespacePath = provider.Kind == MihomoProviderKind.Proxy ? "providers/proxies" : "providers/rules";
        return SendWithoutBodyAsync(HttpMethod.Put, $"{namespacePath}/{Uri.EscapeDataString(provider.Name)}", cancellationToken);
    }

    /// <summary>Sends an HTTP request that does not require a request body.</summary>
    private async Task SendWithoutBodyAsync(HttpMethod method, string relativePath, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, BuildUri(relativePath));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads and parses one JSON endpoint.</summary>
    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(BuildUri(relativePath), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds an absolute request URI.</summary>
    private Uri BuildUri(string relativePath)
    {
        return new Uri(_baseUri, relativePath);
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
        return text.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(text + "/", UriKind.Absolute);
    }
}
