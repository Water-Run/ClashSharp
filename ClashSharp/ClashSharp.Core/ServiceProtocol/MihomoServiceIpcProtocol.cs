using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Diagnostics;

namespace ClashSharp.ServiceProtocol;

/// <summary>Defines the versioned local IPC contract between Clash# and its mihomo Windows service.</summary>
public static class MihomoServiceIpcProtocol
{
    /// <summary>Gets the only protocol version accepted by this build.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Gets the maximum encoded request or response size.</summary>
    public const int MaximumFrameBytes = 1024 * 1024;

    /// <summary>Gets the maximum number of service log entries returned by one request.</summary>
    public const int MaximumLogEntries = 256;

    /// <summary>Gets the maximum character count retained for one service log entry.</summary>
    public const int MaximumLogEntryCharacters = 4096;

    /// <summary>Gets the maximum character count accepted for one controller identifier or selector.</summary>
    public const int MaximumControllerIdentifierCharacters = 512;

    /// <summary>Gets the maximum character count accepted for one controller text field.</summary>
    public const int MaximumControllerTextCharacters = 4096;

    /// <summary>Gets the maximum number of active connections returned by one broker request.</summary>
    public const int MaximumControllerConnections = 2048;

    /// <summary>Gets the maximum number of proxy groups returned by one broker request.</summary>
    public const int MaximumControllerProxyGroups = 256;

    /// <summary>Gets the maximum number of candidates returned for one proxy group.</summary>
    public const int MaximumControllerCandidatesPerGroup = 1024;

    /// <summary>Gets the maximum number of providers returned by one broker request.</summary>
    public const int MaximumControllerProviders = 512;

    /// <summary>Gets the maximum aggregate item count in one controller broker payload.</summary>
    public const int MaximumControllerAggregateItems = 4096;

    /// <summary>Gets the maximum aggregate character count in one controller broker payload.</summary>
    public const int MaximumControllerAggregateCharacters = 256 * 1024;

    /// <summary>Gets the maximum number of typed runtime log entries returned by one broker request.</summary>
    public const int MaximumRuntimeLogEntries = 256;

    /// <summary>Gets the maximum character count accepted for one typed runtime log message.</summary>
    public const int MaximumRuntimeLogMessageCharacters = RuntimeLogText.MaximumCharacters;

    /// <summary>
    /// Normalizes untrusted runtime-log text to the same bounded, single-line-safe contract on
    /// both the App-owned WebSocket and Service-owned IPC paths.
    /// </summary>
    public static string NormalizeRuntimeLogMessage(string message)
    {
        return RuntimeLogText.Normalize(message);
    }

    private const string PipePrefix = "ClashSharp.Mihomo.";

    /// <summary>Builds a stable owner/deployment-specific pipe name without exposing the SID or token.</summary>
    /// <param name="userSid">Canonical Windows user SID authorized by the service pipe ACL.</param>
    /// <param name="authenticationToken">Deployment-scoped 256-bit authentication token.</param>
    /// <returns>A stable pipe name scoped to the supplied SID.</returns>
    public static string BuildPipeName(string userSid, string authenticationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (!IsCanonicalSha256(authenticationToken))
        {
            throw new ArgumentException(
                "The service IPC authentication token must be canonical lowercase SHA-256 text.",
                nameof(authenticationToken));
        }

        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"ClashSharp.Mihomo.IPC\0{userSid}\0{authenticationToken}"));
        return PipePrefix + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>Returns whether a token or content hash is canonical lowercase SHA-256 text.</summary>
    /// <param name="value">Candidate text.</param>
    /// <returns>True for exactly 64 lowercase hexadecimal characters.</returns>
    public static bool IsCanonicalSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(static character =>
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }

    internal static bool IsBoundedRequiredIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaximumControllerIdentifierCharacters
            && !value.Any(char.IsControl);
    }

    internal static bool IsBoundedControllerText(string? value)
    {
        return value is not null
            && value.Length <= MaximumControllerTextCharacters
            && !value.Any(char.IsControl);
    }
}

/// <summary>Identifies one authenticated service operation.</summary>
public enum MihomoServiceIpcCommand
{
    /// <summary>Negotiates protocol version and observes the service session.</summary>
    Hello = 0,

    /// <summary>Returns the current service-child state.</summary>
    Status = 1,

    /// <summary>Starts the service child for an exact runtime generation and hash.</summary>
    Start = 2,

    /// <summary>Restarts the service child on an exact runtime generation and hash.</summary>
    Reload = 3,

    /// <summary>Stops the service child while leaving the service host available.</summary>
    Stop = 4,

    /// <summary>Returns a bounded snapshot of service startup/runtime log entries.</summary>
    Logs = 5,

    /// <summary>Reads the effective routing mode, TUN state, and mixed port from mihomo.</summary>
    ProbeEffectiveConfiguration = 6,

    /// <summary>Returns a bounded typed snapshot of active mihomo connections.</summary>
    GetConnections = 7,

