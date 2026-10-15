namespace ClashSharp.Agent;

/// <summary>Defines independent user grants for the application's native MCP endpoint.</summary>
[Flags]
public enum McpAccessScope
{
    /// <summary>No product access.</summary>
    None = 0,

    /// <summary>Read basic application, core and proxy status.</summary>
    ReadStatus = 1 << 0,

    /// <summary>Read approved settings and redacted configuration information.</summary>
    ReadConfiguration = 1 << 1,

    /// <summary>Submit validated configuration changes without implicit authority over connectivity.</summary>
    ModifyConfiguration = 1 << 2,

    /// <summary>Read bounded diagnostic logs and current connection information.</summary>
    ReadDiagnostics = 1 << 3,

    /// <summary>Read paginated historical record summaries.</summary>
    ReadHistorySummary = 1 << 4,

    /// <summary>Read details of explicitly identified historical records.</summary>
    ReadHistoryDetails = 1 << 5,

    /// <summary>Include sensitive configuration values when the corresponding read operation is also granted.</summary>
    ReadSensitiveConfiguration = 1 << 6,

    /// <summary>Change proxy mode, active routing selection, or other current or future connectivity behavior.</summary>
    ChangeProxyState = 1 << 7,
}
