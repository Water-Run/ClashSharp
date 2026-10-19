namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Identifies the fixed native agent use cases independently of transport method names.</summary>
public enum McpToolId
{
    /// <summary>Read live status and currently granted capabilities.</summary>
    GetStatus,
    /// <summary>Read approved settings.</summary>
    GetSettings,
    /// <summary>Submit a complete validated setting change set.</summary>
    UpdateSettings,
    /// <summary>List configuration profiles.</summary>
    ListProfiles,
    /// <summary>Read a safe configuration projection.</summary>
    ReadProfile,
    /// <summary>Read authorized original configuration content.</summary>
    ReadSensitiveProfile,
    /// <summary>Validate and store configuration text with concurrency and connectivity guards.</summary>
    SaveProfile,
    /// <summary>Read bounded current diagnostics.</summary>
    GetDiagnostics,
    /// <summary>List retained configuration versions.</summary>
    ListProfileHistory,
    /// <summary>Read a safe retained-version projection.</summary>
    ReadProfileHistory,
    /// <summary>Read authorized original retained-version content.</summary>
    ReadSensitiveProfileHistory,
    /// <summary>Read bounded aggregate traffic history.</summary>
    GetTrafficHistory,
    /// <summary>Request a network takeover mode.</summary>
    SetProxyState,
    /// <summary>Select a proxy group member.</summary>
    SelectProxy,
    /// <summary>Activate an existing profile.</summary>
    ActivateProfile,
    /// <summary>List live strategy groups without original configuration content.</summary>
    ListProxyGroups,
    /// <summary>List the names of members in one live strategy group.</summary>
    ListProxyGroupMembers,
    /// <summary>List retained application-log identifiers and metadata without log content.</summary>
    ListLogHistory,
    /// <summary>Read the redacted content of one identified retained application log.</summary>
    ReadLogHistory,
    /// <summary>Retry one exact failed settings application under a fresh operation identity.</summary>
    RetrySettingsApplication,
}
