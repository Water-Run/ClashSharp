/*
 * SQLite Log Storage Service
 * Provides persistent local storage for logs, connection records, traffic snapshots, and cleanup operations
 *
 * @author: WaterRun
 * @file: Service/LogStorageService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClashSharp.Model;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Service;

/// <summary>Summarizes the current SQLite log storage footprint and record counts.</summary>
/// <param name="DatabasePath">Absolute path to the SQLite database file; never null.</param>
/// <param name="DatabaseSizeBytes">Current SQLite footprint in bytes, including WAL sidecar files when present.</param>
/// <param name="LogCount">Total count of log records currently stored.</param>
/// <param name="ConnectionCount">Total count of connection records currently stored.</param>
/// <remarks>
/// Invariants: Count values are non-negative and reflect the database state at query time.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct LogStorageSummary(
    string DatabasePath,
    long DatabaseSizeBytes,
    long LogCount,
    long ConnectionCount);

/// <summary>Summarizes long-term traffic and aggregation records stored in SQLite.</summary>
/// <param name="TotalUploadBytes">Total uploaded bytes estimated from connection records.</param>
/// <param name="TotalDownloadBytes">Total downloaded bytes estimated from connection records.</param>
/// <param name="ConnectionCount">Total connection record count.</param>
/// <param name="SnapshotCount">Total traffic snapshot count.</param>
/// <param name="ProfileCount">Number of profile traffic aggregation rows.</param>
/// <param name="NodeCount">Number of node traffic aggregation rows.</param>
/// <param name="NodeHealthCount">Number of node health rows.</param>
/// <param name="RuleCount">Number of rule hit aggregation rows.</param>
/// <remarks>
/// Invariants: Count and byte values are non-negative and reflect the database state at query time.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct TrafficStatisticsSummary(
    long TotalUploadBytes,
    long TotalDownloadBytes,
    long ConnectionCount,
    long SnapshotCount,
    long ProfileCount,
    long NodeCount,
    long NodeHealthCount,
    long RuleCount);

/// <summary>Provides SQLite-backed persistence for logs, connection history, and traffic statistics.</summary>
/// <remarks>
/// Invariants: The database schema is created before public operations query or mutate records.
/// Thread safety: Public methods serialize database access through a private lock.
/// Side effects: Creates and mutates a local SQLite database under the application data directory.
/// </remarks>
public sealed partial class LogStorageService
{
    /// <summary>Synchronization object guarding all SQLite operations for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Absolute path to the SQLite database file cached for this service lifetime.</summary>
    private readonly string _databasePath;

    private readonly Func<string> _getActiveProfileId;

    /// <summary>Tracks whether schema creation has completed for this service instance.</summary>
    private bool _isInitialized;

    /// <summary>Initializes the storage service and computes the database path.</summary>
    internal LogStorageService(string databasePath, Func<string> getActiveProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _databasePath = Path.GetFullPath(databasePath);
        string? dataDirectory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        _getActiveProfileId = getActiveProfileId ?? throw new ArgumentNullException(nameof(getActiveProfileId));
    }

    /// <summary>Gets the absolute SQLite database path used by this service.</summary>
    /// <value>Non-null absolute path under the application data directory.</value>
    public string DatabasePath => _databasePath;

    /// <summary>Returns storage size and primary record counts from the SQLite database.</summary>
    /// <returns>A <see cref="LogStorageSummary"/> snapshot for the current database state.</returns>
    public LogStorageSummary GetStorageSummary()
    {
        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            long logCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM Logs;");
            long connectionCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM Connections;");
            long databaseSize = LogStorageFootprint.CalculateBytes(_databasePath);

            return new LogStorageSummary(_databasePath, databaseSize, logCount, connectionCount);
        }
    }

    /// <summary>Returns long-term traffic statistics and aggregation row counts from SQLite.</summary>
    /// <returns>A <see cref="TrafficStatisticsSummary"/> snapshot for the current database state.</returns>
    public TrafficStatisticsSummary GetTrafficStatisticsSummary()
    {
        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            long totalUploadBytes = ExecuteScalarLong(connection, "SELECT COALESCE(SUM(UploadBytes), 0) FROM Connections;");
            long totalDownloadBytes = ExecuteScalarLong(connection, "SELECT COALESCE(SUM(DownloadBytes), 0) FROM Connections;");
            long connectionCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM Connections;");
            long snapshotCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM TrafficSnapshots;");
            long profileCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM ProfileTrafficStats;");
            long nodeCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM NodeTrafficStats;");
            long nodeHealthCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM NodeHealthStats;");
            long ruleCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM RuleHitStats;");

            return new TrafficStatisticsSummary(
                totalUploadBytes,
                totalDownloadBytes,
                connectionCount,
                snapshotCount,
                profileCount,
                nodeCount,
                nodeHealthCount,
                ruleCount);
        }
    }

    /// <summary>Returns traffic aggregation rows grouped by profile.</summary>
    /// <param name="limit">Maximum number of rows to return; must be greater than zero.</param>
    /// <returns>Profile traffic rows ordered by total traffic descending.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than or equal to zero.</exception>
    public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT ProfileId, UploadBytes, DownloadBytes, ConnectionCount, UpdatedAtUnixTime
                FROM ProfileTrafficStats
                ORDER BY (UploadBytes + DownloadBytes) DESC, UpdatedAtUnixTime DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            return ReadTrafficStatisticRows(command);
        }
    }

    /// <summary>Returns traffic aggregation rows grouped by day.</summary>
    /// <param name="limit">Maximum number of rows to return; must be greater than zero.</param>
    /// <returns>Daily traffic rows ordered from newest to oldest.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than or equal to zero.</exception>
    public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    strftime('%Y-%m-%d', CreatedAtUnixTime, 'unixepoch', 'localtime') AS DayLabel,
                    COALESCE(SUM(UploadBytes), 0) AS UploadBytes,
                    COALESCE(SUM(DownloadBytes), 0) AS DownloadBytes,
                    COUNT(*) AS SampleCount,
                    MAX(CreatedAtUnixTime) AS UpdatedAtUnixTime
                FROM TrafficSnapshots
                GROUP BY DayLabel
                ORDER BY UpdatedAtUnixTime DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            return ReadTrafficStatisticRows(command);
        }
    }

    /// <summary>Returns traffic aggregation rows grouped by proxy node.</summary>
    /// <param name="limit">Maximum number of rows to return; must be greater than zero.</param>
    /// <returns>Node traffic rows ordered by total traffic descending.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than or equal to zero.</exception>
    public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT NodeName, UploadBytes, DownloadBytes, 0 AS SampleCount, UpdatedAtUnixTime
                FROM NodeTrafficStats
                ORDER BY (UploadBytes + DownloadBytes) DESC, UpdatedAtUnixTime DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            return ReadTrafficStatisticRows(command);
        }
    }

    /// <summary>Upserts one node latency measurement into SQLite.</summary>
    /// <param name="nodeName">Node name. Must not be null or whitespace.</param>
    /// <param name="regionCode">Node region code. Must not be null.</param>
    /// <param name="latencyMilliseconds">Measured latency in milliseconds; null when the probe failed or was unavailable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodeName"/> or <paramref name="regionCode"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="nodeName"/> is whitespace.</exception>
    public void UpsertNodeHealth(string nodeName, string regionCode, int? latencyMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(nodeName);
        ArgumentNullException.ThrowIfNull(regionCode);

        if (string.IsNullOrWhiteSpace(nodeName))
        {
            throw new ArgumentException("Node name must not be whitespace.", nameof(nodeName));
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            long updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using SqliteConnection connection = OpenConnection();
            ExecuteNonQuery(
                connection,
                null,
                """
                INSERT INTO NodeHealthStats (NodeName, RegionCode, LatencyMilliseconds, UpdatedAtUnixTime)
                VALUES ($nodeName, $regionCode, $latency, $updatedAt)
                ON CONFLICT(NodeName) DO UPDATE SET
                    RegionCode = excluded.RegionCode,
                    LatencyMilliseconds = excluded.LatencyMilliseconds,
                    UpdatedAtUnixTime = excluded.UpdatedAtUnixTime;
                """,
                ("$nodeName", nodeName),
                ("$regionCode", regionCode),
                ("$latency", latencyMilliseconds.HasValue ? latencyMilliseconds.Value : DBNull.Value),
                ("$updatedAt", updatedAt));

            ExecuteNonQuery(
                connection,
                null,
                """
                INSERT INTO NodeTrafficStats (NodeName, RegionCode, UploadBytes, DownloadBytes, UpdatedAtUnixTime)
                VALUES ($nodeName, $regionCode, 0, 0, $updatedAt)
                ON CONFLICT(NodeName) DO UPDATE SET
                    RegionCode = excluded.RegionCode,
                    UpdatedAtUnixTime = excluded.UpdatedAtUnixTime;
                """,
                ("$nodeName", nodeName),
                ("$regionCode", regionCode),
                ("$updatedAt", updatedAt));
        }
    }

    /// <summary>Gets the latest measured latency for a proxy node.</summary>
    /// <param name="nodeName">Proxy node name. Must not be null.</param>
    /// <returns>Latency in milliseconds, or null when no latency is stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodeName"/> is null.</exception>
    public int? GetNodeLatencyMilliseconds(string nodeName)
    {
        ArgumentNullException.ThrowIfNull(nodeName);

        if (string.IsNullOrWhiteSpace(nodeName))
        {
            return null;
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT LatencyMilliseconds
                FROM NodeHealthStats
                WHERE NodeName = $nodeName
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$nodeName", nodeName.Trim());
            object? result = command.ExecuteScalar();
            return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Ensures rule hit rows exist for the provided rules without changing existing counts.</summary>
    /// <param name="rules">Rule rows to register. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is null.</exception>
    public void EnsureRuleHitRows(IEnumerable<RulePreview> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        lock (_syncLock)
        {
            EnsureInitialized();

            long updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using SqliteConnection connection = OpenConnection();
            foreach (RulePreview rule in rules)
            {
                string ruleName = BuildRuleName(rule);
                if (string.IsNullOrWhiteSpace(ruleName))
                {
                    continue;
                }

                ExecuteNonQuery(
                    connection,
                    null,
                    """
                    INSERT INTO RuleHitStats (RuleName, HitCount, UpdatedAtUnixTime)
                    VALUES ($ruleName, 0, $updatedAt)
                    ON CONFLICT(RuleName) DO NOTHING;
                    """,
                    ("$ruleName", ruleName),
                    ("$updatedAt", updatedAt));
            }
        }
    }

    /// <summary>Increments one rule hit counter.</summary>
    /// <param name="ruleName">Rule key such as "DOMAIN-SUFFIX,example.com,PROXY". Must not be null or whitespace.</param>
    /// <param name="increment">Positive hit increment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ruleName"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ruleName"/> is whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="increment"/> is less than or equal to zero.</exception>
    public void IncrementRuleHit(string ruleName, long increment = 1)
    {
        ArgumentNullException.ThrowIfNull(ruleName);

        if (string.IsNullOrWhiteSpace(ruleName))
        {
            throw new ArgumentException("Rule name must not be whitespace.", nameof(ruleName));
        }

        if (increment <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(increment), "Increment must be greater than zero.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            long updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using SqliteConnection connection = OpenConnection();
            ExecuteNonQuery(
                connection,
                null,
                """
                INSERT INTO RuleHitStats (RuleName, HitCount, UpdatedAtUnixTime)
                VALUES ($ruleName, $increment, $updatedAt)
                ON CONFLICT(RuleName) DO UPDATE SET
                    HitCount = RuleHitStats.HitCount + excluded.HitCount,
                    UpdatedAtUnixTime = excluded.UpdatedAtUnixTime;
                """,
                ("$ruleName", ruleName.Trim()),
                ("$increment", increment),
                ("$updatedAt", updatedAt));
        }
    }

    /// <summary>Appends active mihomo connection rows and updates traffic aggregation tables.</summary>
    /// <param name="connections">Active connection rows to persist. Must not be null.</param>
    /// <returns>Number of connection rows inserted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connections"/> is null.</exception>
    public int AppendConnectionSnapshot(IEnumerable<ActiveConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        lock (_syncLock)
        {
            EnsureInitialized();

            long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long totalUploadBytes = 0;
            long totalDownloadBytes = 0;
            int insertedCount = 0;

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (ActiveConnection activeConnection in connections)
            {
                totalUploadBytes += activeConnection.UploadBytes;
                totalDownloadBytes += activeConnection.DownloadBytes;
                insertedCount += ExecuteNonQuery(
                    connection,
                    transaction,
                    """
                    INSERT INTO Connections (CreatedAtUnixTime, ProcessName, Host, RuleName, ProxyName, UploadBytes, DownloadBytes)
                    VALUES ($createdAt, $processName, $host, $ruleName, $proxyName, $uploadBytes, $downloadBytes);
                    """,
                    ("$createdAt", createdAt),
                    ("$processName", activeConnection.ProcessName),
                    ("$host", activeConnection.Host),
                    ("$ruleName", activeConnection.RawRuleDisplay),
                    ("$proxyName", activeConnection.ProxyName),
                    ("$uploadBytes", activeConnection.UploadBytes),
                    ("$downloadBytes", activeConnection.DownloadBytes));

                UpsertNodeTraffic(connection, transaction, activeConnection.ProxyName, activeConnection.UploadBytes, activeConnection.DownloadBytes, createdAt);
                UpsertRuleHit(connection, transaction, activeConnection.RawRuleDisplay, 1, createdAt);
            }

            ExecuteNonQuery(
                connection,
                transaction,
                "INSERT INTO TrafficSnapshots (CreatedAtUnixTime, UploadBytes, DownloadBytes) VALUES ($createdAt, $uploadBytes, $downloadBytes);",
                ("$createdAt", createdAt),
                ("$uploadBytes", totalUploadBytes),
                ("$downloadBytes", totalDownloadBytes));
            UpsertProfileTraffic(connection, transaction, GetActiveProfileId(), totalUploadBytes, totalDownloadBytes, insertedCount, createdAt);
            transaction.Commit();

            return insertedCount;
        }
    }

    /// <summary>Returns all stored rule hit counts keyed by rule name.</summary>
    /// <returns>Dictionary of rule hit counts.</returns>
    public IReadOnlyDictionary<string, long> GetRuleHitCounts()
    {
        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT RuleName, HitCount FROM RuleHitStats;";

            Dictionary<string, long> hitCounts = new(StringComparer.Ordinal);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                hitCounts[reader.GetString(0)] = reader.GetInt64(1);
            }

            return hitCounts;
        }
    }

    /// <summary>Writes one application log record to persistent SQLite storage.</summary>
    /// <param name="level">Log severity level. Must not be null or whitespace.</param>
    /// <param name="source">Log source component. Must not be null or whitespace.</param>
    /// <param name="message">Primary log message. Must not be null or whitespace.</param>
    /// <param name="detail">Optional detail text; may be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="level"/>, <paramref name="source"/>, or <paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="level"/>, <paramref name="source"/>, or <paramref name="message"/> is whitespace.</exception>
    public void AppendLog(string level, string source, string message, string? detail)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(level))
        {
            throw new ArgumentException("Log level must not be whitespace.", nameof(level));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Log source must not be whitespace.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Log message must not be whitespace.", nameof(message));
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            ExecuteNonQuery(
                connection,
                null,
                "INSERT INTO Logs (CreatedAtUnixTime, Level, Source, Message, Detail) VALUES ($createdAt, $level, $source, $message, $detail);",
                ("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ("$level", level),
                ("$source", source),
                ("$message", message),
                ("$detail", detail ?? string.Empty));
        }
    }

    /// <summary>Returns the newest log records up to <paramref name="limit"/>.</summary>
    /// <param name="limit">Maximum number of records to return; must be greater than zero.</param>
    /// <returns>A read-only list of newest log records ordered from newest to oldest.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than or equal to zero.</exception>
    public IReadOnlyList<LogRecord> GetRecentLogs(int limit)
    {
        return GetRecentLogs(limit, source: null);
    }

    /// <summary>Returns the newest log records for one source up to <paramref name="limit"/>.</summary>
    /// <param name="source">Log source to filter by. Must not be null or whitespace.</param>
    /// <param name="limit">Maximum number of records to return; must be greater than zero.</param>
    /// <returns>A read-only list of newest log records ordered from newest to oldest.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than or equal to zero.</exception>
    public IReadOnlyList<LogRecord> GetRecentLogs(string source, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return GetRecentLogs(limit, source.Trim());
    }

    /// <summary>Returns newest log records matching optional visible-field filters.</summary>
    /// <param name="limit">Maximum number of records to return; must be greater than zero.</param>
    /// <param name="source">Optional exact log source/category filter.</param>
    /// <param name="level">Optional exact log level filter.</param>
    /// <param name="searchText">Optional text searched across level, source, message, and detail.</param>
    /// <returns>Matching log records ordered from newest to oldest.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than or equal to zero.</exception>
    public IReadOnlyList<LogRecord> GetLogs(int limit, string? source = null, string? level = null, string? searchText = null)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        string? normalizedSource = NormalizeOptionalFilter(source);
        string? normalizedLevel = NormalizeOptionalFilter(level);
        string? normalizedSearchText = NormalizeOptionalFilter(searchText);

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            List<string> predicates = [];

            if (normalizedSource is not null)
            {
                predicates.Add("Source = $source");
                command.Parameters.AddWithValue("$source", normalizedSource);
            }

            if (normalizedLevel is not null)
            {
                predicates.Add("Level = $level");
                command.Parameters.AddWithValue("$level", normalizedLevel);
            }

            if (normalizedSearchText is not null)
            {
                predicates.Add("(Level LIKE $search ESCAPE '\\' OR Source LIKE $search ESCAPE '\\' OR Message LIKE $search ESCAPE '\\' OR Detail LIKE $search ESCAPE '\\')");
                command.Parameters.AddWithValue("$search", $"%{EscapeLikePattern(normalizedSearchText)}%");
            }

            string whereClause = predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates);
            command.CommandText = "SELECT CreatedAtUnixTime, Level, Source, Message, Detail FROM Logs" + whereClause + " ORDER BY CreatedAtUnixTime DESC, Id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);

            return ReadLogRecords(command);
        }
    }

    /// <summary>Returns all distinct log sources/categories ordered for filter display.</summary>
    /// <returns>Distinct source names; never null.</returns>
    public IReadOnlyList<string> GetLogSources()
    {
        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT Source FROM Logs ORDER BY Source COLLATE NOCASE ASC;";

            List<string> sources = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    sources.Add(reader.GetString(0));
                }
            }

            return sources;
        }
    }

    private IReadOnlyList<LogRecord> GetRecentLogs(int limit, string? source)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = string.IsNullOrWhiteSpace(source)
                ? "SELECT CreatedAtUnixTime, Level, Source, Message, Detail FROM Logs ORDER BY CreatedAtUnixTime DESC, Id DESC LIMIT $limit;"
                : "SELECT CreatedAtUnixTime, Level, Source, Message, Detail FROM Logs WHERE Source = $source ORDER BY CreatedAtUnixTime DESC, Id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            if (!string.IsNullOrWhiteSpace(source))
            {
                command.Parameters.AddWithValue("$source", source);
            }

            return ReadLogRecords(command);
        }
    }

    /// <summary>Reads log records from a command selecting CreatedAt, Level, Source, Message, and Detail.</summary>
    /// <param name="command">Prepared command. Must not be null.</param>
    /// <returns>Read-only log record list.</returns>
    private static IReadOnlyList<LogRecord> ReadLogRecords(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        using SqliteDataReader reader = command.ExecuteReader();
        List<LogRecord> records = [];
        while (reader.Read())
        {
            records.Add(new LogRecord(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }

        return records;
    }

    private static string? NormalizeOptionalFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string EscapeLikePattern(string value)
    {
        return value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    /// <summary>Deletes records older than <paramref name="cutoff"/> and compacts the database.</summary>
    /// <param name="cutoff">Exclusive upper bound for records to delete; must be a valid timestamp.</param>
    public void CleanupBefore(DateTimeOffset cutoff)
    {
        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            long cutoffUnixTime = cutoff.ToUnixTimeSeconds();

            ExecuteNonQuery(connection, transaction, "DELETE FROM Logs WHERE CreatedAtUnixTime < $cutoff;", ("$cutoff", cutoffUnixTime));
            ExecuteNonQuery(connection, transaction, "DELETE FROM Connections WHERE CreatedAtUnixTime < $cutoff;", ("$cutoff", cutoffUnixTime));
            ExecuteNonQuery(connection, transaction, "DELETE FROM TrafficSnapshots WHERE CreatedAtUnixTime < $cutoff;", ("$cutoff", cutoffUnixTime));
            ExecuteNonQuery(connection, transaction, "DELETE FROM RuleHitStats WHERE UpdatedAtUnixTime < $cutoff;", ("$cutoff", cutoffUnixTime));
            ExecuteNonQuery(connection, transaction, "DELETE FROM NodeHealthStats WHERE UpdatedAtUnixTime < $cutoff;", ("$cutoff", cutoffUnixTime));
            transaction.Commit();
            LogStorageMaintenance.Vacuum(connection);
        }
    }

    /// <summary>Deletes old records until the database is below <paramref name="targetSizeBytes"/> when possible.</summary>
    /// <param name="targetSizeBytes">Desired maximum database size in bytes; must be zero or greater.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetSizeBytes"/> is negative.</exception>
    public void CleanupToSize(long targetSizeBytes)
    {
        if (targetSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes), "Target size must be zero or greater.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();

            while (LogStorageFootprint.CalculateBytes(_databasePath) > targetSizeBytes)
            {
                long deleted = LogStorageMaintenance.DeleteOldestBatch(connection, "Logs", "CreatedAtUnixTime")
                    + LogStorageMaintenance.DeleteOldestBatch(connection, "Connections", "CreatedAtUnixTime")
                    + LogStorageMaintenance.DeleteOldestBatch(connection, "TrafficSnapshots", "CreatedAtUnixTime");

                if (deleted == 0)
                {
                    break;
                }

                LogStorageMaintenance.Vacuum(connection);
            }
        }
    }

    /// <summary>Keeps the newest <paramref name="maxLogCount"/> log records and compacts the database.</summary>
    /// <param name="maxLogCount">Maximum number of log records to keep; must be zero or greater.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLogCount"/> is negative.</exception>
    public void CleanupToLogCount(long maxLogCount)
    {
        if (maxLogCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLogCount), "Maximum log count must be zero or greater.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            ExecuteNonQuery(
                connection,
                null,
                "DELETE FROM Logs WHERE Id NOT IN (SELECT Id FROM Logs ORDER BY CreatedAtUnixTime DESC, Id DESC LIMIT $count);",
                ("$count", maxLogCount));
            LogStorageMaintenance.Vacuum(connection);
        }
    }

    /// <summary>Deletes all persistent log, connection, traffic, and rule-hit records and compacts the database.</summary>
    public void ClearAll()
    {
        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, transaction, "DELETE FROM Logs;");
            ExecuteNonQuery(connection, transaction, "DELETE FROM Connections;");
            ExecuteNonQuery(connection, transaction, "DELETE FROM TrafficSnapshots;");
            ExecuteNonQuery(connection, transaction, "DELETE FROM ProfileTrafficStats;");
            ExecuteNonQuery(connection, transaction, "DELETE FROM NodeTrafficStats;");
            ExecuteNonQuery(connection, transaction, "DELETE FROM NodeHealthStats;");
            ExecuteNonQuery(connection, transaction, "DELETE FROM RuleHitStats;");
            transaction.Commit();
            LogStorageMaintenance.Vacuum(connection);
        }
    }

    /// <summary>Forgets schema initialization after the database file has been deleted externally.</summary>
    internal void ResetAfterDataDeletion()
    {
        lock (_syncLock)
        {
            _isInitialized = false;
        }
    }

    /// <summary>Ensures the SQLite database schema exists before any data operation runs.</summary>
    private void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        using SqliteConnection connection = OpenConnection();
        LogStorageSchema.EnsureCreated(connection);
        _isInitialized = true;
    }

    /// <summary>Opens a SQLite connection to the configured database path.</summary>
    /// <returns>An open <see cref="SqliteConnection"/> instance owned by the caller.</returns>
    private SqliteConnection OpenConnection()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        SqliteConnection connection = new(builder.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Executes a non-query SQL command with optional transaction and parameters.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="transaction">Optional active transaction for the command.</param>
    /// <param name="sql">SQL command text. Must not be null.</param>
    /// <param name="parameters">Command parameters as name-value pairs; may be empty.</param>
    /// <returns>The number of rows affected by the command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="sql"/> is null.</exception>
    private static int ExecuteNonQuery(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>Executes a scalar SQL command and converts the result to a 64-bit integer.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="sql">Scalar SQL command text. Must not be null.</param>
    /// <returns>The scalar result converted to <see cref="long"/>; zero when the result is null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="sql"/> is null.</exception>
    /// <exception cref="FormatException">The returned scalar value cannot be converted to a 64-bit integer.</exception>
    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = command.ExecuteScalar();
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads traffic statistic rows from a command returning label, upload, download, sample count, and update time columns.</summary>
    /// <param name="command">Prepared command to execute. Must not be null.</param>
    /// <returns>Read-only traffic statistic rows.</returns>
    private static IReadOnlyList<TrafficStatisticRow> ReadTrafficStatisticRows(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<TrafficStatisticRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TrafficStatisticRow(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.IsDBNull(4) ? 0 : reader.GetInt64(4))));
        }

        return rows;
    }

    /// <summary>Upserts node traffic counters inside an existing transaction.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="transaction">Active SQLite transaction. Must not be null.</param>
    /// <param name="nodeName">Node name. Must not be null.</param>
    /// <param name="uploadBytes">Uploaded bytes to add.</param>
    /// <param name="downloadBytes">Downloaded bytes to add.</param>
    /// <param name="updatedAt">Updated timestamp in Unix seconds.</param>
    private static void UpsertNodeTraffic(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeName,
        long uploadBytes,
        long downloadBytes,
        long updatedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(nodeName);

        string normalizedNodeName = string.IsNullOrWhiteSpace(nodeName) ? "DIRECT" : nodeName.Trim();
        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT INTO NodeTrafficStats (NodeName, RegionCode, UploadBytes, DownloadBytes, UpdatedAtUnixTime)
            VALUES ($nodeName, NULL, $uploadBytes, $downloadBytes, $updatedAt)
            ON CONFLICT(NodeName) DO UPDATE SET
                UploadBytes = NodeTrafficStats.UploadBytes + excluded.UploadBytes,
                DownloadBytes = NodeTrafficStats.DownloadBytes + excluded.DownloadBytes,
                UpdatedAtUnixTime = excluded.UpdatedAtUnixTime;
            """,
            ("$nodeName", normalizedNodeName),
            ("$uploadBytes", Math.Max(0, uploadBytes)),
            ("$downloadBytes", Math.Max(0, downloadBytes)),
            ("$updatedAt", updatedAt));
    }

    /// <summary>Upserts profile traffic counters inside an existing transaction.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="transaction">Active SQLite transaction. Must not be null.</param>
    /// <param name="profileId">Profile identifier. Must not be null.</param>
    /// <param name="uploadBytes">Uploaded bytes to add.</param>
    /// <param name="downloadBytes">Downloaded bytes to add.</param>
    /// <param name="connectionCount">Connection count to add.</param>
    /// <param name="updatedAt">Updated timestamp in Unix seconds.</param>
    private static void UpsertProfileTraffic(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        long uploadBytes,
        long downloadBytes,
        long connectionCount,
        long updatedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(profileId);

        string normalizedProfileId = string.IsNullOrWhiteSpace(profileId) ? ProfileCatalogIds.BuiltInDirect : profileId.Trim();
        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT INTO ProfileTrafficStats (ProfileId, UploadBytes, DownloadBytes, ConnectionCount, UpdatedAtUnixTime)
            VALUES ($profileId, $uploadBytes, $downloadBytes, $connectionCount, $updatedAt)
            ON CONFLICT(ProfileId) DO UPDATE SET
                UploadBytes = ProfileTrafficStats.UploadBytes + excluded.UploadBytes,
                DownloadBytes = ProfileTrafficStats.DownloadBytes + excluded.DownloadBytes,
                ConnectionCount = ProfileTrafficStats.ConnectionCount + excluded.ConnectionCount,
                UpdatedAtUnixTime = excluded.UpdatedAtUnixTime;
            """,
            ("$profileId", normalizedProfileId),
            ("$uploadBytes", Math.Max(0, uploadBytes)),
            ("$downloadBytes", Math.Max(0, downloadBytes)),
            ("$connectionCount", Math.Max(0, connectionCount)),
            ("$updatedAt", updatedAt));
    }

    /// <summary>Upserts rule hit counters inside an existing transaction.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="transaction">Active SQLite transaction. Must not be null.</param>
    /// <param name="ruleName">Rule key. Must not be null.</param>
    /// <param name="increment">Hit increment.</param>
    /// <param name="updatedAt">Updated timestamp in Unix seconds.</param>
    private static void UpsertRuleHit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ruleName,
        long increment,
        long updatedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(ruleName);

        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return;
        }

        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT INTO RuleHitStats (RuleName, HitCount, UpdatedAtUnixTime)
            VALUES ($ruleName, $increment, $updatedAt)
            ON CONFLICT(RuleName) DO UPDATE SET
                HitCount = RuleHitStats.HitCount + excluded.HitCount,
                UpdatedAtUnixTime = excluded.UpdatedAtUnixTime;
            """,
            ("$ruleName", ruleName.Trim()),
            ("$increment", Math.Max(1, increment)),
            ("$updatedAt", updatedAt));
    }

    /// <summary>Builds the stable rule name used by SQLite rule-hit counters.</summary>
    /// <param name="rule">Rule row to convert.</param>
    /// <returns>Stable rule name.</returns>
    private static string BuildRuleName(RulePreview rule)
    {
        return $"{rule.RuleType},{rule.Payload},{rule.Action}";
    }

    private string GetActiveProfileId()
    {
        return _getActiveProfileId();
    }

}
