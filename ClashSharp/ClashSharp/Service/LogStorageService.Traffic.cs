using System;
using System.Collections.Generic;
using ClashSharp.Model;
using ClashSharp.Model.Rules;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Service;

public sealed partial class LogStorageService
{
    /// <summary>Commits core-wide totals and observed connection deltas with their durable baselines.</summary>
    /// <remarks>A retry or reopened repository cannot count an already committed snapshot twice.</remarks>
    internal int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot)
    {
        using IDisposable operation = _operations.Enter();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Epoch == Guid.Empty || snapshot.UploadTotalBytes < 0 || snapshot.DownloadTotalBytes < 0)
        {
            throw new ArgumentException("The traffic snapshot counters are invalid.", nameof(snapshot));
        }

        string profileId = snapshot.ProfileId ?? GetActiveProfileId();
        lock (_syncLock)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            string epoch = snapshot.Epoch.ToString("D");
            (string? previousEpoch, long previousUpload, long previousDownload, string? previousIdentity) = ReadTrafficBaseline(connection, transaction);
            bool sameEpoch = StringComparer.Ordinal.Equals(epoch, previousEpoch);
            string identity = snapshot.ConfigurationIdentity ?? profileId;
            // A counter interval spanning a configuration change has no provable per-profile split.
            string attributedProfile = !sameEpoch || StringComparer.Ordinal.Equals(previousIdentity, identity) ? profileId : "unknown";
            long upload = CounterDelta(snapshot.UploadTotalBytes, previousUpload, sameEpoch);
            long download = CounterDelta(snapshot.DownloadTotalBytes, previousDownload, sameEpoch);
            Dictionary<(string Id, long StartedAt), (long Upload, long Download)> previous = sameEpoch
                ? ReadConnectionBaselines(connection, transaction)
                : [];
            // Retain only current connection identities, never hosts or process names, in the checkpoint.
            if (snapshot.ConnectionsComplete || !sameEpoch)
            {
                ExecuteNonQuery(connection, transaction, "DELETE FROM TrafficConnectionCounters;");
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!snapshot.ConnectionsComplete)
            {
                ExecuteNonQuery(connection, transaction, "INSERT INTO TrafficDetailGaps (Id, SampleCount) VALUES (1, 1) ON CONFLICT(Id) DO UPDATE SET SampleCount = SampleCount + 1;");
            }
            int insertedCount = 0;
            int newConnectionCount = 0;
            HashSet<(string, long)> seen = [];
            foreach (ActiveConnection row in snapshot.ConnectionsComplete ? snapshot.Connections : [])
            {
                (string Id, long StartedAt) key = (row.Id, row.StartedAt.UtcTicks);
                if (!seen.Add(key) || row.UploadBytes < 0 || row.DownloadBytes < 0)
                {
                    throw new ArgumentException("The traffic snapshot connections are invalid.", nameof(snapshot));
                }

                bool observedBefore = previous.TryGetValue(key, out var baseline);
                long connectionUpload = CounterDelta(row.UploadBytes, baseline.Upload, observedBefore);
                long connectionDownload = CounterDelta(row.DownloadBytes, baseline.Download, observedBefore);
                ExecuteNonQuery(connection, transaction,
                    "INSERT INTO TrafficConnectionCounters (ConnectionId, StartedAtTicks, UploadBytes, DownloadBytes) VALUES ($id, $startedAt, $upload, $download);",
                    ("$id", row.Id), ("$startedAt", row.StartedAt.UtcTicks),
                    ("$upload", row.UploadBytes), ("$download", row.DownloadBytes));
                if (!observedBefore)
                {
                    newConnectionCount++;
                    UpsertRuleHit(connection, transaction, row.RawRuleDisplay, 1, now);
                    if (snapshot.ConfigurationIdentity is { Length: > 0 } configurationIdentity && configurationIdentity != "unknown")
                    {
                        ExecuteNonQuery(connection, transaction,
                            "INSERT INTO ObservedRuleHits (ConfigurationIdentity, RuleKey, HitCount, UpdatedAtUnixTime) VALUES ($identity, $rule, 1, $now) ON CONFLICT(ConfigurationIdentity, RuleKey) DO UPDATE SET HitCount = HitCount + 1, UpdatedAtUnixTime = excluded.UpdatedAtUnixTime;",
                            ("$identity", configurationIdentity), ("$rule", RuleObservationKey.Create(row.RuleName, row.RulePayload)), ("$now", now));
                    }
                }

                if (!observedBefore || connectionUpload > 0 || connectionDownload > 0)
                {
                    insertedCount += ExecuteNonQuery(connection, transaction,
                        """
                        INSERT INTO Connections (CreatedAtUnixTime, ProcessName, Host, RuleName, ProxyName, UploadBytes, DownloadBytes)
                        VALUES ($createdAt, $process, $host, $rule, $proxy, $upload, $download);
                        """,
                        ("$createdAt", now), ("$process", row.ProcessName), ("$host", row.Host),
                        ("$rule", row.RawRuleDisplay), ("$proxy", row.ProxyName),
                        ("$upload", connectionUpload), ("$download", connectionDownload));
                    UpsertNodeTraffic(connection, transaction, row.ProxyName, connectionUpload, connectionDownload, now);
                }
            }

            if (upload > 0 || download > 0 || newConnectionCount > 0)
            {
                ExecuteNonQuery(connection, transaction,
                    "INSERT INTO TrafficSnapshots (CreatedAtUnixTime, UploadBytes, DownloadBytes) VALUES ($createdAt, $upload, $download);",
                    ("$createdAt", now), ("$upload", upload), ("$download", download));
                UpsertProfileTraffic(connection, transaction, attributedProfile, upload, download, newConnectionCount, now);
            }

            ExecuteNonQuery(connection, transaction,
                """
                INSERT INTO TrafficCounterState (Id, Epoch, UploadBytes, DownloadBytes, ProfileIdentity) VALUES (1, $epoch, $upload, $download, $identity)
                ON CONFLICT(Id) DO UPDATE SET Epoch = excluded.Epoch, UploadBytes = excluded.UploadBytes, DownloadBytes = excluded.DownloadBytes, ProfileIdentity = excluded.ProfileIdentity;
                """,
                ("$epoch", epoch), ("$upload", snapshot.UploadTotalBytes), ("$download", snapshot.DownloadTotalBytes), ("$identity", identity));
            transaction.Commit();
            return insertedCount;
        }
    }

    private static long CounterDelta(long current, long previous, bool sameEpoch) =>
        sameEpoch && current >= previous ? current - previous : current;

    private static (string? Epoch, long Upload, long Download, string? Identity) ReadTrafficBaseline(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Epoch, UploadBytes, DownloadBytes, ProfileIdentity FROM TrafficCounterState WHERE Id = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : (null, 0, 0, null);
    }

    private static Dictionary<(string Id, long StartedAt), (long Upload, long Download)> ReadConnectionBaselines(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ConnectionId, StartedAtTicks, UploadBytes, DownloadBytes FROM TrafficConnectionCounters;";
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<(string, long), (long, long)> counters = [];
        while (reader.Read())
        {
            counters.Add((reader.GetString(0), reader.GetInt64(1)), (reader.GetInt64(2), reader.GetInt64(3)));
        }

        return counters;
    }
}
