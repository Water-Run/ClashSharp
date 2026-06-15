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
using Windows.Storage;

namespace ClashSharp.Service;

/// <summary>Summarizes the current SQLite log storage footprint and record counts.</summary>
/// <param name="DatabasePath">Absolute path to the SQLite database file; never null.</param>
/// <param name="DatabaseSizeBytes">Current database file size in bytes; zero when the file does not exist.</param>
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

/// <summary>Provides SQLite-backed persistence for logs, connection history, and traffic statistics.</summary>
/// <remarks>
/// Invariants: The database schema is created before public operations query or mutate records.
/// Thread safety: Public methods serialize database access through a private lock.
/// Side effects: Creates and mutates a local SQLite database under the application data directory.
/// </remarks>
public sealed class LogStorageService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="LogStorageService"/> instance.</value>
    public static LogStorageService Instance { get; } = new();

    /// <summary>Synchronization object guarding all SQLite operations for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Absolute path to the SQLite database file cached for this service lifetime.</summary>
    private readonly string _databasePath;

    /// <summary>Tracks whether schema creation has completed for this service instance.</summary>
    private bool _isInitialized;

    /// <summary>Initializes the storage service and computes the database path.</summary>
    private LogStorageService()
    {
        string dataDirectory = ResolveDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        _databasePath = Path.Combine(dataDirectory, "ClashSharpLogs.sqlite3");
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
            long databaseSize = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;

            return new LogStorageSummary(_databasePath, databaseSize, logCount, connectionCount);
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
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        }

        lock (_syncLock)
        {
            EnsureInitialized();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT CreatedAtUnixTime, Level, Source, Message, Detail FROM Logs ORDER BY CreatedAtUnixTime DESC, Id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);

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
            transaction.Commit();
            Vacuum(connection);
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

            while (File.Exists(_databasePath) && new FileInfo(_databasePath).Length > targetSizeBytes)
            {
                long deleted = DeleteOldestBatch(connection, "Logs", "CreatedAtUnixTime")
                    + DeleteOldestBatch(connection, "Connections", "CreatedAtUnixTime")
                    + DeleteOldestBatch(connection, "TrafficSnapshots", "CreatedAtUnixTime");

                if (deleted == 0)
                {
                    break;
                }

                Vacuum(connection);
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
            Vacuum(connection);
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
            ExecuteNonQuery(connection, transaction, "DELETE FROM RuleHitStats;");
            transaction.Commit();
            Vacuum(connection);
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
        ExecuteNonQuery(connection, null, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, null, "CREATE TABLE IF NOT EXISTS Logs (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedAtUnixTime INTEGER NOT NULL, Level TEXT NOT NULL, Source TEXT NOT NULL, Message TEXT NOT NULL, Detail TEXT NULL);");
        ExecuteNonQuery(connection, null, "CREATE INDEX IF NOT EXISTS IX_Logs_CreatedAtUnixTime ON Logs (CreatedAtUnixTime);");
        ExecuteNonQuery(connection, null, "CREATE TABLE IF NOT EXISTS Connections (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedAtUnixTime INTEGER NOT NULL, ProcessName TEXT NULL, Host TEXT NOT NULL, RuleName TEXT NULL, ProxyName TEXT NULL, UploadBytes INTEGER NOT NULL DEFAULT 0, DownloadBytes INTEGER NOT NULL DEFAULT 0);");
        ExecuteNonQuery(connection, null, "CREATE INDEX IF NOT EXISTS IX_Connections_CreatedAtUnixTime ON Connections (CreatedAtUnixTime);");
        ExecuteNonQuery(connection, null, "CREATE TABLE IF NOT EXISTS TrafficSnapshots (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedAtUnixTime INTEGER NOT NULL, UploadBytes INTEGER NOT NULL, DownloadBytes INTEGER NOT NULL);");
        ExecuteNonQuery(connection, null, "CREATE TABLE IF NOT EXISTS ProfileTrafficStats (ProfileId TEXT PRIMARY KEY, UploadBytes INTEGER NOT NULL DEFAULT 0, DownloadBytes INTEGER NOT NULL DEFAULT 0, UpdatedAtUnixTime INTEGER NOT NULL);");
        ExecuteNonQuery(connection, null, "CREATE TABLE IF NOT EXISTS NodeTrafficStats (NodeName TEXT PRIMARY KEY, RegionCode TEXT NULL, UploadBytes INTEGER NOT NULL DEFAULT 0, DownloadBytes INTEGER NOT NULL DEFAULT 0, UpdatedAtUnixTime INTEGER NOT NULL);");
        ExecuteNonQuery(connection, null, "CREATE TABLE IF NOT EXISTS RuleHitStats (RuleName TEXT PRIMARY KEY, HitCount INTEGER NOT NULL DEFAULT 0, UpdatedAtUnixTime INTEGER NOT NULL);");

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

    /// <summary>Deletes a bounded batch of oldest records from <paramref name="tableName"/>.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="tableName">Trusted internal table name. Must not be null.</param>
    /// <param name="orderColumn">Trusted internal order column name. Must not be null.</param>
    /// <returns>The number of rows deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/>, <paramref name="tableName"/>, or <paramref name="orderColumn"/> is null.</exception>
    private static int DeleteOldestBatch(SqliteConnection connection, string tableName, string orderColumn)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(orderColumn);

        string sql = $"DELETE FROM {tableName} WHERE Id IN (SELECT Id FROM {tableName} ORDER BY {orderColumn} ASC, Id ASC LIMIT 5000);";
        return ExecuteNonQuery(connection, null, sql);
    }

    /// <summary>Compacts the SQLite database file after destructive cleanup operations.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    private static void Vacuum(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ExecuteNonQuery(connection, null, "VACUUM;");
    }

    /// <summary>Resolves the directory used for local application records.</summary>
    /// <returns>Absolute directory path for local application data; never null.</returns>
    private static string ResolveDataDirectory()
    {
        try
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClashSharp");
        }
    }
}
