/*
 * Mihomo Connection Service
 * Reads active connection rows from the mihomo external controller
 *
 * @author: WaterRun
 * @file: Service/MihomoConnectionService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Reads active connection rows from the mihomo external controller.</summary>
/// <remarks>
/// Invariants: The service targets the local external-controller configured by <see cref="CoreConfigurationService"/>.
/// Thread safety: Stateless service and safe for concurrent calls.
/// Side effects: Performs local HTTP requests against mihomo.
/// </remarks>
public sealed class MihomoConnectionService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MihomoConnectionService"/> instance.</value>
    public static MihomoConnectionService Instance { get; } = new();

    /// <summary>Local mihomo external-controller endpoint.</summary>
    private static readonly Uri ConnectionsUri = new("http://127.0.0.1:9090/connections");

    /// <summary>Shared HTTP client used for local mihomo API requests.</summary>
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Initializes the connection service.</summary>
    private MihomoConnectionService()
    {
    }

    /// <summary>Reads active connections from the local mihomo external controller.</summary>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Active connection rows; empty when mihomo reports no active connections.</returns>
    /// <exception cref="HttpRequestException">The mihomo API request fails.</exception>
    /// <exception cref="JsonException">The mihomo API returns invalid JSON.</exception>
    public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(ConnectionsUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    /// <summary>Parses one mihomo connection JSON object.</summary>
    /// <param name="connectionElement">Connection JSON object.</param>
    /// <returns>Parsed connection row.</returns>
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
    /// <param name="connectionElement">Connection JSON object.</param>
    /// <returns>Proxy chain display text.</returns>
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
    /// <param name="element">Parent element.</param>
    /// <param name="propertyName">Property name. Must not be null.</param>
    /// <returns>Child object when present; otherwise default JSON element.</returns>
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
    /// <param name="element">JSON object.</param>
    /// <param name="propertyName">Property name. Must not be null.</param>
    /// <returns>String value when present; otherwise empty string.</returns>
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
    /// <param name="element">JSON object.</param>
    /// <param name="propertyName">Property name. Must not be null.</param>
    /// <returns>Integer value when present; otherwise zero.</returns>
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
    /// <param name="value">Timestamp text. Must not be null.</param>
    /// <returns>Parsed timestamp or current time when unavailable.</returns>
    private static DateTimeOffset ParseStartedAt(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : DateTimeOffset.Now;
    }

    /// <summary>Returns the first non-empty value from <paramref name="values"/>.</summary>
    /// <param name="values">Candidate values. Must not be null.</param>
    /// <returns>First non-empty value; otherwise empty string.</returns>
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
    /// <param name="path">Process path. Must not be null.</param>
    /// <returns>File name when available; otherwise empty string.</returns>
    private static string GetFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.IsNullOrWhiteSpace(path) ? string.Empty : System.IO.Path.GetFileName(path);
    }
}