    /// <summary>Closes one active connection identified by a typed bounded identifier.</summary>
    CloseConnection = 8,

    /// <summary>Closes every active mihomo connection.</summary>
    CloseAllConnections = 9,

    /// <summary>Returns bounded typed proxy-group and provider snapshots.</summary>
    GetProxyRuntimeSnapshot = 10,

    /// <summary>Selects one proxy in one proxy group.</summary>
    SelectProxy = 11,

    /// <summary>Polls the service-owned bounded ring of typed mihomo runtime logs.</summary>
    GetRuntimeLogs = 12,

    /// <summary>Requests an update of one named proxy or rule provider.</summary>
    UpdateProvider = 13,
}

/// <summary>Classifies the service-owned mihomo child lifecycle.</summary>
public enum MihomoServiceChildState
{
    /// <summary>No service-owned child exists.</summary>
    Stopped = 0,

    /// <summary>A child is being created and bound to its Job Object.</summary>
    Starting = 1,

    /// <summary>The service owns a running child.</summary>
    Running = 2,

    /// <summary>The service is stopping its child and has not released ownership yet.</summary>
    Stopping = 3,

    /// <summary>The last lifecycle operation failed and ownership cannot be claimed as ready.</summary>
    Faulted = 4,
}

/// <summary>Classifies the effective mihomo routing mode exposed by the controller.</summary>
public enum MihomoServiceIpcRoutingMode
{
    /// <summary>Routes traffic directly.</summary>
    Direct = 0,

    /// <summary>Routes traffic according to the active rule set.</summary>
    Rule = 1,

    /// <summary>Routes eligible traffic through the selected global proxy.</summary>
    Global = 2,
}

/// <summary>Identifies the controller namespace that owns one provider resource.</summary>
public enum MihomoServiceIpcProviderKind
{
    /// <summary>A proxy-provider resource.</summary>
    Proxy = 0,

    /// <summary>A rule-provider resource.</summary>
    Rule = 1,
}

/// <summary>Classifies one normalized mihomo runtime log entry.</summary>
public enum MihomoServiceIpcRuntimeLogLevel
{
    /// <summary>Diagnostic detail.</summary>
    Debug = 0,

    /// <summary>Normal operational information.</summary>
    Information = 1,

    /// <summary>A recoverable warning.</summary>
    Warning = 2,

    /// <summary>An error or fatal runtime event.</summary>
    Error = 3,
}

/// <summary>Binds one broker operation to the exact service-owned mihomo runtime.</summary>
public sealed record MihomoServiceIpcControllerBinding
{
    /// <summary>Gets the expected service-host process session.</summary>
    public Guid ServiceSessionId { get; init; }

    /// <summary>Gets the expected active runtime generation.</summary>
    public long Generation { get; init; }

    /// <summary>Gets the expected lowercase SHA-256 runtime configuration hash.</summary>
    public string ConfigurationHash { get; init; } = string.Empty;

    /// <summary>Returns a stable validation error code, or null for an exact runtime binding.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        return ServiceSessionId == Guid.Empty
            || Generation < 1
            || !MihomoServiceIpcProtocol.IsCanonicalSha256(ConfigurationHash)
                ? "service.ipc.controller_binding_invalid"
                : null;
    }
}

/// <summary>Identifies one bounded proxy selection without exposing an arbitrary request body.</summary>
public sealed record MihomoServiceIpcProxySelection
{
    /// <summary>Gets the exact proxy-group name.</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>Gets the exact candidate proxy name.</summary>
    public string ProxyName { get; init; } = string.Empty;

    /// <summary>Returns a stable validation error code, or null for a bounded selection.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        return !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(GroupName)
            || !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(ProxyName)
                ? "service.ipc.proxy_selection_invalid"
                : null;
    }
}

/// <summary>Identifies one provider update without exposing a URL or controller path.</summary>
public sealed record MihomoServiceIpcProviderUpdate
{
    /// <summary>Gets the controller provider namespace.</summary>
    public MihomoServiceIpcProviderKind Kind { get; init; }

    /// <summary>Gets the exact provider name from the active configuration.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Returns a stable validation error code, or null for a bounded update.</summary>
    public string? Validate()
    {
        return !Enum.IsDefined(Kind)
            || !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(Name)
                ? "service.ipc.provider_update_invalid"
                : null;
    }
}

/// <summary>Defines one bounded cursor-based poll of service-owned mihomo runtime logs.</summary>
public sealed record MihomoServiceIpcRuntimeLogQuery
{
    /// <summary>Gets the last sequence already observed by the caller, or zero for the beginning.</summary>
    public long AfterSequence { get; init; }

    /// <summary>Gets the maximum number of later entries requested.</summary>
    public int MaximumEntries { get; init; }

    /// <summary>Returns a stable validation error code, or null for a bounded log query.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        return AfterSequence < 0
            || MaximumEntries is < 1 or > MihomoServiceIpcProtocol.MaximumRuntimeLogEntries
                ? "service.ipc.runtime_log_query_invalid"
                : null;
    }
}

