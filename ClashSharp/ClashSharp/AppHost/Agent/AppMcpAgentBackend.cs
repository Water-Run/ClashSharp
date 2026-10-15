using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Hosting.Data;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Hosting.Agent;

/// <summary>Routes authenticated agent requests to generation-owned product transactions.</summary>
internal sealed class AppMcpAgentBackend(AppDataGenerationRepositories repositories, DataGenerationManager generations,
    MutationAdmissionBarrier admission, IRuntimeSettingsAuthority authority, INetworkStateObserver network,
    McpAccessState access, DateTimeOffset processStarted) : IMcpAgentBackend
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    // Completed write receipts contain only detached result metadata and argument hashes. They
    // prevent duplicate effects in this process; clients must reread state after a process restart.
    private readonly Dictionary<Guid, (string Hash, McpToolResponse Response)> _receipts = [];
    private readonly Queue<Guid> _receiptOrder = new();

    public McpAccessPolicy CapturePolicy() => access.Policy;
    public bool TryAuthenticate(string bearerToken, [NotNullWhen(true)] out McpClientIdentity? identity) => access.TryAuthenticate(bearerToken, out identity);
    public bool IsCurrentIdentity(McpClientIdentity identity) => access.IsCurrentIdentity(identity);

    public async Task<McpToolResponse> ExecuteAsync(McpClientIdentity identity, McpToolRequest request, CancellationToken cancellationToken)
    {
        McpToolDefinition definition = McpToolCatalog.All.Single(tool => tool.Id == request.Tool);
        if (!McpToolArgumentValidator.IsValid(definition, request.Arguments)) { return McpToolResponse.Error("mcp.invalid_arguments"); }
        JsonElement args = request.Arguments;
        bool exclusive = request.Tool == McpToolId.UpdateSettings && args.GetProperty("changes")
            .EnumerateObject().Any(change => change.Name == SettingsRegistry.Keys.TriggerNotificationsEnabled.Value);
        await using MutationAdmissionLease lease = exclusive
            ? await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false)
            : await admission.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RevocationToken);
        CancellationToken token = lifetime.Token;
        await using DataGenerationLease pin = await generations.AcquireAsync(token).ConfigureAwait(false);
        if (!pin.Descriptor.IsSameGeneration(repositories.Generation)) { return McpToolResponse.Error("mcp.generation_changed"); }
        if (!access.IsCurrentIdentity(identity) || !access.Policy.Allows(definition.RequiredScopes)) { return McpToolResponse.Error("mcp.access_denied"); }
        if (definition.IsReadOnly)
        {
            McpToolResponse read = await ExecuteReadAsync(request.Tool, args, token).ConfigureAwait(false);
            Audit(definition, identity, read);
            return read;
        }

        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!access.IsCurrentIdentity(identity) || !access.Policy.Allows(definition.RequiredScopes)) { return McpToolResponse.Error("mcp.access_denied"); }
            Guid operation = Guid.Parse(args.GetProperty("operation_id").GetString()!);
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Tool + ":" + args.GetRawText())));
            if (_receipts.TryGetValue(operation, out var receipt))
            {
                return StringComparer.Ordinal.Equals(receipt.Hash, hash) ? receipt.Response : McpToolResponse.Error("mcp.operation_id_conflict");
            }
            McpToolResponse result = await ExecuteWriteAsync(request.Tool, args, operation, lease, token).ConfigureAwait(false);
            Audit(definition, identity, result);
            _receipts.Add(operation, (hash, result));
            _receiptOrder.Enqueue(operation);
            if (_receiptOrder.Count > 128) { _receipts.Remove(_receiptOrder.Dequeue()); }
            return result;
        }
        finally { _writes.Release(); }
    }

    private async Task<McpToolResponse> ExecuteReadAsync(McpToolId tool, JsonElement args, CancellationToken token)
    {
        int Limit() => args.TryGetProperty("limit", out JsonElement limit) ? limit.GetInt32() : 25;
        int Offset() => args.TryGetProperty("offset", out JsonElement offset) ? offset.GetInt32() : 0;
        switch (tool)
        {
            case McpToolId.GetStatus:
                NetworkStateSnapshot state = await network.ObserveAsync(token).ConfigureAwait(false);
                return Success(new
                {
                    mode = state.Mode.ToString(),
                    known = state.IsKnown,
                    core_running = state.CoreRunning,
                    system_proxy = state.SystemProxyEnabled,
                    tun = state.TransparentProxyEnabled,
                    mixed_port = state.MixedPort,
                    capabilities = McpToolCatalog.All.Where(item => access.Policy.Allows(item.RequiredScopes)).Select(item => item.Name).ToArray()
                });
            case McpToolId.GetSettings:
                string[]? requested = args.TryGetProperty("keys", out JsonElement keys) ? keys.EnumerateArray().Select(key => key.GetString()!).ToArray() : null;
                SettingDefinition[] readable = SettingsRegistry.Default.Definitions.Where(item => !item.IsSensitive && item.ApplicationKind != SettingApplicationKind.McpServer).ToArray();
                if (requested is not null && requested.Any(key => readable.All(item => item.Key.Value != key))) { return McpToolResponse.Error("mcp.setting_unavailable"); }
                SettingsEnvelope envelope = repositories.Session.Snapshot;
                return Success(new
                {
                    settings = readable.Where(item => requested is null || requested.Contains(item.Key.Value, StringComparer.Ordinal))
                    .ToDictionary(item => item.Key.Value, item => new
                    {
                        desired = envelope.Desired[item.Key].Value.CanonicalText,
                        type = item.ValueType.Name,
                        applied = envelope.Applied[item.Key].Value?.CanonicalText,
                        applied_state = envelope.Applied[item.Key].Kind.ToString()
                    })
                });
            case McpToolId.ListProfiles:
                ConfigurationProfile[] profiles = repositories.Profiles.GetProfiles().ToArray();
                return Success(new { total = profiles.Length, profiles = profiles.Skip(Offset()).Take(Limit()).Select(ProfileMetadata).ToArray() });
            case McpToolId.ReadProfile:
            case McpToolId.ReadSensitiveProfile:
            case McpToolId.ReadProfileHistory:
            case McpToolId.ReadSensitiveProfileHistory:
                string profileId = args.GetProperty("profile_id").GetString()!;
                string? version = args.TryGetProperty("version_id", out JsonElement versionId) ? versionId.GetString() : null;
                if (profileId == "builtin-direct" && version is null)
                {
                    ConfigurationProfile builtIn = repositories.Profiles.GetProfiles().Single(profile => profile.Id == profileId);
                    return Success(new { profile = ProfileMetadata(builtIn), read_only = true });
                }
                string content = await repositories.Profiles.ReadAgentSourceAsync(profileId, version, token).ConfigureAwait(false);
                string sha256 = ProfileCatalogService.AgentSourceHash(content);
                if (tool is McpToolId.ReadSensitiveProfile or McpToolId.ReadSensitiveProfileHistory)
                {
                    return Success(new { profile_id = profileId, version_id = version, sha256, content });
                }
                int nodes = MihomoProfilePreviewParser.ParseNodes(content, code => new(code, code, code)).Count;
                int rules = MihomoProfilePreviewParser.ParseRules(content, key => key).Count;
                return Success(new { profile_id = profileId, version_id = version, sha256, node_count = nodes, rule_count = rules });
            case McpToolId.ListProfileHistory:
                ProfileHistoryEntry[] history = repositories.Profiles.GetProfileHistory(args.GetProperty("profile_id").GetString()!).ToArray();
                return Success(new
                {
                    total = history.Length,
                    versions = history.Skip(Offset()).Take(Limit()).Select(entry => new
                    {
                        version_id = entry.VersionId,
                        profile_id = entry.ProfileId,
                        created_at = entry.CreatedAt,
                        node_count = entry.NodeCount,
                        rule_count = entry.RuleCount,
                        sha256 = entry.ContentSha256,
                        outcome = entry.ApplyOutcome.ToString()
                    }).ToArray()
                });
            case McpToolId.GetTrafficHistory:
                string grouping = args.TryGetProperty("group_by", out JsonElement group) ? group.GetString()! : "day";
                IReadOnlyList<TrafficStatisticRow> rows = grouping switch
                {
                    "profile" => repositories.Logs.GetProfileTrafficRows(Limit()),
                    "node" => repositories.Logs.GetNodeTrafficRows(Limit()),
                    _ => repositories.Logs.GetDailyTrafficRows(Limit()),
                };
                return Success(new
                {
                    group_by = grouping,
                    rows = rows.Select(row => new
                    {
                        label = row.Label,
                        upload_bytes = row.UploadBytes,
                        download_bytes = row.DownloadBytes,
                        samples = row.SampleCount,
                        updated_at = row.UpdatedAt
                    }).ToArray()
                });
            case McpToolId.GetDiagnostics:
                // Free-form log messages may contain user URLs or credentials. Diagnostics exposes
                // bounded current-process severity metadata, never raw historical detail text.
                var diagnostics = repositories.Logs.GetLogs(1000).Where(log => log.CreatedAt >= processStarted).Take(Limit())
                    .Select(log => new { created_at = log.CreatedAt, level = log.Level }).ToArray();
                return Success(new { process_started_at = processStarted, diagnostics });
            default: return McpToolResponse.Error("mcp.operation_unavailable");
        }
    }

    private async Task<McpToolResponse> ExecuteWriteAsync(McpToolId tool, JsonElement args, Guid operation,
        MutationAdmissionLease lease, CancellationToken token)
    {
        switch (tool)
        {
            case McpToolId.UpdateSettings:
                List<SettingValueChange> changes = [];
                foreach (JsonProperty field in args.GetProperty("changes").EnumerateObject())
                {
                    if (!SettingsRegistry.Default.TryResolve(field.Name, out SettingDefinition? setting, out SettingKeyResolution resolution)
                        || setting is null || resolution != SettingKeyResolution.Canonical
                        || McpAccessRules.RequiredSettingWriteScopes(setting.Key) is not { } required || !access.Policy.Allows(required))
                    { return McpToolResponse.Error("mcp.setting_access_denied"); }
                    bool scalar = field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
                    if (!scalar) { return McpToolResponse.Error("mcp.invalid_setting_value"); }
                    string text = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText();
                    SettingNormalizationResult normalized = setting.Normalize(text);
                    if (!normalized.IsSuccess || setting.Key == SettingsRegistry.Keys.CurrentMode && normalized.Value!.Get<ClashSharpMode>() == ClashSharpMode.Faulted)
                    { return McpToolResponse.Error("mcp.invalid_setting_value"); }
                    changes.Add(new(setting.Key, normalized.Value!));
                }
                return SettingsResult(await authority.ApplyChangesAdmittedAsync(changes, operation, lease, token).ConfigureAwait(false));
            case McpToolId.SetProxyState:
                ClashSharpMode mode = Enum.Parse<ClashSharpMode>(args.GetProperty("mode").GetString()!);
                return SettingsResult(await authority.ApplyRuntimeChangesAdmittedAsync([new(SettingsRegistry.Keys.CurrentMode,
                    SettingsRegistry.Default.Get(SettingsRegistry.Keys.CurrentMode.Value).NormalizeValue(mode).Value!)], operation, lease, token).ConfigureAwait(false));
            case McpToolId.ActivateProfile:
                string profileId = args.GetProperty("profile_id").GetString()!;
                if (!repositories.Profiles.GetProfiles().Any(profile => profile.Id == profileId)) { return McpToolResponse.Error("mcp.profile_unavailable"); }
                bool applied = await repositories.Profiles.TryApplyActiveProfileAsync(profileId, token).ConfigureAwait(false);
                return applied ? Success(new { profile_id = profileId, applied = true }) : McpToolResponse.Error("mcp.application_failed");
            case McpToolId.SelectProxy:
                if (repositories.ProxySelections is null) { return McpToolResponse.Error("mcp.operation_unavailable"); }
                await repositories.ProxySelections.SelectAsync(args.GetProperty("group").GetString()!, args.GetProperty("node").GetString()!, token).ConfigureAwait(false);
                return Success(new { applied = true });
            case McpToolId.SaveProfile:
                ProfileImportResult saved = await repositories.Profiles.SaveAgentSourceAsync(operation,
                    args.TryGetProperty("profile_id", out JsonElement id) ? id.GetString() : null,
                    args.GetProperty("name").GetString()!, args.GetProperty("content").GetString()!,
                    args.TryGetProperty("expected_sha256", out JsonElement hash) ? hash.GetString() : null,
                    () => access.Policy.Allows(McpAccessRules.RequiredScopes(McpOperation.ModifyLiveConfiguration)), token).ConfigureAwait(false);
                return Success(new { profile_id = saved.ProfileId, name = saved.ProfileName, node_count = saved.NodeCount, rule_count = saved.RuleCount });
            default: return McpToolResponse.Error("mcp.operation_unavailable");
        }
    }

    private static object ProfileMetadata(ConfigurationProfile profile) => new
    {
        profile_id = profile.Id,
        name = profile.Name,
        node_count = profile.NodeCount,
        rule_count = profile.RuleCount,
        active = profile.IsActive,
        updated_at = profile.UpdatedAt
    };

    private void Audit(McpToolDefinition tool, McpClientIdentity identity, McpToolResponse result)
    {
        try
        {
            repositories.Logs.AppendLog(result.IsError ? "Warning" : "Info", "Mcp", tool.Name,
                $"Client: {identity.CredentialId:N}; Outcome: {(result.IsError ? "rejected" : "completed")}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            // A diagnostic storage failure cannot undo a committed product transaction. No request,
            // response, file path, bearer secret or original exception text enters this fallback.
            System.Diagnostics.Debug.WriteLine("MCP audit unavailable: " + failure.GetType().Name);
        }
    }

    private static McpToolResponse SettingsResult(SettingsAuthorityResult result) => Success(new
    { succeeded = result.IsSucceeded, status = result.Status.ToString(), code = result.Code, revision = result.Envelope?.EnvelopeRevision }, !result.IsSucceeded);

    private static McpToolResponse Success(object content, bool isError = false) => new(isError, JsonSerializer.SerializeToElement(content));
}
