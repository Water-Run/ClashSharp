using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Service;

public sealed partial class LogStorageService
{
    /// <summary>Counts the records removed by date cleanup without modifying storage.</summary>
    /// <param name="cutoff">Exclusive timestamp boundary, matching <see cref="CleanupBefore"/>.</param>
    /// <returns>The number of matching log, connection, traffic, health, and rule-hit records.</returns>
    public long PreviewCleanupBefore(DateTimeOffset cutoff)
    {
        using IDisposable operation = _operations.Enter();
        lock (_syncLock)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM Logs WHERE CreatedAtUnixTime < $cutoff)
                     + (SELECT COUNT(*) FROM Connections WHERE CreatedAtUnixTime < $cutoff)
                     + (SELECT COUNT(*) FROM TrafficSnapshots WHERE CreatedAtUnixTime < $cutoff)
                     + (SELECT COUNT(*) FROM NodeHealthStats WHERE UpdatedAtUnixTime < $cutoff)
                     + (SELECT COUNT(*) FROM RuleHitStats WHERE UpdatedAtUnixTime < $cutoff);
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Counts logs beyond the retained newest records without deleting them.</summary>
    /// <param name="maxLogCount">Maximum retained log count; zero or greater.</param>
    /// <returns>The number of logs that count cleanup would delete.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The retained count is negative.</exception>
    public long PreviewCleanupToLogCount(long maxLogCount)
    {
        using IDisposable operation = _operations.Enter();
        ArgumentOutOfRangeException.ThrowIfNegative(maxLogCount);
        lock (_syncLock)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            return Math.Max(0, ExecuteScalarLong(connection, "SELECT COUNT(*) FROM Logs;") - maxLogCount);
        }
    }

    /// <summary>Counts all history records cleared by <see cref="ClearAll"/>.</summary>
    /// <returns>The number of removable records, excluding retained live counter baselines.</returns>
    public long PreviewClearAll()
    {
        using IDisposable operation = _operations.Enter();
        lock (_syncLock)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            return ExecuteScalarLong(connection, """
                SELECT (SELECT COUNT(*) FROM Logs)
                     + (SELECT COUNT(*) FROM Connections)
                     + (SELECT COUNT(*) FROM TrafficSnapshots)
                     + (SELECT COUNT(*) FROM ProfileTrafficStats)
                     + (SELECT COUNT(*) FROM NodeTrafficStats)
                     + (SELECT COUNT(*) FROM NodeHealthStats)
                     + (SELECT COUNT(*) FROM RuleHitStats);
                """);
        }
    }
}
