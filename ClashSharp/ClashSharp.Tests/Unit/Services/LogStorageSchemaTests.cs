/*
 * Log Storage Schema Tests
 * Verifies SQLite log storage schema creation and lightweight migration behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/LogStorageSchemaTests.cs
 * @date: 2026-06-15
 */

using System;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests SQLite schema creation for log storage.</summary>
/// <remarks>
/// Invariants: Tests use in-memory SQLite databases and do not mutate user data.
/// Thread safety: Each test owns its SQLite connection.
/// Side effects: Creates in-memory SQLite tables only.
/// </remarks>
public sealed class LogStorageSchemaTests
{
    /// <summary>Verifies schema creation creates every table used by log storage.</summary>
    [Fact]
    public void EnsureCreated_EmptyDatabase_CreatesExpectedTables()
    {
        using SqliteConnection connection = OpenInMemoryConnection();

        LogStorageSchema.EnsureCreated(connection);

        Assert.True(TableExists(connection, "Logs"));
        Assert.True(TableExists(connection, "Connections"));
        Assert.True(TableExists(connection, "TrafficSnapshots"));
        Assert.True(TableExists(connection, "ProfileTrafficStats"));
        Assert.True(TableExists(connection, "NodeTrafficStats"));
        Assert.True(TableExists(connection, "NodeHealthStats"));
        Assert.True(TableExists(connection, "RuleHitStats"));
    }

    /// <summary>Verifies schema creation is idempotent on an already initialized database.</summary>
    [Fact]
    public void EnsureCreated_ExistingDatabase_IsIdempotent()
    {
        using SqliteConnection connection = OpenInMemoryConnection();

        LogStorageSchema.EnsureCreated(connection);
        LogStorageSchema.EnsureCreated(connection);

        Assert.True(ColumnExists(connection, "ProfileTrafficStats", "ConnectionCount"));
    }

    /// <summary>Verifies older profile traffic tables are migrated with the connection count column.</summary>
    [Fact]
    public void EnsureCreated_LegacyProfileTrafficTable_AddsConnectionCountColumn()
    {
        using SqliteConnection connection = OpenInMemoryConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ProfileTrafficStats (
                ProfileId TEXT PRIMARY KEY,
                UploadBytes INTEGER NOT NULL DEFAULT 0,
                DownloadBytes INTEGER NOT NULL DEFAULT 0,
                UpdatedAtUnixTime INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        LogStorageSchema.EnsureCreated(connection);

        Assert.True(ColumnExists(connection, "ProfileTrafficStats", "ConnectionCount"));
    }

    /// <summary>Opens an in-memory SQLite connection for one test.</summary>
    /// <returns>An open in-memory SQLite connection owned by the caller.</returns>
    private static SqliteConnection OpenInMemoryConnection()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    /// <summary>Returns whether a SQLite table exists.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="tableName">Table name to check. Must not be null.</param>
    /// <returns>True when the table exists.</returns>
    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableName);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>Returns whether a SQLite table contains a column.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="tableName">Table name to inspect. Must not be null.</param>
    /// <param name="columnName">Column name to find. Must not be null.</param>
    /// <returns>True when the column exists.</returns>
    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(columnName);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(1), columnName))
            {
                return true;
            }
        }

        return false;
    }
}
