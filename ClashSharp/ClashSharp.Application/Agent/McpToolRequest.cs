using System.Text.Json;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Owns a bounded detached argument object; its contents must never be logged implicitly.</summary>
public sealed class McpToolRequest
{
    /// <summary>Maximum UTF-8 size of one accepted tool argument object.</summary>
    public const int MaximumArgumentBytes = 2 * 1024 * 1024;

    /// <summary>Creates a request for a known use case.</summary>
    /// <param name="tool">Fixed native agent use case.</param>
    /// <param name="arguments">Detached JSON object validated again by the admitted business operation.</param>
    public McpToolRequest(McpToolId tool, JsonElement arguments)
    {
        if (!Enum.IsDefined(tool)) { throw new ArgumentOutOfRangeException(nameof(tool)); }
        if (arguments.ValueKind != JsonValueKind.Object
            || System.Text.Encoding.UTF8.GetByteCount(arguments.GetRawText()) > MaximumArgumentBytes)
        {
            throw new ArgumentException("MCP arguments must be a bounded JSON object.", nameof(arguments));
        }
        Tool = tool;
        Arguments = arguments.Clone();
    }

    /// <summary>Gets the fixed requested use case.</summary>
    public McpToolId Tool { get; }

    /// <summary>Gets the owned argument object, treated as untrusted input.</summary>
    public JsonElement Arguments { get; }
}
