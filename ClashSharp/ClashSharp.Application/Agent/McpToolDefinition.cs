using System.Text.Json;
using ClashSharp.Agent;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Describes one fixed tool and the minimum grants required before its business-specific checks.</summary>
public sealed class McpToolDefinition
{
    internal McpToolDefinition(McpToolId id, string name, string description, McpOperation operation, JsonElement inputSchema)
    {
        Id = id;
        Name = name;
        Description = description;
        Operation = operation;
        InputSchema = inputSchema.Clone();
    }

    /// <summary>Gets the internal use-case identity.</summary>
    public McpToolId Id { get; }
    /// <summary>Gets the stable protocol tool name.</summary>
    public string Name { get; }
    /// <summary>Gets trusted product-authored usage instructions without user data.</summary>
    public string Description { get; }
    /// <summary>Gets the minimum capability class.</summary>
    public McpOperation Operation { get; }
    /// <summary>Gets the minimum required grants; individual setting writes may require additional grants.</summary>
    public McpAccessScope RequiredScopes => McpAccessRules.RequiredScopes(Operation);
    /// <summary>Gets the explicit bounded input schema.</summary>
    public JsonElement InputSchema { get; }
    /// <summary>Gets whether the tool makes no product changes.</summary>
    public bool IsReadOnly => (RequiredScopes & (McpAccessScope.ModifyConfiguration | McpAccessScope.ChangeProxyState)) == 0;
}
