using System;
using System.Collections.Generic;
using System.Threading;
using ClashSharp.Diagnostics;
using ClashSharp.Model;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Service;

public sealed partial class LogStorageService
{
    /// <summary>Reads only this repository lifetime's newest diagnostic entries.</summary>
    internal IReadOnlyList<StoredLogRecord> ReadSessionDiagnostics(int limit, CancellationToken cancellationToken) =>
        ReadAgentLogs(limit, beforeId: null, recordId: null, currentSession: true, includeContent: true, source: null, level: null, cancellationToken);

    /// <summary>Reads a descending keyset page without selecting message or detail columns.</summary>
    internal IReadOnlyList<StoredLogRecord> ReadLogHistorySummaries(int limit, long? beforeId, string? source, string? level,
        CancellationToken cancellationToken) =>
        ReadAgentLogs(limit, beforeId, recordId: null, currentSession: false, includeContent: false, source, level, cancellationToken);

    /// <summary>Reads one retained record; removed identifiers return no result.</summary>
    internal StoredLogRecord? ReadLogHistoryEntry(long recordId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordId);
        IReadOnlyList<StoredLogRecord> rows = ReadAgentLogs(1, beforeId: null, recordId, currentSession: false,
            includeContent: true, source: null, level: null, cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    private IReadOnlyList<StoredLogRecord> ReadAgentLogs(int limit, long? beforeId, long? recordId, bool currentSession,
        bool includeContent, string? source, string? level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 101);
        if (beforeId is <= 0) { throw new ArgumentOutOfRangeException(nameof(beforeId)); }
        if (source is { Length: > 128 } || level is { Length: > 32 }) { throw new ArgumentException("Log filters exceed their bounds."); }
        using IDisposable operation = _operations.Enter();
        lock (_syncLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandTimeout = 5;
            List<string> predicates = ["Id > 0"];
            if (currentSession) { predicates.Add("SessionEpoch = $session"); command.Parameters.AddWithValue("$session", _logSessionEpoch); }
            if (beforeId is { } before) { predicates.Add("Id < $before"); command.Parameters.AddWithValue("$before", before); }
            if (recordId is { } id) { predicates.Add("Id = $id"); command.Parameters.AddWithValue("$id", id); }
            if (source is not null) { predicates.Add("Source = $source"); command.Parameters.AddWithValue("$source", source); }
            if (level is not null) { predicates.Add("Level = $level"); command.Parameters.AddWithValue("$level", level); }
            string content = includeContent
                ? "substr(Message, 1, $bound), substr(COALESCE(Detail, ''), 1, $bound), length(Message) > $bound OR length(COALESCE(Detail, '')) > $bound"
                : "'', '', 0";
            // Only fixed projections enter SQL. Legacy text is bounded inside SQLite before it
            // crosses the managed boundary; summaries never load either free-form content field.
            command.CommandText = "SELECT Id, CreatedAtUnixTime, substr(Level, 1, $bound), substr(Source, 1, $bound), " + content
                + ", length(Level) > $bound OR length(Source) > $bound FROM Logs"
                + (predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates))
                + " ORDER BY Id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$bound", RuntimeLogText.MaximumCharacters);
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            List<StoredLogRecord> rows = [];
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawLevel = reader.GetString(2);
                string rawSource = reader.GetString(3);
                string rawMessage = reader.GetString(4);
                string rawDetail = reader.GetString(5);
                bool truncated = reader.GetBoolean(6) || reader.GetBoolean(7)
                    || rawLevel.Length > RuntimeLogText.MaximumCharacters || rawSource.Length > RuntimeLogText.MaximumCharacters
                    || rawMessage.Length > RuntimeLogText.MaximumCharacters || rawDetail.Length > RuntimeLogText.MaximumCharacters;
                // Old databases may predate persistence redaction. Every agent read reapplies it.
                rows.Add(new(reader.GetInt64(0), new(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                    PersistedLogText.Normalize(rawLevel), PersistedLogText.Normalize(rawSource),
                    PersistedLogText.Normalize(rawMessage), PersistedLogText.Normalize(rawDetail)), truncated));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return rows.AsReadOnly();
        }
    }
}