/// <summary>Contains one framed authenticated request sent to the mihomo service.</summary>
public sealed record MihomoServiceIpcRequest
{
    /// <summary>Gets the caller protocol version.</summary>
    public int ProtocolVersion { get; init; }

    /// <summary>Gets the nonempty correlation identity.</summary>
    public Guid RequestId { get; init; }

    /// <summary>Gets the deployment-scoped 256-bit authentication token.</summary>
    public string AuthenticationToken { get; init; } = string.Empty;

    /// <summary>Gets the requested operation.</summary>
    public MihomoServiceIpcCommand Command { get; init; }

    /// <summary>Gets the exact runtime generation for start/reload.</summary>
    public long? Generation { get; init; }

    /// <summary>Gets the exact lowercase SHA-256 configuration hash for start/reload.</summary>
    public string? ConfigurationHash { get; init; }

    /// <summary>Gets the requested bounded service log count.</summary>
    public int? MaximumLogEntries { get; init; }

    /// <summary>Gets the expected service session, generation, and hash for a controller broker command.</summary>
    public MihomoServiceIpcControllerBinding? ExpectedRuntime { get; init; }

    /// <summary>Gets the exact active connection id for a close-one command.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>Gets the bounded proxy selection for a select command.</summary>
    public MihomoServiceIpcProxySelection? ProxySelection { get; init; }

    /// <summary>Gets the cursor and count for a typed runtime-log poll.</summary>
    public MihomoServiceIpcRuntimeLogQuery? RuntimeLogQuery { get; init; }

    /// <summary>Gets the typed provider update requested from the active runtime.</summary>
    public MihomoServiceIpcProviderUpdate? ProviderUpdate { get; init; }

    /// <summary>Returns a stable validation error code, or null for a structurally valid request.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        if (ProtocolVersion <= 0)
        {
            return "service.ipc.protocol_version_invalid";
        }

        if (RequestId == Guid.Empty)
        {
            return "service.ipc.request_id_invalid";
        }

        if (!MihomoServiceIpcProtocol.IsCanonicalSha256(AuthenticationToken))
        {
            return "service.ipc.authentication_token_invalid";
        }

        if (!Enum.IsDefined(Command))
        {
            return "service.ipc.command_invalid";
        }

        bool requiresGeneration = Command is MihomoServiceIpcCommand.Start
            or MihomoServiceIpcCommand.Reload;
        if (requiresGeneration
            && (Generation is null or < 1
                || !MihomoServiceIpcProtocol.IsCanonicalSha256(ConfigurationHash)))
        {
            return "service.ipc.generation_invalid";
        }

        if (!requiresGeneration && (Generation is not null || ConfigurationHash is not null))
        {
            return "service.ipc.generation_unexpected";
        }

        bool isControllerBrokerCommand = Command is
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration
            or MihomoServiceIpcCommand.GetConnections
            or MihomoServiceIpcCommand.CloseConnection
            or MihomoServiceIpcCommand.CloseAllConnections
            or MihomoServiceIpcCommand.GetProxyRuntimeSnapshot
            or MihomoServiceIpcCommand.SelectProxy
            or MihomoServiceIpcCommand.GetRuntimeLogs
            or MihomoServiceIpcCommand.UpdateProvider;
        if (isControllerBrokerCommand
            && (ExpectedRuntime is null || ExpectedRuntime.Validate() is not null))
        {
            return "service.ipc.expected_runtime_invalid";
        }

        if (!isControllerBrokerCommand && ExpectedRuntime is not null)
        {
            return "service.ipc.expected_runtime_unexpected";
        }

        if (Command == MihomoServiceIpcCommand.CloseConnection)
        {
            if (!MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(ConnectionId))
            {
                return "service.ipc.connection_id_invalid";
            }
        }
        else if (ConnectionId is not null)
        {
            return "service.ipc.connection_id_unexpected";
        }

        if (Command == MihomoServiceIpcCommand.SelectProxy)
        {
            if (ProxySelection is null || ProxySelection.Validate() is not null)
            {
                return "service.ipc.proxy_selection_invalid";
            }
        }
        else if (ProxySelection is not null)
        {
            return "service.ipc.proxy_selection_unexpected";
        }

        if (Command == MihomoServiceIpcCommand.GetRuntimeLogs)
        {
            if (RuntimeLogQuery is null || RuntimeLogQuery.Validate() is not null)
            {
                return "service.ipc.runtime_log_query_invalid";
            }
        }
        else if (RuntimeLogQuery is not null)
        {
            return "service.ipc.runtime_log_query_unexpected";
        }

        if (Command == MihomoServiceIpcCommand.UpdateProvider)
        {
            if (ProviderUpdate is null || ProviderUpdate.Validate() is not null)
            {
                return "service.ipc.provider_update_invalid";
            }
        }
        else if (ProviderUpdate is not null)
        {
            return "service.ipc.provider_update_unexpected";
        }

