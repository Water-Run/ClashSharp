using System.Text.Json;
using ClashSharp.ApplicationModel.Agent;

namespace ClashSharp.Tests.Unit.Agent;

public sealed class McpToolArgumentValidatorTests
{
    [Theory]
    [InlineData(McpToolId.SelectProxy, 512, true)]
    [InlineData(McpToolId.SelectProxy, 513, false)]
    [InlineData(McpToolId.ListProxyGroupMembers, 512, true)]
    [InlineData(McpToolId.ListProxyGroupMembers, 513, false)]
    public void ProxyNames_AcceptTheControllerBoundaryWithoutTruncation(McpToolId tool, int length, bool valid)
    {
        Dictionary<string, object> arguments = new() { ["group"] = new string('x', length) };
        if (tool == McpToolId.SelectProxy)
        {
            arguments["operation_id"] = Guid.NewGuid().ToString();
            arguments["node"] = new string('y', length);
        }
        Assert.Equal(valid, McpToolArgumentValidator.IsValid(McpToolCatalog.All.Single(definition => definition.Id == tool),
            JsonSerializer.SerializeToElement(arguments)));
    }

    [Theory]
    [InlineData(McpToolId.GetStatus, "{}", true)]
    [InlineData(McpToolId.RetrySettingsApplication, "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"batch_id\":\"11111111-2222-3333-4444-555555555556\",\"attempt_id\":\"11111111-2222-3333-4444-555555555557\"}", true)]
    [InlineData(McpToolId.RetrySettingsApplication, "{}", false)]
    [InlineData(McpToolId.RetrySettingsApplication, "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"batch_id\":\"00000000-0000-0000-0000-000000000000\",\"attempt_id\":\"11111111-2222-3333-4444-555555555557\"}", false)]
    [InlineData(McpToolId.RetrySettingsApplication, "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"batch_id\":\"11111111-2222-3333-4444-555555555556\",\"attempt_id\":\"not-an-attempt\"}", false)]
    [InlineData(McpToolId.RetrySettingsApplication, "{\"operation_id\":\"11111111-2222-3333-4444-555555555555\",\"batch_id\":\"11111111-2222-3333-4444-555555555556\",\"attempt_id\":\"11111111-2222-3333-4444-555555555557\",\"keys\":[\"MixedPort\"]}", false)]
    [InlineData(McpToolId.GetStatus, "{\"unexpected\":true}", false)]
    [InlineData(McpToolId.ListLogHistory, "{\"limit\":100,\"source\":\"Fixture\",\"level\":\"Warning\"}", true)]
    [InlineData(McpToolId.ListLogHistory, "{\"limit\":101}", false)]
    [InlineData(McpToolId.ListLogHistory, "{\"level\":\"arbitrary\"}", false)]
    [InlineData(McpToolId.ListLogHistory, "{\"before_record_id\":\"11111111222233334444555555555555:1\"}", true)]
    [InlineData(McpToolId.ListLogHistory, "{\"before_record_id\":\"11111111222233334444555555555555:0\"}", false)]
    [InlineData(McpToolId.ReadLogHistory, "{}", false)]
    [InlineData(McpToolId.ReadLogHistory, "{\"record_id\":\"11111111222233334444555555555555:9223372036854775807\"}", true)]
    [InlineData(McpToolId.ReadLogHistory, "{\"record_id\":\"11111111222233334444555555555555:01\"}", false)]
    [InlineData(McpToolId.ReadLogHistory, "{\"record_id\":\"11111111222233334444555555555555:-1\"}", false)]
    [InlineData(McpToolId.ReadLogHistory, "{\"record_id\":\"C:/outside\"}", false)]
    [InlineData(McpToolId.ListProxyGroups, "{\"offset\":0,\"limit\":100}", true)]
    [InlineData(McpToolId.ListProxyGroups, "{\"limit\":101}", false)]
    [InlineData(McpToolId.ListProxyGroupMembers, "{\"group\":\"GLOBAL\",\"offset\":1,\"limit\":2}", true)]
    [InlineData(McpToolId.ListProxyGroupMembers, "{}", false)]
    [InlineData(McpToolId.ListProxyGroupMembers, "{\"group\":\"\"}", false)]
    [InlineData(McpToolId.ListProxyGroupMembers, "{\"group\":\"GLOBAL\",\"offset\":-1}", false)]
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
