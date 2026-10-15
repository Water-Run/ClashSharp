using System.Text.Json;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Contains an explicitly selected response projection without exposing exceptions or service instances.</summary>
public sealed class McpToolResponse
{
    /// <summary>Maximum UTF-8 size of one structured tool response.</summary>
    public const int MaximumContentBytes = 2 * 1024 * 1024;

    /// <summary>Creates a bounded structured response.</summary>
    /// <param name="isError">Whether the operation failed without claiming success.</param>
    /// <param name="content">An object containing only authorized response data or stable error codes.</param>
    public McpToolResponse(bool isError, JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object
            || System.Text.Encoding.UTF8.GetByteCount(content.GetRawText()) > MaximumContentBytes)
        {
            throw new ArgumentException("MCP output must be a bounded JSON object.", nameof(content));
        }
        IsError = isError;
        Content = content.Clone();
    }

    /// <summary>Gets whether the requested operation failed.</summary>
    public bool IsError { get; }

    /// <summary>Gets explicitly authorized structured response content.</summary>
    public JsonElement Content { get; }

    /// <summary>Creates a value-free error using a stable diagnostic identifier.</summary>
    /// <param name="code">Lowercase diagnostic identifier with dots or underscores, at most 80 characters.</param>
    /// <returns>An error response without raw exception or request text.</returns>
    public static McpToolResponse Error(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 80 || code.Any(character => character is not (>= 'a' and <= 'z' or '_' or '.')))
        {
            throw new ArgumentException("Invalid MCP error code.", nameof(code));
        }
        return new(true, JsonSerializer.SerializeToElement(new { error = code }));
    }
}
