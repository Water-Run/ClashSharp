namespace ClashSharp.Agent;

/// <summary>Contains a validated immutable user policy; it grants no authority to change itself.</summary>
public sealed record McpAccessPolicy
{
    /// <summary>Contains every currently defined permission bit, excluding future or unknown capabilities.</summary>
    public const McpAccessScope KnownScopes = McpAccessScope.ReadStatus
        | McpAccessScope.ReadConfiguration | McpAccessScope.ModifyConfiguration
        | McpAccessScope.ReadDiagnostics | McpAccessScope.ReadHistorySummary
        | McpAccessScope.ReadHistoryDetails | McpAccessScope.ReadSensitiveConfiguration
        | McpAccessScope.ChangeProxyState;

    /// <summary>Gets the initial policy: the endpoint is off and only status reading is preselected.</summary>
    public static McpAccessPolicy Default { get; } = new(false, McpAccessScope.ReadStatus);

    /// <summary>Creates a policy from explicit local user choices.</summary>
    /// <param name="enabled">Whether the user enabled the MCP endpoint.</param>
    /// <param name="scopes">The complete set of independent granted scopes.</param>
    public McpAccessPolicy(bool enabled, McpAccessScope scopes)
    {
        if ((scopes & ~KnownScopes) != McpAccessScope.None) { throw new ArgumentOutOfRangeException(nameof(scopes)); }
        Enabled = enabled;
        Scopes = scopes;
    }

    /// <summary>Gets whether the endpoint is allowed to accept authenticated product requests.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the user-selected permission bits without implied grants.</summary>
    public McpAccessScope Scopes { get; }

    /// <summary>Requires an enabled endpoint and every known, nonempty requested permission.</summary>
    /// <param name="required">All scopes needed by the complete operation.</param>
    /// <returns>Whether this policy grants the complete operation.</returns>
    public bool Allows(McpAccessScope required) => Enabled
        && required != McpAccessScope.None
        && (required & ~KnownScopes) == McpAccessScope.None
        && (Scopes & required) == required;
}
