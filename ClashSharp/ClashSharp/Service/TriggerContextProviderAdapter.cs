using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;
using RuntimeTrafficRateSnapshot = ClashSharp.Model.RuntimeTrafficRateSnapshot;

namespace ClashSharp.Service;

internal interface ITriggerTrafficContextSource
{
    Task<TriggerTrafficContextSnapshot> ReadAsync(
        IReadOnlyCollection<TimeSpan> rollingWindows,
        bool includeAllTimeTraffic,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}

internal interface ITriggerRuntimeContextSource
{
    Task<RuntimeTrafficRateSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class TriggerTrafficContextSnapshot
{
    public TriggerTrafficContextSnapshot(
        long? allTimeTrafficBytes,
        IReadOnlyDictionary<TimeSpan, long> rollingTrafficBytes)
    {
        ArgumentNullException.ThrowIfNull(rollingTrafficBytes);
        if (allTimeTrafficBytes < 0
            || rollingTrafficBytes.Any(pair => pair.Key <= TimeSpan.Zero || pair.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rollingTrafficBytes));
        }

        AllTimeTrafficBytes = allTimeTrafficBytes;
        RollingTrafficBytes = new ReadOnlyDictionary<TimeSpan, long>(
            new Dictionary<TimeSpan, long>(rollingTrafficBytes));
    }

    public long? AllTimeTrafficBytes { get; }

    public ReadOnlyDictionary<TimeSpan, long> RollingTrafficBytes { get; }
}

/// <summary>Adapts asynchronous controller and SQLite reads to the application trigger contract.</summary>
internal sealed class TriggerContextProviderAdapter : ITriggerContextProvider
{
    private static readonly TriggerDataField[] TrafficFields =
    [
        TriggerDataField.RollingTraffic,
        TriggerDataField.AllTimeTraffic,
    ];

    private static readonly TriggerDataField[] RuntimeFields =
    [
        TriggerDataField.CurrentSessionTraffic,
        TriggerDataField.UploadBytesPerSecond,
        TriggerDataField.DownloadBytesPerSecond,
        TriggerDataField.ActiveConnectionCount,
    ];

    private readonly ITriggerTrafficContextSource _traffic;
    private readonly ITriggerRuntimeContextSource _runtime;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;

    public TriggerContextProviderAdapter(
        ITriggerTrafficContextSource traffic,
        ITriggerRuntimeContextSource runtime,
        TimeProvider timeProvider,
        DateTimeOffset startedAt)
    {
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startedAt = startedAt;
    }

