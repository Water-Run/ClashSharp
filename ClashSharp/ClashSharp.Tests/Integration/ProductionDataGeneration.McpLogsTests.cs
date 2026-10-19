extern alias ClashSharpUi;
using System.Text;
using System.Text.Json;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task NativeAgent_CurrentDiagnosticsContainUsefulRedactedTextWithoutHistoricalRows()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        var logs = fixture.Containers[0].Logs;
        logs.AppendLog("Warning", "FixtureLog", "core.connection_failed", "Authorization: Bearer private-current-token");
        InsertLegacyAgentLog(logs.DatabasePath, "private-previous-session", "private-previous-detail");
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadDiagnostics);

        CallToolResult response = await agent.CallAsync(McpToolId.GetDiagnostics, new { limit = 100 });

        Assert.False(response.IsError ?? false, response.StructuredContent?.GetRawText());
        JsonElement content = response.StructuredContent!.Value;
        Assert.True(content.GetProperty("session_only").GetBoolean());
        JsonElement current = Assert.Single(content.GetProperty("diagnostics").EnumerateArray(),
            row => row.GetProperty("source").GetString() == "FixtureLog");
        Assert.Equal("core.connection_failed", current.GetProperty("message").GetString());
        Assert.Contains("[redacted]", current.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-current-token", content.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-previous", content.GetRawText(), StringComparison.Ordinal);
        Assert.True((await agent.CallAsync(McpToolId.ReadLogHistory,
            new { record_id = current.GetProperty("record_id").GetString() })).IsError);
        Assert.True((await agent.CallAsync(McpToolId.ListLogHistory, new { })).IsError);
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_LogSummariesPageStablyAndDetailsNeedTheirOwnGrant()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        var logs = fixture.Containers[0].Logs;
        for (int index = 1; index <= 3; index++) { logs.AppendLog("Info", "FixtureLog", "private-message-" + index, "private-detail"); }
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadHistorySummary);
        Assert.DoesNotContain(await agent.Client.ListToolsAsync(), tool => tool.Name == "clashsharp_read_log_history");

        CallToolResult first = await agent.CallAsync(McpToolId.ListLogHistory, new { source = "FixtureLog", limit = 2 });

        Assert.False(first.IsError ?? false, first.StructuredContent?.GetRawText());
        JsonElement page = first.StructuredContent!.Value;
        Assert.True(page.GetProperty("has_more").GetBoolean());
        JsonElement[] firstRows = page.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(2, firstRows.Length);
        Assert.All(firstRows, row => { Assert.False(row.TryGetProperty("message", out _)); Assert.False(row.TryGetProperty("detail", out _)); });
        Assert.DoesNotContain("private-message", page.GetRawText(), StringComparison.Ordinal);
        string recordId = firstRows[0].GetProperty("record_id").GetString()!;
        string cursor = page.GetProperty("next_before_record_id").GetString()!;
        Assert.True((await agent.CallAsync(McpToolId.ReadLogHistory, new { record_id = recordId })).IsError);
        logs.AppendLog("Info", "FixtureLog", "arrived after first page", null);
        CallToolResult next = await agent.CallAsync(McpToolId.ListLogHistory, new { source = "FixtureLog", limit = 2, before_record_id = cursor });
        JsonElement older = Assert.Single(next.StructuredContent!.Value.GetProperty("records").EnumerateArray());
        Assert.DoesNotContain(firstRows, row => row.GetProperty("record_id").GetString() == older.GetProperty("record_id").GetString());
        Assert.False(next.StructuredContent.Value.GetProperty("has_more").GetBoolean());

        await agent.SetScopesAsync(McpAccessScope.ReadHistorySummary | McpAccessScope.ReadHistoryDetails);
        CallToolResult detail = await agent.CallAsync(McpToolId.ReadLogHistory, new { record_id = recordId });
        Assert.False(detail.IsError ?? false, detail.StructuredContent?.GetRawText());
        Assert.Equal("private-message-3", detail.StructuredContent!.Value.GetProperty("message").GetString());
        Assert.True(detail.StructuredContent.Value.GetProperty("redacted").GetBoolean());
        await agent.SetScopesAsync(McpAccessScope.ReadHistorySummary);
        Assert.True((await agent.CallAsync(McpToolId.ReadLogHistory, new { record_id = recordId })).IsError);
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_HistoricalLogContentIsRedactedAndGenerationOrRemovedIdsAreRefused()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        var logs = fixture.Containers[0].Logs;
        _ = logs.GetStorageSummary();
        InsertLegacyAgentLog(logs.DatabasePath, "password=private-legacy-secret config.validation_failed",
            "download https://user:private-password@example.test/private?token=private-query");
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadHistorySummary | McpAccessScope.ReadHistoryDetails);
        CallToolResult list = await agent.CallAsync(McpToolId.ListLogHistory, new { source = "LegacyAgent" });
        string recordId = Assert.Single(list.StructuredContent!.Value.GetProperty("records").EnumerateArray()).GetProperty("record_id").GetString()!;

        CallToolResult detail = await agent.CallAsync(McpToolId.ReadLogHistory, new { record_id = recordId });

        Assert.False(detail.IsError ?? false, detail.StructuredContent?.GetRawText());
        Assert.Contains("config.validation_failed", detail.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-legacy-secret", detail.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", detail.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        string foreignId = Guid.NewGuid().ToString("N") + recordId[32..];
        CallToolResult foreign = await agent.CallAsync(McpToolId.ReadLogHistory, new { record_id = foreignId });
        Assert.Equal("mcp.record_unavailable", foreign.StructuredContent!.Value.GetProperty("error").GetString());
        CallToolResult foreignCursor = await agent.CallAsync(McpToolId.ListLogHistory, new { before_record_id = foreignId });
        Assert.Equal("mcp.record_unavailable", foreignCursor.StructuredContent!.Value.GetProperty("error").GetString());
        _ = logs.CleanupLogs(source: "LegacyAgent");
        CallToolResult removed = await agent.CallAsync(McpToolId.ReadLogHistory, new { record_id = recordId });
        Assert.Equal("mcp.record_unavailable", removed.StructuredContent!.Value.GetProperty("error").GetString());
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_MaximumDiagnosticPageStaysWithinProtocolBoundsAndMarksTruncation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        string oversized = new('\uFFFF', 4096);
        for (int index = 0; index < 100; index++) { fixture.Containers[0].Logs.AppendLog(oversized, oversized, oversized, oversized); }
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadDiagnostics);

        CallToolResult response = await agent.CallAsync(McpToolId.GetDiagnostics, new { limit = 100 });

        Assert.False(response.IsError ?? false, response.StructuredContent?.GetRawText());
        JsonElement content = response.StructuredContent!.Value;
        Assert.Equal(100, content.GetProperty("diagnostics").GetArrayLength());
        Assert.True(Encoding.UTF8.GetByteCount(content.GetRawText()) < McpToolResponse.MaximumContentBytes);
        Assert.All(content.GetProperty("diagnostics").EnumerateArray(), row => Assert.True(row.GetProperty("truncated").GetBoolean()));
        Assert.Empty(agent.Errors.Records);
    }

    [Fact]
    public async Task NativeAgent_DiagnosticTruncationKeepsUnicodePairsIntact()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await fixture.StartAsync();
        string sourcePrefix = new('s', 127);
        string messagePrefix = new('m', 1023);
        fixture.Containers[0].Logs.AppendLog("Info", sourcePrefix + "😀", messagePrefix + "😀", messagePrefix + "😀");
        await using AgentSession agent = await AgentSession.StartAsync(fixture, McpAccessScope.ReadDiagnostics);

        CallToolResult response = await agent.CallAsync(McpToolId.GetDiagnostics, new { limit = 1 });

        Assert.False(response.IsError ?? false, response.StructuredContent?.GetRawText());
        JsonElement row = Assert.Single(response.StructuredContent!.Value.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(sourcePrefix, row.GetProperty("source").GetString());
        Assert.Equal(messagePrefix, row.GetProperty("message").GetString());
        Assert.Equal(messagePrefix, row.GetProperty("detail").GetString());
        Assert.True(row.GetProperty("truncated").GetBoolean());
    }

    private static void InsertLegacyAgentLog(string path, string message, string detail)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Logs (CreatedAtUnixTime, Level, Source, Message, Detail) VALUES (1, 'Info', 'LegacyAgent', $message, $detail);";
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }
}
