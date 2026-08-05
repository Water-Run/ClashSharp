using System.Globalization;
using System.Net;
using System.Text.Json;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.MihomoService;

internal sealed record MihomoServiceControllerBrokerPayload
{
    internal MihomoServiceIpcEffectiveConfiguration? EffectiveConfiguration { get; init; }

    internal MihomoServiceIpcConnectionSnapshot? ConnectionSnapshot { get; init; }

    internal MihomoServiceIpcProxyRuntimeSnapshot? ProxyRuntimeSnapshot { get; init; }

    internal MihomoServiceIpcRuntimeLogSnapshot? RuntimeLogSnapshot { get; init; }
}

internal sealed record MihomoServiceControllerBrokerResult(
    MihomoServiceControllerBrokerPayload? Payload,
    string? ErrorCode,
    MihomoServiceIpcSnapshot Snapshot)
{
    internal bool Succeeded => ErrorCode is null;
}

/// <summary>Executes the protocol's fixed typed controller capability allowlist.</summary>
internal sealed class MihomoServiceControllerBroker
{
    private const int MaximumJsonNodes = 100_000;
    private readonly MihomoChildSupervisor _supervisor;
    private readonly IMihomoControllerTransportFactory _transportFactory;
    private readonly MihomoRuntimeLogBuffer _runtimeLogs;
    private readonly MihomoServiceLogBuffer _serviceLogs;