    public async Task<TriggerContextResult> AcquireAsync(
        TriggerContextRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        bool needsTraffic = request.RequiredFields.Any(TrafficFields.Contains);
        bool needsRuntime = request.RequiredFields.Any(RuntimeFields.Contains);
        Task<SourceRead<TriggerTrafficContextSnapshot>>? trafficRead = needsTraffic
            ? ReadTrafficAsync(request, observedAt, cancellationToken)
            : null;
        Task<SourceRead<RuntimeTrafficRateSnapshot>>? runtimeRead = needsRuntime
            ? ReadRuntimeAsync(cancellationToken)
            : null;

        if (trafficRead is not null && runtimeRead is not null)
        {
            await Task.WhenAll(trafficRead, runtimeRead).ConfigureAwait(false);
        }

        SourceRead<TriggerTrafficContextSnapshot> traffic = trafficRead is null
            ? default
            : await trafficRead.ConfigureAwait(false);
        SourceRead<RuntimeTrafficRateSnapshot> runtime = runtimeRead is null
            ? default
            : await runtimeRead.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<TriggerDataField, TriggerDataUnavailableReason> unavailable = [];
        MarkSourceFailures(request, traffic, runtime, unavailable);

        TriggerTrafficContextSnapshot? trafficSnapshot = traffic.HasValue
            ? traffic.Value
            : null;
        IReadOnlyDictionary<TimeSpan, long>? rollingTraffic = trafficSnapshot?.RollingTrafficBytes;
        if (trafficSnapshot is not null
            && request.RequiredFields.Contains(TriggerDataField.RollingTraffic)
            && request.RollingWindows.Any(window => !trafficSnapshot.RollingTrafficBytes.ContainsKey(window)))
        {
            unavailable[TriggerDataField.RollingTraffic] = TriggerDataUnavailableReason.MalformedData;
        }

        if (trafficSnapshot is not null
            && request.RequiredFields.Contains(TriggerDataField.AllTimeTraffic)
            && trafficSnapshot.AllTimeTrafficBytes is null)
        {
            unavailable[TriggerDataField.AllTimeTraffic] =
                TriggerDataUnavailableReason.MalformedData;
        }

        long? currentSessionTraffic = null;
        if (runtime.HasValue
            && request.RequiredFields.Contains(TriggerDataField.CurrentSessionTraffic))
        {
            RuntimeTrafficRateSnapshot runtimeSnapshot = runtime.Value;
            try
            {
                currentSessionTraffic = checked(
                    runtimeSnapshot.SessionUploadBytes + runtimeSnapshot.SessionDownloadBytes);
            }
            catch (OverflowException)
            {
                unavailable[TriggerDataField.CurrentSessionTraffic] =
                    TriggerDataUnavailableReason.MalformedData;
            }
        }

        TriggerNotificationLevel? notificationLevel = request.NotificationLevel;
        if (request.RequiredFields.Contains(TriggerDataField.NotificationLevel)
            && notificationLevel is null)
        {
            unavailable[TriggerDataField.NotificationLevel] =
                TriggerDataUnavailableReason.MissingEventData;
        }

        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(
            observedAt,
            _timeProvider.LocalTimeZone);
        TimeSpan elapsed = observedAt >= _startedAt
            ? observedAt - _startedAt
            : TimeSpan.Zero;
        TriggerEvaluationContext context = new(
            request.EventKind,
            DateOnly.FromDateTime(localNow.DateTime),
            TimeOnly.FromDateTime(localNow.DateTime),
            rollingTraffic,
            request.RequiredFields.Contains(TriggerDataField.CurrentSessionTraffic)
                ? currentSessionTraffic
                : null,
            request.RequiredFields.Contains(TriggerDataField.AllTimeTraffic)
                ? trafficSnapshot?.AllTimeTrafficBytes
                : null,
            request.RequiredFields.Contains(TriggerDataField.UploadBytesPerSecond)
                ? runtime.HasValue ? runtime.Value.UploadBytesPerSecond : null
                : null,
            request.RequiredFields.Contains(TriggerDataField.DownloadBytesPerSecond)
                ? runtime.HasValue ? runtime.Value.DownloadBytesPerSecond : null
                : null,
            request.RequiredFields.Contains(TriggerDataField.ActiveConnectionCount)
                ? runtime.HasValue ? runtime.Value.ActiveConnectionCount : null
                : null,
            request.RequiredFields.Contains(TriggerDataField.Runtime) ? elapsed : null,
            request.RequiredFields.Contains(TriggerDataField.NotificationLevel)
                ? notificationLevel
                : null);
        return unavailable.Count == 0
            ? TriggerContextResult.Available(context)
            : TriggerContextResult.Degraded(context, unavailable);
    }

    private async Task<SourceRead<TriggerTrafficContextSnapshot>> ReadTrafficAsync(
        TriggerContextRequest request,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            TriggerTrafficContextSnapshot snapshot = await _traffic.ReadAsync(
                request.RollingWindows,
                request.RequiredFields.Contains(TriggerDataField.AllTimeTraffic),
                observedAt,
                cancellationToken).ConfigureAwait(false);
            return new SourceRead<TriggerTrafficContextSnapshot>(snapshot, HasValue: true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SourceRead<TriggerTrafficContextSnapshot>(
                default!,
                HasValue: false,
                ClassifyFailure(exception, isStorage: true));
        }
    }

    private async Task<SourceRead<RuntimeTrafficRateSnapshot>> ReadRuntimeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeTrafficRateSnapshot snapshot = await _runtime.ReadAsync(
                cancellationToken).ConfigureAwait(false);
            if (snapshot.UploadBytesPerSecond < 0
                || snapshot.DownloadBytesPerSecond < 0
                || snapshot.ActiveConnectionCount < 0
                || snapshot.SessionUploadBytes < 0
                || snapshot.SessionDownloadBytes < 0)
            {
                return new SourceRead<RuntimeTrafficRateSnapshot>(
                    default,
                    HasValue: false,
                    TriggerDataUnavailableReason.MalformedData);
            }

            return new SourceRead<RuntimeTrafficRateSnapshot>(snapshot, HasValue: true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SourceRead<RuntimeTrafficRateSnapshot>(
                default,
                HasValue: false,
                ClassifyFailure(exception, isStorage: false));
        }
    }