        if (Command == MihomoServiceIpcCommand.Logs)
        {
            if (MaximumLogEntries is null or < 1
                or > MihomoServiceIpcProtocol.MaximumLogEntries)
            {
                return "service.ipc.log_limit_invalid";
            }
        }
        else if (MaximumLogEntries is not null)
        {
            return "service.ipc.log_limit_unexpected";
        }

        return null;
    }
}

/// <summary>Captures the service session and its currently owned runtime generation.</summary>
public sealed record MihomoServiceIpcSnapshot
{
    /// <summary>Gets the nonempty identity regenerated for every service-host process.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Gets the service build version participating in the handshake.</summary>
    public string ServiceVersion { get; init; } = string.Empty;

    /// <summary>Gets the owned child lifecycle state.</summary>
    public MihomoServiceChildState ChildState { get; init; }

    /// <summary>Gets the owned child process ID while ownership exists.</summary>
    public int? ChildProcessId { get; init; }

    /// <summary>Gets the runtime generation currently owned by the child.</summary>
    public long? ActiveGeneration { get; init; }

    /// <summary>Gets the exact configuration content hash currently owned by the child.</summary>
    public string? ActiveConfigurationHash { get; init; }

    /// <summary>Gets a stable fault code when the child state is faulted.</summary>
    public string? FaultCode { get; init; }

    /// <summary>Returns a stable validation error code, or null for a coherent snapshot.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        if (SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(ServiceVersion)
            || ServiceVersion.Length > 128
            || ServiceVersion.Any(char.IsControl))
        {
            return "service.ipc.session_invalid";
        }

        if (!Enum.IsDefined(ChildState))
        {
            return "service.ipc.child_state_invalid";
        }

        bool canOwnChild = ChildState is MihomoServiceChildState.Starting
            or MihomoServiceChildState.Running
            or MihomoServiceChildState.Stopping
            or MihomoServiceChildState.Faulted;
        if (ChildProcessId is <= 0
            || ChildProcessId is not null && !canOwnChild)
        {
            return "service.ipc.child_process_invalid";
        }

        bool hasGeneration = ActiveGeneration is not null || ActiveConfigurationHash is not null;
        if (hasGeneration
            && (ActiveGeneration is null or < 1
                || !MihomoServiceIpcProtocol.IsCanonicalSha256(ActiveConfigurationHash)))
        {
            return "service.ipc.active_generation_invalid";
        }

        if (ChildState is MihomoServiceChildState.Running or MihomoServiceChildState.Stopping
            && (ChildProcessId is null || !hasGeneration))
        {
            return "service.ipc.active_generation_invalid";
        }

        if (ChildState == MihomoServiceChildState.Faulted
            && ChildProcessId is not null
            && !hasGeneration)
        {
            return "service.ipc.active_generation_invalid";
        }

        if (ChildState == MihomoServiceChildState.Starting && !hasGeneration)
        {
            return "service.ipc.active_generation_invalid";
        }

        if (ChildState == MihomoServiceChildState.Stopped
            && (ChildProcessId is not null || hasGeneration))
        {
            return "service.ipc.stopped_generation_invalid";
        }

        if (ChildState == MihomoServiceChildState.Faulted
            != !string.IsNullOrWhiteSpace(FaultCode))
        {
            return "service.ipc.fault_invalid";
        }

        if (FaultCode is { Length: > 256 }
            || FaultCode?.Any(char.IsControl) == true)
        {
            return "service.ipc.fault_invalid";
        }

        return null;
    }
}

/// <summary>Projects only the effective mihomo fields needed for readiness verification.</summary>
public sealed record MihomoServiceIpcEffectiveConfiguration
{
    /// <summary>Gets whether the controller reports a usable effective configuration.</summary>
    public bool ControllerReady { get; init; }

    /// <summary>Gets the effective routing mode.</summary>
    public MihomoServiceIpcRoutingMode Mode { get; init; }

    /// <summary>Gets whether the mihomo TUN engine is enabled.</summary>
    public bool TunEnabled { get; init; }

    /// <summary>Gets the effective mixed proxy port, or zero when the Service disables that listener.</summary>
    public int MixedPort { get; init; }

    /// <summary>Returns a stable validation error code, or null for a bounded readiness projection.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        return !Enum.IsDefined(Mode) || MixedPort is < 0 or > ushort.MaxValue
            ? "service.ipc.effective_configuration_invalid"
            : null;
    }
}

/// <summary>Projects one active connection into the bounded service IPC contract.</summary>
public sealed record MihomoServiceIpcConnection
{
    /// <summary>Gets the controller connection identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the originating process name when reported.</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Gets the destination host or address.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Gets the matched rule name or type.</summary>
    public string RuleName { get; init; } = string.Empty;