    internal MihomoServiceControllerBroker(
        MihomoChildSupervisor supervisor,
        IMihomoControllerTransportFactory transportFactory,
        MihomoRuntimeLogBuffer runtimeLogs,
        MihomoServiceLogBuffer serviceLogs)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _transportFactory = transportFactory
            ?? throw new ArgumentNullException(nameof(transportFactory));
        _runtimeLogs = runtimeLogs ?? throw new ArgumentNullException(nameof(runtimeLogs));
        _serviceLogs = serviceLogs ?? throw new ArgumentNullException(nameof(serviceLogs));
    }

    internal async Task<MihomoServiceControllerBrokerResult> ExecuteAsync(
        MihomoServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRuntime is null)
        {
            return new MihomoServiceControllerBrokerResult(
                null,
                "service.controller.binding_invalid",
                _supervisor.GetSnapshot());
        }

        try
        {
            MihomoControllerBoundOperationResult<MihomoServiceControllerBrokerPayload> result =
                await _supervisor.ExecuteControllerOperationAsync(
                        request.ExpectedRuntime,
                        (context, token) => ExecuteBoundAsync(request, context, token),
                        cancellationToken)
                    .ConfigureAwait(false);
            if (result.Failure is not null)
            {
                return MapFailure(result.Failure, result.Snapshot);
            }

            return result.ErrorCode is null
                ? new MihomoServiceControllerBrokerResult(result.Value!, null, result.Snapshot)
                : new MihomoServiceControllerBrokerResult(null, result.ErrorCode, result.Snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MihomoControllerUpstreamException exception)
        {
            return new MihomoServiceControllerBrokerResult(
                null,
                exception.ErrorCode,
                _supervisor.GetSnapshot());
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or FormatException)
        {
            _serviceLogs.Append(
                "broker",
                $"Controller payload rejected ({exception.GetType().Name}).");
            return new MihomoServiceControllerBrokerResult(
                null,
                "service.controller.upstream_invalid",
                _supervisor.GetSnapshot());
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or InvalidOperationException)
        {
            _serviceLogs.Append(
                "broker",
                $"Controller request failed ({exception.GetType().Name}).");
            return new MihomoServiceControllerBrokerResult(
                null,
                ContainsServerIdentityFailure(exception)
                    ? "service.controller.server_identity_invalid"
                    : "service.controller.request_failed",
                _supervisor.GetSnapshot());
        }
    }

    private MihomoServiceControllerBrokerResult MapFailure(
        Exception exception,
        MihomoServiceIpcSnapshot snapshot)
    {
        if (exception is MihomoControllerUpstreamException upstream)
        {
            return new MihomoServiceControllerBrokerResult(null, upstream.ErrorCode, snapshot);
        }

        if (exception is JsonException or InvalidDataException or FormatException)
        {
            _serviceLogs.Append(
                "broker",
                $"Controller payload rejected ({exception.GetType().Name}).");
            return new MihomoServiceControllerBrokerResult(
                null,
                "service.controller.upstream_invalid",
                snapshot);
        }

        _serviceLogs.Append(
            "broker",
            $"Controller request failed ({exception.GetType().Name}).");
        return new MihomoServiceControllerBrokerResult(
            null,
            ContainsServerIdentityFailure(exception)
                ? "service.controller.server_identity_invalid"
                : "service.controller.request_failed",
            snapshot);
    }

    private async Task<MihomoServiceControllerBrokerPayload> ExecuteBoundAsync(
        MihomoServiceIpcRequest request,
        MihomoControllerRuntimeContext context,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case MihomoServiceIpcCommand.ProbeEffectiveConfiguration:
                return new MihomoServiceControllerBrokerPayload
                {
                    EffectiveConfiguration = context.EffectiveConfiguration,
                };
            case MihomoServiceIpcCommand.GetRuntimeLogs:
                MihomoServiceIpcRuntimeLogQuery query = request.RuntimeLogQuery!;
                return new MihomoServiceControllerBrokerPayload
                {
                    RuntimeLogSnapshot = _runtimeLogs.ReadAfter(
                        query.AfterSequence,
                        query.MaximumEntries),
                };
        }

        await using IMihomoControllerTransport transport = _transportFactory.Create(
            context.Authority,
            context.ProcessId);
        return request.Command switch
        {
            MihomoServiceIpcCommand.GetConnections =>
                new MihomoServiceControllerBrokerPayload
                {
                    ConnectionSnapshot = await GetConnectionsAsync(
                            transport,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
            MihomoServiceIpcCommand.CloseConnection => await CloseConnectionAsync(
                    transport,
                    request.ConnectionId!,
                    cancellationToken)
                .ConfigureAwait(false),
            MihomoServiceIpcCommand.CloseAllConnections => await CloseAllConnectionsAsync(
                    transport,
                    cancellationToken)
                .ConfigureAwait(false),
            MihomoServiceIpcCommand.GetProxyRuntimeSnapshot =>
                new MihomoServiceControllerBrokerPayload
                {
                    ProxyRuntimeSnapshot = await GetProxyRuntimeAsync(
                            transport,
                            cancellationToken)
                        .ConfigureAwait(false),
                },
            MihomoServiceIpcCommand.SelectProxy => await SelectProxyAsync(
                    transport,
                    request.ProxySelection!,
                    cancellationToken)
                .ConfigureAwait(false),
            MihomoServiceIpcCommand.UpdateProvider => await UpdateProviderAsync(
                    transport,
                    request.ProviderUpdate!,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new MihomoControllerUpstreamException(
                "service.controller.operation_not_supported"),
        };
    }

    private static async Task<MihomoServiceControllerBrokerPayload> UpdateProviderAsync(
        IMihomoControllerTransport transport,
        MihomoServiceIpcProviderUpdate update,
        CancellationToken cancellationToken)
    {
        EnsureIdentifier(update.Name, "provider name");
        string providerRoot = update.Kind switch
        {
            MihomoServiceIpcProviderKind.Proxy => "/providers/proxies/",
            MihomoServiceIpcProviderKind.Rule => "/providers/rules/",
            _ => throw new MihomoControllerUpstreamException(
                "service.controller.provider_update_invalid"),
        };
        _ = await SendExpectedAsync(
                transport,
                HttpMethod.Put,
                providerRoot + Uri.EscapeDataString(update.Name),
                null,
                maximumResponseBytes: 8 * 1024,
                HttpStatusCode.NoContent,
                cancellationToken)
            .ConfigureAwait(false);
        return new MihomoServiceControllerBrokerPayload();
    }

    private static async Task<MihomoServiceIpcConnectionSnapshot> GetConnectionsAsync(
        IMihomoControllerTransport transport,
        CancellationToken cancellationToken)
    {
        MihomoControllerHttpResponse response = await SendExpectedAsync(
                transport,
                HttpMethod.Get,
                "/connections",
                null,
                maximumResponseBytes: 4 * 1024 * 1024,
                HttpStatusCode.OK,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = ParseStrictJson(response.Content);
        JsonElement root = RequireObject(document.RootElement, "connections response");
        if (!root.TryGetProperty("connections", out JsonElement rows)
            || rows.ValueKind != JsonValueKind.Array
            || rows.GetArrayLength() > MihomoServiceIpcProtocol.MaximumControllerConnections)
        {
            throw new InvalidDataException("The connections response is invalid.");
        }

        List<MihomoServiceIpcConnection> connections = new(rows.GetArrayLength());
        foreach (JsonElement row in rows.EnumerateArray())
        {
            connections.Add(ParseConnection(RequireObject(row, "connection")));
        }

        MihomoServiceIpcConnectionSnapshot snapshot = new()
        {
            Connections = connections,
        };
        EnsureValid(snapshot.Validate());
        return snapshot;
    }

    private static async Task<MihomoServiceControllerBrokerPayload> CloseConnectionAsync(
        IMihomoControllerTransport transport,
        string connectionId,
        CancellationToken cancellationToken)
    {
        EnsureIdentifier(connectionId, "connection id");
        _ = await SendExpectedAsync(
                transport,
                HttpMethod.Delete,
                "/connections/" + Uri.EscapeDataString(connectionId),
                null,
                maximumResponseBytes: 8 * 1024,
                HttpStatusCode.NoContent,
                cancellationToken)
            .ConfigureAwait(false);
        return new MihomoServiceControllerBrokerPayload();
    }

    private static async Task<MihomoServiceControllerBrokerPayload> CloseAllConnectionsAsync(
        IMihomoControllerTransport transport,
        CancellationToken cancellationToken)
    {
        _ = await SendExpectedAsync(
                transport,
                HttpMethod.Delete,
                "/connections",
                null,
                maximumResponseBytes: 8 * 1024,
                HttpStatusCode.NoContent,
                cancellationToken)
            .ConfigureAwait(false);
        return new MihomoServiceControllerBrokerPayload();
    }

    private static async Task<MihomoServiceIpcProxyRuntimeSnapshot> GetProxyRuntimeAsync(
        IMihomoControllerTransport transport,
        CancellationToken cancellationToken)
    {
        MihomoServiceIpcProxyGroup[] groups = await GetProxyGroupsAsync(
                transport,
                cancellationToken)
            .ConfigureAwait(false);
        MihomoServiceIpcProvider[] proxyProviders = await GetProvidersAsync(
                transport,
                "/providers/proxies",
                MihomoServiceIpcProviderKind.Proxy,
                cancellationToken)
            .ConfigureAwait(false);
        MihomoServiceIpcProvider[] ruleProviders = await GetProvidersAsync(
                transport,
                "/providers/rules",
                MihomoServiceIpcProviderKind.Rule,
                cancellationToken)
            .ConfigureAwait(false);
        MihomoServiceIpcProvider[] providers = [.. proxyProviders, .. ruleProviders];
        MihomoServiceIpcProxyRuntimeSnapshot snapshot = new()
        {
            Groups = groups,
            Providers = providers,
        };
        EnsureValid(snapshot.Validate());
        return snapshot;
    }

    private static async Task<MihomoServiceControllerBrokerPayload> SelectProxyAsync(
        IMihomoControllerTransport transport,
        MihomoServiceIpcProxySelection selection,
        CancellationToken cancellationToken)
    {
        EnsureIdentifier(selection.GroupName, "proxy group");
        EnsureIdentifier(selection.ProxyName, "proxy candidate");
        MihomoServiceIpcProxyGroup[] groups = await GetProxyGroupsAsync(
                transport,
                cancellationToken)
            .ConfigureAwait(false);
        MihomoServiceIpcProxyGroup? group = groups.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, selection.GroupName, StringComparison.Ordinal));
        if (group is null
            || !group.Candidates.Contains(selection.ProxyName, StringComparer.Ordinal))
        {
            throw new MihomoControllerUpstreamException(
                "service.controller.proxy_selection_invalid");
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["name"] = selection.ProxyName,
        });
        _ = await SendExpectedAsync(
                transport,
                HttpMethod.Put,
                "/proxies/" + Uri.EscapeDataString(selection.GroupName),
                body,
                maximumResponseBytes: 8 * 1024,
                HttpStatusCode.NoContent,
                cancellationToken)
            .ConfigureAwait(false);
        return new MihomoServiceControllerBrokerPayload();
    }

    private static async Task<MihomoServiceIpcProxyGroup[]> GetProxyGroupsAsync(
        IMihomoControllerTransport transport,
        CancellationToken cancellationToken)
    {
        MihomoControllerHttpResponse response = await SendExpectedAsync(
                transport,
                HttpMethod.Get,
                "/proxies",
                null,
                maximumResponseBytes: 8 * 1024 * 1024,
                HttpStatusCode.OK,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = ParseStrictJson(response.Content);
        JsonElement root = RequireObject(document.RootElement, "proxies response");
        if (!root.TryGetProperty("proxies", out JsonElement proxies)
            || proxies.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The proxies response is invalid.");
        }

        List<MihomoServiceIpcProxyGroup> groups = [];
        foreach (JsonProperty property in proxies.EnumerateObject())
        {
            JsonElement proxy = RequireObject(property.Value, "proxy");
            if (!proxy.TryGetProperty("all", out JsonElement candidatesElement))
            {
                continue;
            }

            if (candidatesElement.ValueKind != JsonValueKind.Array
                || candidatesElement.GetArrayLength() is < 1
                    or > MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup)
            {
                throw new InvalidDataException("A proxy group candidate list is invalid.");
            }

            if (groups.Count >= MihomoServiceIpcProtocol.MaximumControllerProxyGroups)
            {
                throw new InvalidDataException("The proxy group count exceeds its safety limit.");
            }

            string name = FirstNonEmpty(ReadString(proxy, "name"), property.Name);
            EnsureIdentifier(name, "proxy group name");
            string type = FirstNonEmpty(ReadString(proxy, "type"), "Unknown");
            EnsureIdentifier(type, "proxy group type");
            List<string> candidates = new(candidatesElement.GetArrayLength());
            foreach (JsonElement candidateElement in candidatesElement.EnumerateArray())
            {
                string candidate = candidateElement.ValueKind == JsonValueKind.String
                    ? candidateElement.GetString() ?? string.Empty
                    : string.Empty;
                EnsureIdentifier(candidate, "proxy candidate");
                if (!candidates.Contains(candidate, StringComparer.Ordinal))
                {
                    candidates.Add(candidate);
                }
            }

            if (candidates.Count == 0)
            {
                throw new InvalidDataException("A proxy group has no unique candidates.");
            }

            string current = ReadString(proxy, "now");
            if (!candidates.Contains(current, StringComparer.Ordinal))
            {
                current = candidates[0];
            }

            groups.Add(new MihomoServiceIpcProxyGroup
            {
                Name = name,
                Type = type,
                CurrentSelection = current,
                Candidates = candidates,
            });
        }

        if (groups.Sum(group => group.Candidates.Count) + groups.Count
            > MihomoServiceIpcProtocol.MaximumControllerAggregateItems)
        {
            throw new InvalidDataException("The proxy group aggregate exceeds its safety limit.");
        }

        return groups.ToArray();
    }

    private static async Task<MihomoServiceIpcProvider[]> GetProvidersAsync(
        IMihomoControllerTransport transport,
        string path,
        MihomoServiceIpcProviderKind kind,
        CancellationToken cancellationToken)
    {
        MihomoControllerHttpResponse response = await SendExpectedAsync(
                transport,
                HttpMethod.Get,
                path,
                null,
                maximumResponseBytes: 8 * 1024 * 1024,
                HttpStatusCode.OK,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = ParseStrictJson(response.Content);
        JsonElement root = RequireObject(document.RootElement, "providers response");
        if (!root.TryGetProperty("providers", out JsonElement providers)
            || providers.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The providers response is invalid.");
        }

        List<MihomoServiceIpcProvider> result = [];
        foreach (JsonProperty property in providers.EnumerateObject())
        {
            if (result.Count >= MihomoServiceIpcProtocol.MaximumControllerProviders)
            {
                throw new InvalidDataException("The provider count exceeds its safety limit.");
            }

            JsonElement provider = RequireObject(property.Value, "provider");
            string name = FirstNonEmpty(ReadString(provider, "name"), property.Name);
            EnsureIdentifier(name, "provider name");
            result.Add(new MihomoServiceIpcProvider
            {
                Name = name,
                Kind = kind,
                VehicleType = NormalizeText(ReadString(provider, "vehicleType")),
                Behavior = NormalizeText(ReadString(provider, "behavior")),
                ItemCount = ReadProviderItemCount(provider, kind),
                UpdatedAt = ReadOptionalTimestamp(provider, "updatedAt"),
            });
        }

        return result.ToArray();
    }

    private static MihomoServiceIpcConnection ParseConnection(JsonElement connection)
    {
        string id = ReadString(connection, "id");
        EnsureIdentifier(id, "connection id");
        JsonElement metadata = connection.TryGetProperty("metadata", out JsonElement metadataValue)
            && metadataValue.ValueKind == JsonValueKind.Object
                ? metadataValue
                : default;
        string processCandidate = FirstNonEmpty(
            ReadString(metadata, "process"),
            ReadString(metadata, "processPath"),
            ReadString(connection, "process"),
            string.Empty);
        string processName = FirstNonEmpty(Path.GetFileName(processCandidate), "unknown");
        string host = FirstNonEmpty(
            ReadString(metadata, "host"),
            ReadString(metadata, "destinationIP"),
            ReadString(metadata, "remoteDestination"),
            ReadString(connection, "host"),
            "unknown");
        DateTimeOffset startedAt = ReadRequiredTimestamp(connection, "start");
        return new MihomoServiceIpcConnection
        {
            Id = id,
            ProcessName = NormalizeText(processName),
            Host = NormalizeText(host),
            RuleName = NormalizeText(FirstNonEmpty(ReadString(connection, "rule"), "MATCH")),
            RulePayload = NormalizeText(ReadString(connection, "rulePayload")),
            ProxyName = NormalizeText(ReadProxyChain(connection)),
            UploadBytes = Math.Max(0, ReadInt64(connection, "upload")),
            DownloadBytes = Math.Max(0, ReadInt64(connection, "download")),
            StartedAt = startedAt,
        };
    }

    private static string ReadProxyChain(JsonElement connection)
    {
        if (!connection.TryGetProperty("chains", out JsonElement chains))
        {
            return FirstNonEmpty(ReadString(connection, "chain"), "DIRECT");
        }

        if (chains.ValueKind != JsonValueKind.Array || chains.GetArrayLength() > 64)
        {
            throw new InvalidDataException("The connection chain is invalid.");
        }

        List<string> values = [];
        foreach (JsonElement element in chains.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String
                && element.GetString() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values.Count == 0 ? "DIRECT" : string.Join(" / ", values);
    }

    private static int ReadProviderItemCount(
        JsonElement provider,
        MihomoServiceIpcProviderKind kind)
    {
        if (kind == MihomoServiceIpcProviderKind.Proxy
            && provider.TryGetProperty("proxies", out JsonElement proxies)
            && proxies.ValueKind == JsonValueKind.Array)
        {
            return proxies.GetArrayLength();
        }

        if (provider.TryGetProperty("ruleCount", out JsonElement ruleCount)
            && ruleCount.ValueKind == JsonValueKind.Number
            && ruleCount.TryGetInt32(out int count))
        {
            return Math.Max(0, count);
        }

        return provider.TryGetProperty("rules", out JsonElement rules)
            && rules.ValueKind == JsonValueKind.Array
                ? rules.GetArrayLength()
                : 0;
    }

    private static async Task<MihomoControllerHttpResponse> SendExpectedAsync(
        IMihomoControllerTransport transport,
        HttpMethod method,
        string path,
        ReadOnlyMemory<byte>? body,
        int maximumResponseBytes,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        MihomoControllerHttpResponse response = await transport.SendAsync(
                method,
                path,
                body,
                maximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != expectedStatus)
        {
            throw new MihomoControllerUpstreamException(
                "service.controller.upstream_status");
        }

        return response;
    }

    private static JsonDocument ParseStrictJson(ReadOnlyMemory<byte> content)
    {
        JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        try
        {
            int nodeCount = 0;
            EnsureUniqueProperties(document.RootElement, ref nodeCount);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void EnsureUniqueProperties(JsonElement element, ref int nodeCount)
    {
        nodeCount++;
        if (nodeCount > MaximumJsonNodes)
        {
            throw new InvalidDataException("The controller JSON is too structurally complex.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("The controller JSON contains duplicate properties.");
                }

                EnsureUniqueProperties(property.Value, ref nodeCount);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                EnsureUniqueProperties(child, ref nodeCount);
            }
        }
    }

    private static JsonElement RequireObject(JsonElement element, string description)
    {
        return element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException($"The controller {description} is not an object.");
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long result)
                ? result
                : 0;
    }

    private static DateTimeOffset ReadRequiredTimestamp(
        JsonElement element,
        string propertyName)
    {
        string value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp)
                ? timestamp
                : throw new InvalidDataException("A controller timestamp is invalid.");
    }

    private static DateTimeOffset? ReadOptionalTimestamp(
        JsonElement element,
        string propertyName)
    {
        string value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp)
                ? timestamp
                : null;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizeText(string value)
    {
        int maximum = MihomoServiceIpcProtocol.MaximumControllerTextCharacters;
        int capacity = Math.Min(value.Length, maximum);
        Span<char> normalized = capacity <= 1024
            ? stackalloc char[capacity]
            : new char[capacity];
        int sourceIndex = 0;
        int destinationIndex = 0;
        while (sourceIndex < value.Length && destinationIndex < capacity)
        {
            char character = value[sourceIndex++];
            if (char.IsHighSurrogate(character))
            {
                if (sourceIndex < value.Length && char.IsLowSurrogate(value[sourceIndex]))
                {
                    if (destinationIndex + 1 >= capacity)
                    {
                        break;
                    }

                    normalized[destinationIndex++] = character;
                    normalized[destinationIndex++] = value[sourceIndex++];
                }
                else
                {
                    normalized[destinationIndex++] = '\uFFFD';
                }

                continue;
            }

            normalized[destinationIndex++] = char.IsLowSurrogate(character)
                ? '\uFFFD'
                : char.IsControl(character) ? ' ' : character;
        }

        return new string(normalized[..destinationIndex]).Trim();
    }

    private static void EnsureIdentifier(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MihomoServiceIpcProtocol.MaximumControllerIdentifierCharacters
            || value.Any(char.IsControl)
            || !IsWellFormedUtf16(value))
        {
            throw new InvalidDataException($"The controller {description} is invalid.");
        }
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureValid(string? validationError)
    {
        if (validationError is not null)
        {
            throw new InvalidDataException(
                $"The typed controller projection is invalid ({validationError}).");
        }
    }

    private static bool ContainsServerIdentityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MihomoControllerServerIdentityException)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class MihomoControllerUpstreamException : IOException
{
    internal MihomoControllerUpstreamException(string errorCode)
        : base("A typed controller operation failed.")
    {
        ErrorCode = errorCode;
    }

    internal string ErrorCode { get; }
}
