/*
 * Log Storage Maintenance Tests
 * Verifies SQLite cleanup helper behavior for log storage maintenance
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/LogStorageMaintenanceTests.cs
 * @date: 2026-06-15
 */

using System;
using ClashSharp.Service;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests SQLite log storage maintenance helpers.</summary>
/// <remarks>
/// Invariants: Tests use in-memory SQLite databases and do not mutate user data.
/// Thread safety: Each test owns its SQLite connection.
/// Side effects: Creates in-memory SQLite tables only.
/// </remarks>
public sealed class LogStorageMaintenanceTests
{
    /// <summary>Verifies oldest rows are deleted according to the supplied order column and batch size.</summary>
    [Fact]
    public void DeleteOldestBatch_DeletesOldestRowsOnly()
    {
        using SqliteConnection connection = OpenInMemoryConnection();
        CreateSampleTable(connection);
        InsertSampleRow(connection, 30);
        InsertSampleRow(connection, 10);
        InsertSampleRow(connection, 20);

        int deleted = LogStorageMaintenance.DeleteOldestBatch(connection, "Logs", "CreatedAtUnixTime", batchSize: 2);

        Assert.Equal(2, deleted);
        Assert.Equal(1, CountRows(connection));
        Assert.Equal(30, ReadOnlyTimestamp(connection));
    }

    /// <summary>Verifies invalid batch size is rejected before SQL is executed.</summary>
    [Fact]
    public void DeleteOldestBatch_InvalidBatchSize_Throws()
    {
        using SqliteConnection connection = OpenInMemoryConnection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LogStorageMaintenance.DeleteOldestBatch(connection, "Logs", "CreatedAtUnixTime", batchSize: 0));
    }

    /// <summary>Verifies vacuum maintenance can run against a valid SQLite database.</summary>
    [Fact]
    public void Vacuum_ValidConnection_Completes()
    {
        using SqliteConnection connection = OpenInMemoryConnection();
        CreateSampleTable(connection);

        LogStorageMaintenance.Vacuum(connection);

        Assert.True(true);
    }

    /// <summary>Opens an in-memory SQLite connection for one test.</summary>
    /// <returns>An open in-memory SQLite connection owned by the caller.</returns>
    private static SqliteConnection OpenInMemoryConnection()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    /// <summary>Creates a minimal table compatible with maintenance batch deletion.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    private static void CreateSampleTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Logs (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedAtUnixTime INTEGER NOT NULL);";
        command.ExecuteNonQuery();
    }

    /// <summary>Inserts one sample row into the test table.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="timestamp">Timestamp value to insert.</param>
    private static void InsertSampleRow(SqliteConnection connection, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Logs (CreatedAtUnixTime) VALUES ($timestamp);";
        command.Parameters.AddWithValue("$timestamp", timestamp);
        command.ExecuteNonQuery();
    }

    /// <summary>Counts rows in the test table.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <returns>Current row count.</returns>
    private static long CountRows(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Logs;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Reads the only remaining timestamp from the test table.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <returns>The remaining timestamp value.</returns>
    private static long ReadOnlyTimestamp(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CreatedAtUnixTime FROM Logs;";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
