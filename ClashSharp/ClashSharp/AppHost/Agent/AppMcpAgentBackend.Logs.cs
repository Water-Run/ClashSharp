using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.Model;

namespace ClashSharp.Hosting.Agent;

internal sealed partial class AppMcpAgentBackend
{
    private McpToolResponse ReadDiagnostics(int limit, CancellationToken token)
    {
        // Even 100 maximally escaped entries remain below the protocol's two-MiB content bound.
        var diagnostics = repositories.Logs.ReadSessionDiagnostics(limit, token).Select(row => new
        {
            record_id = LogRecordId(row.Id),
            created_at = row.Record.CreatedAt,
            level = BoundLogText(row.Record.Level, 32),
            source = BoundLogText(row.Record.Source, 128),
            message = BoundLogText(row.Record.Message, 1024),
            detail = BoundLogText(row.Record.Detail, 1024),
            truncated = row.IsTruncated || row.Record.Level.Length > 32 || row.Record.Source.Length > 128
                || row.Record.Message.Length > 1024 || row.Record.Detail.Length > 1024
        }).ToArray();
        return Success(new { process_started_at = processStarted, session_only = true, diagnostics });
    }

    private McpToolResponse ReadLogHistorySummaries(JsonElement args, int limit, CancellationToken token)
    {
        long? before = null;
        if (args.TryGetProperty("before_record_id", out JsonElement cursor))
        {
            if (!TryReadLogRecordId(cursor.GetString()!, out long id)) { return McpToolResponse.Error("mcp.record_unavailable"); }
            before = id;
        }
        string? source = args.TryGetProperty("source", out JsonElement filterSource) ? filterSource.GetString() : null;
        string? level = args.TryGetProperty("level", out JsonElement filterLevel) ? filterLevel.GetString() : null;
        StoredLogRecord[] rows = repositories.Logs.ReadLogHistorySummaries(limit + 1, before, source, level, token).ToArray();
        bool more = rows.Length > limit;
        var records = rows.Take(limit).Select(row => new
        {
            record_id = LogRecordId(row.Id),
            created_at = row.Record.CreatedAt,
            level = BoundLogText(row.Record.Level, 32),
            source = BoundLogText(row.Record.Source, 128),
            truncated = row.IsTruncated || row.Record.Level.Length > 32 || row.Record.Source.Length > 128
        }).ToArray();
        return Success(new
        {
            records,
            has_more = more,
            next_before_record_id = more ? LogRecordId(rows[limit - 1].Id) : null
        });
    }

    private McpToolResponse ReadLogHistoryEntry(JsonElement args, CancellationToken token)
    {
        if (!TryReadLogRecordId(args.GetProperty("record_id").GetString()!, out long id)
            || repositories.Logs.ReadLogHistoryEntry(id, token) is not { } row)
        {
            return McpToolResponse.Error("mcp.record_unavailable");
        }
        return Success(new
        {
            record_id = LogRecordId(row.Id),
            created_at = row.Record.CreatedAt,
            level = row.Record.Level,
            source = row.Record.Source,
            message = row.Record.Message,
            detail = row.Record.Detail,
            truncated = row.IsTruncated,
            redacted = true
        });
    }

    private string LogRecordId(long id) => repositories.Generation.GenerationId.ToString("N") + ":" + id.ToString(CultureInfo.InvariantCulture);

    private bool TryReadLogRecordId(string text, out long id)
    {
        id = 0;
        return text.Length is >= 34 and <= 52 && text[32] == ':'
            && Guid.TryParseExact(text[..32], "N", out Guid generation) && generation == repositories.Generation.GenerationId
            && long.TryParse(text.AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && id > 0 && StringComparer.Ordinal.Equals(text, LogRecordId(id));
    }

    private static string BoundLogText(string text, int maximum)
    {
        if (text.Length <= maximum) { return text; }
        int length = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..length];
    }
}
