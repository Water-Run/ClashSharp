using System.Text.Json;
using ClashSharp.ApplicationModel.Agent;

namespace ClashSharp.Tests.Unit.Agent;

public sealed class McpToolArgumentValidatorTests
{
    [Theory]
    [InlineData(McpToolId.GetStatus, "{}", true)]
    [InlineData(McpToolId.GetStatus, "{\"unexpected\":true}", false)]
    [InlineData(McpToolId.ListProfiles, "{\"offset\":0,\"limit\":100}", true)]
    [InlineData(McpToolId.ListProfiles, "{\"limit\":101}", false)]
    [InlineData(McpToolId.ListProfiles, "{\"limit\":1.5}", false)]
    [InlineData(McpToolId.ListProfiles, "{\"limit\":\"1\"}", false)]
    [InlineData(McpToolId.ListProfiles, "{\"limit\":1,\"limit\":2}", false)]
    [InlineData(McpToolId.ReadProfile, "{}", false)]
    [InlineData(McpToolId.ReadProfile, "{\"profile_id\":\"\"}", false)]
    [InlineData(McpToolId.ReadProfile, "{\"profile_id\":\"local-fixture\",\"path\":\"C:/outside\"}", false)]
    [InlineData(McpToolId.GetTrafficHistory, "{\"group_by\":\"node\"}", true)]
    [InlineData(McpToolId.GetTrafficHistory, "{\"group_by\":\"raw\"}", false)]
    [InlineData(McpToolId.SetProxyState, "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"mode\":\"Faulted\"}", false)]
    [InlineData(McpToolId.SetProxyState, "{\"operation_id\":\"00000000-0000-0000-0000-000000000000\",\"mode\":\"Disabled\"}", false)]
    [InlineData(McpToolId.UpdateSettings, "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"changes\":{}}", false)]
    [InlineData(McpToolId.UpdateSettings, "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"changes\":{\"RateUnit\":\"Bytes\",\"RateUnit\":\"Bits\"}}", false)]
    public void ValidatesWholeClosedSchema(McpToolId tool, string input, bool valid)
    {
        Assert.Equal(valid, McpToolArgumentValidator.IsValid(McpToolCatalog.All.Single(definition => definition.Id == tool),
            JsonSerializer.Deserialize<JsonElement>(input)));
    }
}
