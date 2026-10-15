using System.Text.Json;
using ClashSharp.ApplicationModel.Agent;

namespace ClashSharp.Tests.Unit.Agent;

/// <summary>Checks credential isolation and detached bounded protocol values.</summary>
public sealed class McpCredentialTests
{
    [Fact]
    public void Credentials_AuthenticateOnlyTheirOwnOpaqueSecret()
    {
        string firstSecret = McpAccessCredential.CreateSecret();
        string secondSecret = McpAccessCredential.CreateSecret();
        McpAccessCredential first = new(firstSecret);
        McpAccessCredential second = new(secondSecret);

        Assert.True(first.TryAuthenticate(firstSecret, out McpClientIdentity? identity));
        Assert.Equal(first.Identity, identity);
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.False(first.TryAuthenticate(secondSecret, out _));
        Assert.False(first.TryAuthenticate("A" + firstSecret[1..], out _));
        Assert.False(first.TryAuthenticate(" " + firstSecret, out _));
        Assert.DoesNotContain(firstSecret, first.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("Bearer token")]
    public void InvalidPersistedCredentials_AreNotSilentlyReplaced(string value)
    {
        Assert.Throws<ArgumentException>(() => new McpAccessCredential(value));
    }

    [Fact]
    public void ToolValues_RetainDetachedObjectsAfterParserDisposal()
    {
        McpToolRequest request;
        McpToolResponse response;
        using (JsonDocument document = JsonDocument.Parse("""{"value":"owned"}"""))
        {
            request = new(McpToolId.GetStatus, document.RootElement);
            response = new(false, document.RootElement);
        }
        Assert.Equal("owned", request.Arguments.GetProperty("value").GetString());
        Assert.Equal("owned", response.Content.GetProperty("value").GetString());
    }

    [Fact]
    public void ToolValues_RejectNonObjectOrOversizedPayloads()
    {
        using JsonDocument array = JsonDocument.Parse("[]");
        Assert.Throws<ArgumentException>(() => new McpToolRequest(McpToolId.GetStatus, array.RootElement));
        Assert.Throws<ArgumentException>(() => new McpToolResponse(false, array.RootElement));
        JsonElement large = JsonSerializer.SerializeToElement(new { value = new string('x', McpToolRequest.MaximumArgumentBytes) });
        Assert.Throws<ArgumentException>(() => new McpToolRequest(McpToolId.GetStatus, large));
        Assert.Throws<ArgumentException>(() => new McpToolResponse(false, large));
    }

    [Fact]
    public void Catalog_CoversEveryUseCaseAndRejectsUnlistedProtocolNames()
    {
        Assert.Equal(Enum.GetValues<McpToolId>().Order(), McpToolCatalog.All.Select(tool => tool.Id).Order());
        Assert.Equal(McpToolCatalog.All.Count, McpToolCatalog.All.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (McpToolDefinition tool in McpToolCatalog.All)
        {
            Assert.True(McpToolCatalog.TryGet(tool.Name, out McpToolDefinition? resolved));
            Assert.Same(tool, resolved);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(McpToolCatalog.TryGet(tool.Name.ToUpperInvariant(), out _));
        }
        Assert.False(McpToolCatalog.TryGet("run_shell", out _));
    }
}
