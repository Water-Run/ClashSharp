using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Service;

/// <summary>Provides cleanup helpers for SQLite log storage tables and database compaction.</summary>
/// <remarks>
/// Invariants: Table and column names are trusted internal identifiers supplied by callers.
/// Thread safety: Stateless methods are safe for concurrent calls when callers provide independent connections.
/// Side effects: Deletes rows and compacts the SQLite database attached to the supplied connection.
/// </remarks>
internal static class LogStorageMaintenance
{
    private static readonly string[] HistoryTables = ["Logs", "Connections", "TrafficSnapshots"];

    /// <summary>Deletes one bounded batch in chronological order across the three history tables.</summary>
    public static int DeleteOldestHistoryBatch(SqliteConnection connection, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        using SqliteTransaction transaction = connection.BeginTransaction();
        List<(int Table, long Id)> rows = [];
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT TableNumber, Id FROM (
                    SELECT 0 AS TableNumber, Id, CreatedAtUnixTime FROM Logs
                    UNION ALL SELECT 1, Id, CreatedAtUnixTime FROM Connections
                    UNION ALL SELECT 2, Id, CreatedAtUnixTime FROM TrafficSnapshots
                ) ORDER BY CreatedAtUnixTime, TableNumber, Id LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$limit", batchSize);
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt32(0), reader.GetInt64(1)));
            }
        }

        int deleted = 0;
        for (int table = 0; table < HistoryTables.Length; table++)
        {
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {HistoryTables[table]} WHERE Id = $id;";
            SqliteParameter id = delete.Parameters.Add("$id", SqliteType.Integer);
            foreach ((int rowTable, long rowId) in rows)
            {
                if (rowTable == table)
                {
                    id.Value = rowId;
                    deleted += delete.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
        return deleted;
    }

    /// <summary>Compacts storage only after all WAL pages can be checkpointed.</summary>
    /// <returns>False when another reader prevents reclaiming WAL space.</returns>
    public static bool TryVacuumForSizeCleanup(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!TryTruncateWal(connection))
        {
            return false;
        }

        ExecuteNonQuery(connection, "VACUUM;");
        return TryTruncateWal(connection);
    }

    private static bool TryTruncateWal(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt32(0) == 0;
    }

    /// <summary>Deletes a bounded batch of oldest records from <paramref name="tableName"/>.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <param name="tableName">Trusted internal table name. Must not be null.</param>
    /// <param name="orderColumn">Trusted internal order column name. Must not be null.</param>
    /// <param name="batchSize">Maximum rows to delete; must be greater than zero.</param>
    /// <returns>The number of rows deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/>, <paramref name="tableName"/>, or <paramref name="orderColumn"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is less than or equal to zero.</exception>
    public static int DeleteOldestBatch(SqliteConnection connection, string tableName, string orderColumn, int batchSize = 5000)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(orderColumn);

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than zero.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {tableName} WHERE Id IN (SELECT Id FROM {tableName} ORDER BY {orderColumn} ASC, Id ASC LIMIT $limit);";
        command.Parameters.AddWithValue("$limit", batchSize);
        return command.ExecuteNonQuery();
    }

    /// <summary>Compacts the SQLite database and truncates WAL sidecar content when possible.</summary>
    /// <param name="connection">Open SQLite connection. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public static void Vacuum(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        ExecuteNonQuery(connection, "VACUUM;");
        ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>Executes a maintenance non-query SQL command.</summary>
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
}