    private static void MarkSourceFailures(
        TriggerContextRequest request,
        SourceRead<TriggerTrafficContextSnapshot> traffic,
        SourceRead<RuntimeTrafficRateSnapshot> runtime,
        IDictionary<TriggerDataField, TriggerDataUnavailableReason> unavailable)
    {
        if (traffic.Failure is TriggerDataUnavailableReason trafficFailure)
        {
            foreach (TriggerDataField field in TrafficFields.Where(request.RequiredFields.Contains))
            {
                unavailable[field] = trafficFailure;
            }
        }

        if (runtime.Failure is TriggerDataUnavailableReason runtimeFailure)
        {
            foreach (TriggerDataField field in RuntimeFields.Where(request.RequiredFields.Contains))
            {
                unavailable[field] = runtimeFailure;
            }
        }
    }

    private static TriggerDataUnavailableReason ClassifyFailure(
        Exception exception,
        bool isStorage)
    {
        return exception switch
        {
            OperationCanceledException or TimeoutException => TriggerDataUnavailableReason.Timeout,
            JsonException or InvalidDataException or FormatException or OverflowException =>
                TriggerDataUnavailableReason.MalformedData,
            IOException => TriggerDataUnavailableReason.IoFailure,
            SqliteException { SqliteErrorCode: 5 or 6 } => TriggerDataUnavailableReason.Busy,
            SqliteException => TriggerDataUnavailableReason.StorageFailure,
            HttpRequestException => TriggerDataUnavailableReason.SourceUnavailable,
            _ when isStorage => TriggerDataUnavailableReason.StorageFailure,
            _ => TriggerDataUnavailableReason.UnexpectedFailure,
        };
    }

    private readonly record struct SourceRead<T>(
        T Value,
        bool HasValue,
        TriggerDataUnavailableReason? Failure);
}

internal sealed class RuntimeTriggerContextSource(RuntimeTrafficRateService runtime)
    : ITriggerRuntimeContextSource
{
    private readonly RuntimeTrafficRateService _runtime = runtime
        ?? throw new ArgumentNullException(nameof(runtime));

    public Task<RuntimeTrafficRateSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        return _runtime.GetSnapshotAsync(cancellationToken);
    }
}

internal sealed class SqliteTriggerTrafficContextSource : ITriggerTrafficContextSource
{
    private readonly string _databasePath;
    private readonly int _busyTimeoutMilliseconds;

    public SqliteTriggerTrafficContextSource(
        string databasePath,
        TimeSpan? busyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        TimeSpan effectiveTimeout = busyTimeout ?? TimeSpan.FromSeconds(2);
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(busyTimeout));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _busyTimeoutMilliseconds = checked((int)Math.Ceiling(effectiveTimeout.TotalMilliseconds));
    }

    public async Task<TriggerTrafficContextSnapshot> ReadAsync(
        IReadOnlyCollection<TimeSpan> rollingWindows,
        bool includeAllTimeTraffic,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(_busyTimeoutMilliseconds / 1000d)),
        };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (SqliteCommand timeout = connection.CreateCommand())
        {
            timeout.CommandText = FormattableString.Invariant(
                $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds};");
            await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long? allTimeTraffic = includeAllTimeTraffic
            ? await ReadScalarAsync(
                connection,
                "SELECT COALESCE(SUM(UploadBytes + DownloadBytes), 0) FROM Connections;",
                null,
                cancellationToken).ConfigureAwait(false)
            : null;
        Dictionary<TimeSpan, long> rollingTraffic = [];
        foreach (TimeSpan window in rollingWindows.Distinct().Order())
        {
            long cutoff = (observedAt - window).ToUnixTimeSeconds();
            rollingTraffic.Add(
                window,
                await ReadScalarAsync(
                    connection,
                    """
                    SELECT COALESCE(SUM(UploadBytes + DownloadBytes), 0)
                    FROM TrafficSnapshots
                    WHERE CreatedAtUnixTime >= $cutoff;
                    """,
                    cutoff,
                    cancellationToken).ConfigureAwait(false));
        }

        return new TriggerTrafficContextSnapshot(allTimeTraffic, rollingTraffic);
    }

    private static async Task<long> ReadScalarAsync(
        SqliteConnection connection,
        string commandText,
        long? cutoff,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        if (cutoff is long suppliedCutoff)
        {
            command.Parameters.AddWithValue("$cutoff", suppliedCutoff);
        }

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return result >= 0
            ? result
            : throw new InvalidDataException("Trigger traffic context cannot be negative.");
    }
}