    /// <summary>Gets the matched rule payload.</summary>
    public string RulePayload { get; init; } = string.Empty;

    /// <summary>Gets the selected proxy-chain display text.</summary>
    public string ProxyName { get; init; } = string.Empty;

    /// <summary>Gets the uploaded byte count.</summary>
    public long UploadBytes { get; init; }

    /// <summary>Gets the downloaded byte count.</summary>
    public long DownloadBytes { get; init; }

    /// <summary>Gets the reported connection start time.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Returns a stable validation error code, or null for a bounded connection row.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        return !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(Id)
            || !MihomoServiceIpcProtocol.IsBoundedControllerText(ProcessName)
            || !MihomoServiceIpcProtocol.IsBoundedControllerText(Host)
            || !MihomoServiceIpcProtocol.IsBoundedControllerText(RuleName)
            || !MihomoServiceIpcProtocol.IsBoundedControllerText(RulePayload)
            || !MihomoServiceIpcProtocol.IsBoundedControllerText(ProxyName)
            || UploadBytes < 0
            || DownloadBytes < 0
            || StartedAt == default
                ? "service.ipc.connection_invalid"
                : null;
    }
}

/// <summary>Contains one bounded active-connection snapshot.</summary>
public sealed record MihomoServiceIpcConnectionSnapshot
{
    /// <summary>Gets the active connection rows.</summary>
    public IReadOnlyList<MihomoServiceIpcConnection> Connections { get; init; } =
        Array.Empty<MihomoServiceIpcConnection>();

    /// <summary>Returns a stable validation error code, or null for a bounded connection snapshot.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        if (Connections is null
            || Connections.Count > MihomoServiceIpcProtocol.MaximumControllerConnections
            || Connections.Count > MihomoServiceIpcProtocol.MaximumControllerAggregateItems)
        {
            return "service.ipc.connection_snapshot_count_invalid";
        }

        HashSet<string> connectionIds = new(StringComparer.Ordinal);
        long aggregateCharacters = 0;
        foreach (MihomoServiceIpcConnection connection in Connections)
        {
            if (connection is null || connection.Validate() is not null)
            {
                return "service.ipc.connection_snapshot_entry_invalid";
            }

            if (!connectionIds.Add(connection.Id))
            {
                return "service.ipc.connection_snapshot_duplicate_invalid";
            }

            aggregateCharacters += connection.Id.Length
                + connection.ProcessName.Length
                + connection.Host.Length
                + connection.RuleName.Length
                + connection.RulePayload.Length
                + connection.ProxyName.Length;
            if (aggregateCharacters > MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters)
            {
                return "service.ipc.controller_aggregate_invalid";
            }
        }

        return null;
    }
}

/// <summary>Projects one selectable proxy group into the bounded service IPC contract.</summary>
public sealed record MihomoServiceIpcProxyGroup
{
    /// <summary>Gets the proxy-group name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the mihomo proxy-group type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the currently selected proxy name.</summary>
    public string CurrentSelection { get; init; } = string.Empty;

    /// <summary>Gets the selectable candidate names.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    /// <summary>Returns a stable validation error code, or null for a bounded proxy group.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        if (!MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(Name)
            || !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(Type)
            || !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(CurrentSelection)
            || Candidates is null
            || Candidates.Count is < 1
                or > MihomoServiceIpcProtocol.MaximumControllerCandidatesPerGroup)
        {
            return "service.ipc.proxy_group_invalid";
        }

        HashSet<string> candidates = new(StringComparer.Ordinal);
        foreach (string candidate in Candidates)
        {
            if (!MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(candidate)
                || !candidates.Add(candidate))
            {
                return "service.ipc.proxy_group_candidate_invalid";
            }
        }

        return candidates.Contains(CurrentSelection)
            ? null
            : "service.ipc.proxy_group_selection_invalid";
    }
}

/// <summary>Projects one proxy-provider or rule-provider summary.</summary>
public sealed record MihomoServiceIpcProvider
{
    /// <summary>Gets the provider name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the provider namespace.</summary>
    public MihomoServiceIpcProviderKind Kind { get; init; }

    /// <summary>Gets the provider vehicle type.</summary>
    public string VehicleType { get; init; } = string.Empty;

    /// <summary>Gets the rule-provider behavior when reported.</summary>
    public string Behavior { get; init; } = string.Empty;

    /// <summary>Gets the reported item count.</summary>
    public int ItemCount { get; init; }

    /// <summary>Gets the last update time when reported.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Returns a stable validation error code, or null for a bounded provider summary.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        return !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(Name)
            || !Enum.IsDefined(Kind)
            || !MihomoServiceIpcProtocol.IsBoundedControllerText(VehicleType)
            || !MihomoServiceIpcProtocol.IsBoundedControllerText(Behavior)
            || ItemCount < 0
                ? "service.ipc.provider_invalid"
                : null;
    }
}

