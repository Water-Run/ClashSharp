namespace ClashSharp.Agent;

/// <summary>Identifies product capability classes independently of MCP transport and tool discovery.</summary>
public enum McpOperation
{
    /// <summary>Read basic live status.</summary>
    ReadStatus,
    /// <summary>Read configuration without sensitive fields.</summary>
    ReadConfiguration,
    /// <summary>Read configuration including explicitly authorized sensitive fields.</summary>
    ReadSensitiveConfiguration,
    /// <summary>Modify validated configuration that does not change connectivity.</summary>
    ModifyConfiguration,
    /// <summary>Modify active configuration or a setting that can change connectivity.</summary>
    ModifyLiveConfiguration,
    /// <summary>Read bounded logs or current connection details.</summary>
    ReadDiagnostics,
    /// <summary>Read paginated historical record metadata.</summary>
    ReadHistorySummary,
    /// <summary>Read historical details with configuration secrets excluded.</summary>
    ReadHistoryDetails,
    /// <summary>Read historical configuration with explicitly authorized sensitive fields.</summary>
    ReadSensitiveHistory,
    /// <summary>Explicitly change proxy mode, routing selection or activation state.</summary>
    ChangeProxyState,
}
