/*
 * SQLite Log Storage Schema
 * Owns SQLite schema creation and lightweight migrations for Clash# log storage
 *
 * @author: WaterRun
 * @file: Service/LogStorageSchema.cs
 * @date: 2026-06-15
 */

using System;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Service;

/// <summary>Creates and migrates the SQLite schema used by <see cref="LogStorageService"/>.</summary>
/// <remarks>
/// Invariants: Schema operations are idempotent and safe to run on every storage initialization.
/// Thread safety: Stateless methods are safe for concurrent calls when callers provide independent connections.
/// Side effects: Mutates the SQLite database attached to the supplied connection.
/// </remarks>
internal static class LogStorageSchema
{
    /// <summary>Ensures all log storage tables, indexes, and lightweight migrations exist.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public static void EnsureCreated(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS Logs (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedAtUnixTime INTEGER NOT NULL, Level TEXT NOT NULL, Source TEXT NOT NULL, Message TEXT NOT NULL, Detail TEXT NULL);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Logs_CreatedAtUnixTime ON Logs (CreatedAtUnixTime);");
        ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS Connections (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedAtUnixTime INTEGER NOT NULL, ProcessName TEXT NULL, Host TEXT NOT NULL, RuleName TEXT NULL, ProxyName TEXT NULL, UploadBytes INTEGER NOT NULL DEFAULT 0, DownloadBytes INTEGER NOT NULL DEFAULT 0);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Connections_CreatedAtUnixTime ON Connections (CreatedAtUnixTime);");
        ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS TrafficSnapshots (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedAtUnixTime INTEGER NOT NULL, UploadBytes INTEGER NOT NULL, DownloadBytes INTEGER NOT NULL);");
        ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS ProfileTrafficStats (ProfileId TEXT PRIMARY KEY, UploadBytes INTEGER NOT NULL DEFAULT 0, DownloadBytes INTEGER NOT NULL DEFAULT 0, ConnectionCount INTEGER NOT NULL DEFAULT 0, UpdatedAtUnixTime INTEGER NOT NULL);");
        EnsureColumn(connection, "ProfileTrafficStats", "ConnectionCount", "INTEGER NOT NULL DEFAULT 0");
        ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS NodeTrafficStats (NodeName TEXT PRIMARY KEY, RegionCode TEXT NULL, UploadBytes INTEGER NOT NULL DEFAULT 0, DownloadBytes INTEGER NOT NULL DEFAULT 0, UpdatedAtUnixTime INTEGER NOT NULL);");
        ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS NodeHealthStats (NodeName TEXT PRIMARY KEY, RegionCode TEXT NULL, LatencyMilliseconds INTEGER NULL, UpdatedAtUnixTime INTEGER NOT NULL);");
        ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS RuleHitStats (RuleName TEXT PRIMARY KEY, HitCount INTEGER NOT NULL DEFAULT 0, UpdatedAtUnixTime INTEGER NOT NULL);");
    }

    /// <summary>Executes a schema non-query SQL command.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="sql">SQL command text. Must not be null.</param>
    /// <returns>The number of rows affected by the command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="sql"/> is null.</exception>
    private static int ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sql);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    /// <summary>Ensures an existing SQLite table contains a column added by newer schema versions.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="tableName">Trusted internal table name. Must not be null.</param>
    /// <param name="columnName">Trusted internal column name. Must not be null.</param>
    /// <param name="definition">Trusted internal column definition. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/>, <paramref name="tableName"/>, <paramref name="columnName"/>, or <paramref name="definition"/> is null.</exception>
    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(columnName);
        ArgumentNullException.ThrowIfNull(definition);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(1), columnName))
            {
                return;
            }
        }

        ExecuteNonQuery(connection, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};");
    }
}