/// <summary>Contains bounded proxy-group and provider summaries from one controller observation.</summary>
public sealed record MihomoServiceIpcProxyRuntimeSnapshot
{
    /// <summary>Gets the selectable proxy groups.</summary>
    public IReadOnlyList<MihomoServiceIpcProxyGroup> Groups { get; init; } =
        Array.Empty<MihomoServiceIpcProxyGroup>();

    /// <summary>Gets the proxy-provider and rule-provider summaries.</summary>
    public IReadOnlyList<MihomoServiceIpcProvider> Providers { get; init; } =
        Array.Empty<MihomoServiceIpcProvider>();

    /// <summary>Returns a stable validation error code, or null for a bounded runtime snapshot.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        if (Groups is null
            || Providers is null
            || Groups.Count > MihomoServiceIpcProtocol.MaximumControllerProxyGroups
            || Providers.Count > MihomoServiceIpcProtocol.MaximumControllerProviders)
        {
            return "service.ipc.proxy_runtime_count_invalid";
        }

        long aggregateItems = Groups.Count + Providers.Count;
        long aggregateCharacters = 0;
        HashSet<string> groupNames = new(StringComparer.Ordinal);
        foreach (MihomoServiceIpcProxyGroup group in Groups)
        {
            if (group is null
                || group.Validate() is not null
                || !groupNames.Add(group.Name))
            {
                return "service.ipc.proxy_runtime_group_invalid";
            }

            aggregateItems += group.Candidates.Count;
            aggregateCharacters += group.Name.Length
                + group.Type.Length
                + group.CurrentSelection.Length;
            foreach (string candidate in group.Candidates)
            {
                aggregateCharacters += candidate.Length;
            }

            if (aggregateItems > MihomoServiceIpcProtocol.MaximumControllerAggregateItems
                || aggregateCharacters > MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters)
            {
                return "service.ipc.controller_aggregate_invalid";
            }
        }

        HashSet<(MihomoServiceIpcProviderKind Kind, string Name)> providerNames = [];
        foreach (MihomoServiceIpcProvider provider in Providers)
        {
            if (provider is null
                || provider.Validate() is not null
                || !providerNames.Add((provider.Kind, provider.Name)))
            {
                return "service.ipc.proxy_runtime_provider_invalid";
            }

            aggregateCharacters += provider.Name.Length
                + provider.VehicleType.Length
                + provider.Behavior.Length;
            if (aggregateCharacters > MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters)
            {
                return "service.ipc.controller_aggregate_invalid";
            }
        }

        return null;
    }
}

/// <summary>Contains one sequenced, normalized mihomo runtime log entry.</summary>
public sealed record MihomoServiceIpcRuntimeLogEntry
{
    /// <summary>Gets the monotonically increasing service-owned sequence.</summary>
    public long Sequence { get; init; }

    /// <summary>Gets the normalized log level.</summary>
    public MihomoServiceIpcRuntimeLogLevel Level { get; init; }

    /// <summary>Gets the bounded log message after service-side redaction.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Returns a stable validation error code, or null for a bounded runtime log entry.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        return Sequence < 1
            || !Enum.IsDefined(Level)
            || string.IsNullOrWhiteSpace(Message)
            || Message.Length > MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters
            || Message.Any(char.IsControl)
                ? "service.ipc.runtime_log_entry_invalid"
                : null;
    }
}

/// <summary>Contains one bounded cursor-based snapshot of typed mihomo runtime logs.</summary>
public sealed record MihomoServiceIpcRuntimeLogSnapshot
{
    /// <summary>Gets the highest sequence observed by the service ring, or zero when empty.</summary>
    public long LatestSequence { get; init; }

    /// <summary>Gets the entries selected after the requested cursor.</summary>
    public IReadOnlyList<MihomoServiceIpcRuntimeLogEntry> Entries { get; init; } =
        Array.Empty<MihomoServiceIpcRuntimeLogEntry>();

    /// <summary>Returns a stable validation error code, or null for a bounded runtime-log snapshot.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        if (LatestSequence < 0
            || Entries is null
            || Entries.Count > MihomoServiceIpcProtocol.MaximumRuntimeLogEntries)
        {
            return "service.ipc.runtime_log_snapshot_invalid";
        }

        long previousSequence = 0;
        long aggregateCharacters = 0;
        foreach (MihomoServiceIpcRuntimeLogEntry entry in Entries)
        {
            if (entry is null
                || entry.Validate() is not null
                || entry.Sequence <= previousSequence
                || entry.Sequence > LatestSequence)
            {
                return "service.ipc.runtime_log_snapshot_entry_invalid";
            }

            previousSequence = entry.Sequence;
            aggregateCharacters += entry.Message.Length;
            if (aggregateCharacters > MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters)
            {
                return "service.ipc.controller_aggregate_invalid";
            }
        }

        return Entries.Count == 0 || LatestSequence > 0
            ? null
            : "service.ipc.runtime_log_snapshot_invalid";
    }
}

/// <summary>Contains one correlated service response and an optional bounded log snapshot.</summary>
public sealed record MihomoServiceIpcResponse
{
    /// <summary>Gets the service protocol version.</summary>
    public int ProtocolVersion { get; init; }

    /// <summary>Gets the request identity copied from the request.</summary>
    public Guid RequestId { get; init; }

    /// <summary>Gets whether the requested operation completed successfully.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets a stable nonlocalized failure code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Gets the post-operation service snapshot when the request was authenticated.</summary>
    public MihomoServiceIpcSnapshot? Snapshot { get; init; }

    /// <summary>Gets the bounded service log snapshot returned by a logs command.</summary>
    public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();

    /// <summary>Gets the bounded readiness projection returned by a probe command.</summary>
    public MihomoServiceIpcEffectiveConfiguration? EffectiveConfiguration { get; init; }

    /// <summary>Gets the bounded typed active-connection snapshot.</summary>
    public MihomoServiceIpcConnectionSnapshot? ConnectionSnapshot { get; init; }

    /// <summary>Gets the bounded typed proxy-group and provider snapshot.</summary>
    public MihomoServiceIpcProxyRuntimeSnapshot? ProxyRuntimeSnapshot { get; init; }

    /// <summary>Gets the bounded cursor-based typed mihomo runtime-log snapshot.</summary>
    public MihomoServiceIpcRuntimeLogSnapshot? RuntimeLogSnapshot { get; init; }

    /// <summary>Returns a stable validation error code, or null for a coherent response.</summary>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? Validate()
    {
        if (ProtocolVersion <= 0 || RequestId == Guid.Empty)
        {
            return "service.ipc.response_header_invalid";
        }

        if (Succeeded == !string.IsNullOrWhiteSpace(ErrorCode))
        {
            return "service.ipc.response_outcome_invalid";
        }

        if (!Succeeded && !MihomoServiceIpcProtocol.IsBoundedRequiredIdentifier(ErrorCode))
        {
            return "service.ipc.response_outcome_invalid";
        }

        if (Snapshot is not null && Snapshot.Validate() is not null)
        {
            return "service.ipc.response_snapshot_invalid";
        }

        if (Logs is null || Logs.Count > MihomoServiceIpcProtocol.MaximumLogEntries)
        {
            return "service.ipc.response_logs_invalid";
        }

        foreach (string entry in Logs)
        {
            if (entry is null
                || entry.Length > MihomoServiceIpcProtocol.MaximumLogEntryCharacters)
            {
                return "service.ipc.response_log_entry_invalid";
            }
        }

        if (EffectiveConfiguration?.Validate() is not null)
        {
            return "service.ipc.response_effective_configuration_invalid";
        }

        if (ConnectionSnapshot?.Validate() is not null)
        {
            return "service.ipc.response_connection_snapshot_invalid";
        }

        if (ProxyRuntimeSnapshot?.Validate() is not null)
        {
            return "service.ipc.response_proxy_runtime_snapshot_invalid";
        }

        if (RuntimeLogSnapshot?.Validate() is not null)
        {
            return "service.ipc.response_runtime_log_snapshot_invalid";
        }

        int controllerPayloadCount = (EffectiveConfiguration is null ? 0 : 1)
            + (ConnectionSnapshot is null ? 0 : 1)
            + (ProxyRuntimeSnapshot is null ? 0 : 1)
            + (RuntimeLogSnapshot is null ? 0 : 1);
        if (controllerPayloadCount > 1
            || controllerPayloadCount > 0 && Logs.Count > 0)
        {
            return "service.ipc.response_payload_conflict";
        }

        if (!Succeeded && (controllerPayloadCount > 0 || Logs.Count > 0))
        {
            return "service.ipc.response_failure_payload_invalid";
        }

        return null;
    }

    /// <summary>Validates correlation, runtime binding, and exact payload shape for one request.</summary>
    /// <param name="request">The request that this response must satisfy.</param>
    /// <returns>A nonlocalized error code or null.</returns>
    public string? ValidateFor(MihomoServiceIpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? validationError = Validate();
        if (validationError is not null)
        {
            return validationError;
        }

        if (request.Validate() is not null)
        {
            return "service.ipc.response_request_invalid";
        }

        if (ProtocolVersion != request.ProtocolVersion || RequestId != request.RequestId)
        {
            return "service.ipc.response_correlation_invalid";
        }

        bool isControllerBrokerCommand = request.Command is
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration
            or MihomoServiceIpcCommand.GetConnections
            or MihomoServiceIpcCommand.CloseConnection
            or MihomoServiceIpcCommand.CloseAllConnections
            or MihomoServiceIpcCommand.GetProxyRuntimeSnapshot
            or MihomoServiceIpcCommand.SelectProxy
            or MihomoServiceIpcCommand.GetRuntimeLogs
            or MihomoServiceIpcCommand.UpdateProvider;
        if (isControllerBrokerCommand && Succeeded)
        {
            MihomoServiceIpcControllerBinding expectedRuntime = request.ExpectedRuntime!;
            if (Snapshot is not
                {
                    ChildState: MihomoServiceChildState.Running,
                    ActiveGeneration: not null,
                    ActiveConfigurationHash: not null,
                }
                || Snapshot.SessionId != expectedRuntime.ServiceSessionId
                || Snapshot.ActiveGeneration != expectedRuntime.Generation
                || !string.Equals(
                    Snapshot.ActiveConfigurationHash,
                    expectedRuntime.ConfigurationHash,
                    StringComparison.Ordinal))
            {
                return "service.ipc.response_runtime_binding_invalid";
            }
        }

        if (!Succeeded)
        {
            return null;
        }

        bool hasExpectedPayload = request.Command switch
        {
            MihomoServiceIpcCommand.ProbeEffectiveConfiguration =>
                EffectiveConfiguration is not null
                && ConnectionSnapshot is null
                && ProxyRuntimeSnapshot is null
                && RuntimeLogSnapshot is null
                && Logs.Count == 0,
            MihomoServiceIpcCommand.GetConnections =>
                EffectiveConfiguration is null
                && ConnectionSnapshot is not null
                && ProxyRuntimeSnapshot is null
                && RuntimeLogSnapshot is null
                && Logs.Count == 0,
            MihomoServiceIpcCommand.GetProxyRuntimeSnapshot =>
                EffectiveConfiguration is null
                && ConnectionSnapshot is null
                && ProxyRuntimeSnapshot is not null
                && RuntimeLogSnapshot is null
                && Logs.Count == 0,
            MihomoServiceIpcCommand.GetRuntimeLogs =>
                EffectiveConfiguration is null
                && ConnectionSnapshot is null
                && ProxyRuntimeSnapshot is null
                && RuntimeLogSnapshot is not null
                && Logs.Count == 0,
            MihomoServiceIpcCommand.Logs =>
                EffectiveConfiguration is null
                && ConnectionSnapshot is null
                && ProxyRuntimeSnapshot is null
                && RuntimeLogSnapshot is null,
            _ => EffectiveConfiguration is null
                && ConnectionSnapshot is null
                && ProxyRuntimeSnapshot is null
                && RuntimeLogSnapshot is null
                && Logs.Count == 0,
        };
        if (!hasExpectedPayload)
        {
            return "service.ipc.response_command_payload_invalid";
        }

        if (request.Command == MihomoServiceIpcCommand.GetRuntimeLogs)
        {
            MihomoServiceIpcRuntimeLogQuery query = request.RuntimeLogQuery!;
            if (RuntimeLogSnapshot!.Entries.Count > query.MaximumEntries
                || RuntimeLogSnapshot.Entries.Any(entry => entry.Sequence <= query.AfterSequence))
            {
                return "service.ipc.response_runtime_log_cursor_invalid";
            }
        }

        return null;
    }
}

/// <summary>Reads and writes strict length-prefixed JSON service IPC frames.</summary>
public static class MihomoServiceIpcFrameCodec
{
    private const int HeaderLength = sizeof(int);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>Writes one request frame and flushes the supplied stream.</summary>
    /// <param name="stream">Connected duplex pipe stream.</param>
    /// <param name="request">Request to encode.</param>
    /// <param name="cancellationToken">Cancels the pending write.</param>
    public static Task WriteRequestAsync(
        Stream stream,
        MihomoServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WriteAsync(stream, request, cancellationToken);
    }

    /// <summary>Writes one response frame and flushes the supplied stream.</summary>
    /// <param name="stream">Connected duplex pipe stream.</param>
    /// <param name="response">Response to encode.</param>
    /// <param name="cancellationToken">Cancels the pending write.</param>
    public static Task WriteResponseAsync(
        Stream stream,
        MihomoServiceIpcResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        return WriteAsync(stream, response, cancellationToken);
    }

    /// <summary>Reads exactly one request frame.</summary>
    /// <param name="stream">Connected duplex pipe stream.</param>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    /// <returns>The decoded request.</returns>
    public static Task<MihomoServiceIpcRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        return ReadAsync<MihomoServiceIpcRequest>(stream, cancellationToken);
    }

    /// <summary>Reads exactly one response frame.</summary>
    /// <param name="stream">Connected duplex pipe stream.</param>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    /// <returns>The decoded response.</returns>
    public static Task<MihomoServiceIpcResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        return ReadAsync<MihomoServiceIpcResponse>(stream, cancellationToken);
    }

    private static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        if (payload.Length is <= 0 or > MihomoServiceIpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("The service IPC frame exceeds its size limit.");
        }

        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength is <= 0 or > MihomoServiceIpcProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("The service IPC frame has an invalid size.");
        }

        byte[] payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("The service IPC frame is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The service IPC frame is invalid.", exception);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The service IPC frame ended unexpectedly.");
            }

            offset += read;
        }
    }
}
