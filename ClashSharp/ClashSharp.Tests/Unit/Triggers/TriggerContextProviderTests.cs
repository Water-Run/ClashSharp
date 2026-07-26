extern alias ClashSharpUi;
using System.Text.Json;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;
using ITriggerRuntimeContextSource = ClashSharpUi::ClashSharp.Service.ITriggerRuntimeContextSource;
using ITriggerTrafficContextSource = ClashSharpUi::ClashSharp.Service.ITriggerTrafficContextSource;
using RuntimeTrafficRateSnapshot = ClashSharpUi::ClashSharp.Model.RuntimeTrafficRateSnapshot;
using TriggerContextProviderAdapter = ClashSharpUi::ClashSharp.Service.TriggerContextProviderAdapter;
using TriggerTrafficContextSnapshot = ClashSharpUi::ClashSharp.Service.TriggerTrafficContextSnapshot;

namespace ClashSharp.Tests.Unit.Triggers;

/// <summary>Verifies asynchronous, field-typed trigger context acquisition.</summary>
public sealed class TriggerContextProviderTests
{
    [Fact]
    public async Task AcquireAsync_DisabledDefinitionDoesNotInvokeProvider()
    {
        FakeContextProvider provider = new(AvailableContext());
        TriggerContextAcquirer acquirer = new(provider);
        TriggerTaskDefinition definition = Definition(
            isEnabled: false,
            conditions: [ActiveConnectionsCondition()]);

        TriggerContextResult result = await acquirer.AcquireAsync(
            definition,
            TriggerTaskState.CreateInitial(definition),
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        Assert.Equal(TriggerContextStatus.NotRequired, result.Status);
        Assert.Equal("trigger.context.disabled", result.DiagnosticCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task AcquireAsync_PartialFailureIsDegradedWhenAnotherConditionIsDefinitelyFalse()
    {
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                ActiveConnectionsCondition(),
                new TriggerCondition(
                    "runtime",
                    TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromMinutes(1))),
            ]);
        TriggerEvaluationContext context = Context(
            activeConnections: null,
            runtime: TimeSpan.Zero);
        FakeContextProvider provider = new(TriggerContextResult.Degraded(
            context,
            new Dictionary<TriggerDataField, TriggerDataUnavailableReason>
            {
                [TriggerDataField.ActiveConnectionCount] = TriggerDataUnavailableReason.Timeout,
            }));

        TriggerContextResult result = await new TriggerContextAcquirer(provider).AcquireAsync(
            definition,
            TriggerTaskState.CreateInitial(definition),
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        Assert.Equal(TriggerContextStatus.Degraded, result.Status);
        Assert.Equal(TriggerDataUnavailableReason.Timeout, result.UnavailableFields[TriggerDataField.ActiveConnectionCount]);
    }

    [Fact]
    public async Task AcquireAsync_MissingRequiredFieldReturnsTypedUnsoundDecision()
    {
        TriggerTaskDefinition definition = Definition(conditions: [ActiveConnectionsCondition()]);
        FakeContextProvider provider = new(TriggerContextResult.Degraded(
            Context(activeConnections: null),
            new Dictionary<TriggerDataField, TriggerDataUnavailableReason>
            {
                [TriggerDataField.ActiveConnectionCount] = TriggerDataUnavailableReason.Timeout,
            }));

        TriggerContextResult result = await new TriggerContextAcquirer(provider).AcquireAsync(
            definition,
            TriggerTaskState.CreateInitial(definition),
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        Assert.Equal(TriggerContextStatus.Unsound, result.Status);
        Assert.Equal("trigger.context.unsound_decision", result.DiagnosticCode);
        Assert.NotNull(result.Context);
    }

    [Fact]
    public async Task AcquireAsync_PreservesCallerCancellation()
    {
        FakeContextProvider provider = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return AvailableContext();
        });
        TriggerTaskDefinition definition = Definition(conditions: [ActiveConnectionsCondition()]);
        using CancellationTokenSource cancellation = new();

        Task<TriggerContextResult> acquisition = new TriggerContextAcquirer(provider).AcquireAsync(
            definition,
            TriggerTaskState.CreateInitial(definition),
            TriggerEventKind.Periodic,
            null,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
    }

    [Fact]
    public async Task AcquireAsync_AwaitsProviderWithoutBlockingTheCallingThread()
    {
        TaskCompletionSource<object?> entered = Signal();
        TaskCompletionSource<TriggerContextResult> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeContextProvider provider = new((_, _) =>
        {
            entered.TrySetResult(null);
            return release.Task;
        });
        TriggerTaskDefinition definition = Definition(conditions: [ActiveConnectionsCondition()]);

        Task<TriggerContextResult> acquisition = new TriggerContextAcquirer(provider).AcquireAsync(
            definition,
            TriggerTaskState.CreateInitial(definition),
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);
        await entered.Task;

        Assert.False(acquisition.IsCompleted);
        release.SetResult(AvailableContext());
        Assert.Equal(TriggerContextStatus.Available, (await acquisition).Status);
    }

    [Fact]
    public async Task AcquireAsync_RequestsOnlyFieldsUsedByTheDefinition()
    {
        FakeContextProvider provider = new(AvailableContext());
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "window",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(
                        TriggerTrafficScope.RollingWindow,
                        100,
                        TimeSpan.FromMinutes(7))),
                new TriggerCondition(
                    "download",
                    TriggerConditionKind.Rate,
                    new RateConditionParameters(TriggerTrafficDirection.Download, 10)),
                new TriggerCondition(
                    "time",
                    TriggerConditionKind.SystemTime,
                    new SystemTimeConditionParameters(new TimeOnly(10, 0))),
            ]);

        await new TriggerContextAcquirer(provider).AcquireAsync(
            definition,
            TriggerTaskState.CreateInitial(definition),
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        TriggerContextRequest request = Assert.IsType<TriggerContextRequest>(provider.LastRequest);
        Assert.Equal(
            [
                TriggerDataField.LocalDate,
                TriggerDataField.LocalTime,
                TriggerDataField.RollingTraffic,
                TriggerDataField.DownloadBytesPerSecond,
            ],
            request.RequiredFields);
        Assert.Equal([TimeSpan.FromMinutes(7)], request.RollingWindows);
    }

    [Fact]
    public async Task AcquireAsync_NonNotificationEventDoesNotRequestMissingEventData()
    {
        FakeContextProvider provider = new(AvailableContext());
        TriggerTaskDefinition definition = Definition(
            conditions:
            [
                new TriggerCondition(
                    "notification",
                    TriggerConditionKind.Notification,
                    new NotificationConditionParameters(TriggerNotificationLevel.More)),
            ]);

        await new TriggerContextAcquirer(provider).AcquireAsync(
            definition,
            TriggerTaskState.CreateInitial(definition),
            TriggerEventKind.Periodic,
            null,
            CancellationToken.None);

        TriggerContextRequest request = Assert.IsType<TriggerContextRequest>(provider.LastRequest);
        Assert.DoesNotContain(TriggerDataField.NotificationLevel, request.RequiredFields);
    }

    [Theory]
    [MemberData(nameof(ControllerFailures))]
    public async Task Adapter_ControllerFailuresBecomeTypedUnavailableFields(
        Exception exception,
        TriggerDataUnavailableReason expectedReason)
    {
        FakeRuntimeContextSource runtime = new(exception);
        TriggerContextProviderAdapter adapter = Adapter(
            new FakeTrafficContextSource(TrafficSnapshot()),
            runtime);
        TriggerContextRequest request = Request(
            TriggerDataField.UploadBytesPerSecond,
            TriggerDataField.ActiveConnectionCount);

        TriggerContextResult result = await adapter.AcquireAsync(request, CancellationToken.None);

        Assert.Equal(TriggerContextStatus.Degraded, result.Status);
        Assert.Equal(expectedReason, result.UnavailableFields[TriggerDataField.UploadBytesPerSecond]);
        Assert.Equal(expectedReason, result.UnavailableFields[TriggerDataField.ActiveConnectionCount]);
    }

    public static TheoryData<Exception, TriggerDataUnavailableReason> ControllerFailures => new()
    {
        { new TimeoutException(), TriggerDataUnavailableReason.Timeout },
        { new OperationCanceledException(), TriggerDataUnavailableReason.Timeout },
        { new JsonException(), TriggerDataUnavailableReason.MalformedData },
        { new HttpRequestException(), TriggerDataUnavailableReason.SourceUnavailable },
        { new IOException(), TriggerDataUnavailableReason.IoFailure },
    };

    [Theory]
    [MemberData(nameof(StorageFailures))]
    public async Task Adapter_StorageFailuresBecomeTypedUnavailableFields(
        Exception exception,
        TriggerDataUnavailableReason expectedReason)
    {
        TriggerContextProviderAdapter adapter = Adapter(
            new FakeTrafficContextSource(exception),
            new FakeRuntimeContextSource(RuntimeSnapshot()));
        TriggerContextRequest request = Request(
            TriggerDataField.AllTimeTraffic,
            TriggerDataField.RollingTraffic);

        TriggerContextResult result = await adapter.AcquireAsync(request, CancellationToken.None);

        Assert.Equal(TriggerContextStatus.Degraded, result.Status);
        Assert.Equal(expectedReason, result.UnavailableFields[TriggerDataField.AllTimeTraffic]);
        Assert.Equal(expectedReason, result.UnavailableFields[TriggerDataField.RollingTraffic]);
    }

    public static TheoryData<Exception, TriggerDataUnavailableReason> StorageFailures => new()
    {
        { new SqliteException("busy", 5), TriggerDataUnavailableReason.Busy },
        { new SqliteException("error", 1), TriggerDataUnavailableReason.StorageFailure },
        { new IOException(), TriggerDataUnavailableReason.IoFailure },
    };

    [Fact]
    public async Task Adapter_PreservesAvailableRuntimeFieldsWhenStorageFails()
    {
        TriggerContextProviderAdapter adapter = Adapter(
            new FakeTrafficContextSource(new SqliteException("busy", 5)),
            new FakeRuntimeContextSource(RuntimeSnapshot()));
        TriggerContextRequest request = Request(
            TriggerDataField.AllTimeTraffic,
            TriggerDataField.CurrentSessionTraffic,
            TriggerDataField.ActiveConnectionCount);

        TriggerContextResult result = await adapter.AcquireAsync(request, CancellationToken.None);

        Assert.Equal(TriggerContextStatus.Degraded, result.Status);
        Assert.Null(result.Context?.AllTimeTrafficBytes);
        Assert.Equal(700, result.Context?.CurrentSessionTrafficBytes);
        Assert.Equal(3, result.Context?.ActiveConnectionCount);
        Assert.Equal(TriggerDataUnavailableReason.Busy, result.UnavailableFields[TriggerDataField.AllTimeTraffic]);
        Assert.DoesNotContain(TriggerDataField.CurrentSessionTraffic, result.UnavailableFields.Keys);
    }

    [Fact]
    public async Task Adapter_AwaitsSourcesAndDoesNotSubstituteCachedValues()
    {
        TaskCompletionSource<object?> entered = Signal();
        TaskCompletionSource<RuntimeTrafficRateSnapshot> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRuntimeContextSource runtime = new(async cancellationToken =>
        {
            entered.TrySetResult(null);
            return await release.Task.WaitAsync(cancellationToken);
        });
        TriggerContextProviderAdapter adapter = Adapter(
            new FakeTrafficContextSource(TrafficSnapshot()),
            runtime);

        Task<TriggerContextResult> acquisition = adapter.AcquireAsync(
            Request(TriggerDataField.ActiveConnectionCount),
            CancellationToken.None);
        await entered.Task;

        Assert.False(acquisition.IsCompleted);
        release.SetResult(RuntimeSnapshot());
        Assert.Equal(3, (await acquisition).Context?.ActiveConnectionCount);
    }

    [Fact]
    public async Task Adapter_ObservesEveryParallelSourceBeforeCompleting()
    {
        TaskCompletionSource<TriggerTrafficContextSnapshot> trafficRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<RuntimeTrafficRateSnapshot> runtimeRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TriggerContextProviderAdapter adapter = Adapter(
            new FakeTrafficContextSource((_, _, _, cancellationToken) =>
                trafficRelease.Task.WaitAsync(cancellationToken)),
            new FakeRuntimeContextSource(cancellationToken =>
                runtimeRelease.Task.WaitAsync(cancellationToken)));

        Task<TriggerContextResult> acquisition = adapter.AcquireAsync(
            Request(
                TriggerDataField.AllTimeTraffic,
                TriggerDataField.ActiveConnectionCount),
            CancellationToken.None);
        trafficRelease.SetResult(TrafficSnapshot());

        Assert.False(acquisition.IsCompleted);
        runtimeRelease.SetResult(RuntimeSnapshot());
        Assert.Equal(TriggerContextStatus.Available, (await acquisition).Status);
    }

    [Fact]
    public async Task Adapter_PreservesCallerCancellationAcrossSourceReads()
    {
        FakeRuntimeContextSource runtime = new(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return RuntimeSnapshot();
        });
        TriggerContextProviderAdapter adapter = Adapter(
            new FakeTrafficContextSource(TrafficSnapshot()),
            runtime);
        using CancellationTokenSource cancellation = new();

        Task<TriggerContextResult> acquisition = adapter.AcquireAsync(
            Request(TriggerDataField.ActiveConnectionCount),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
    }

    private static TriggerContextProviderAdapter Adapter(
        ITriggerTrafficContextSource traffic,
        ITriggerRuntimeContextSource runtime)
    {
        return new TriggerContextProviderAdapter(
            traffic,
            runtime,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)),
            new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.Zero));
    }

    private static TriggerContextRequest Request(params TriggerDataField[] fields)
    {
        return new TriggerContextRequest(
            TriggerEventKind.Periodic,
            null,
            fields,
            fields.Contains(TriggerDataField.RollingTraffic)
                ? [TimeSpan.FromMinutes(5)]
                : []);
    }

    private static TriggerContextResult AvailableContext()
    {
        return TriggerContextResult.Available(Context(activeConnections: 3));
    }

    private static TriggerEvaluationContext Context(
        int? activeConnections,
        TimeSpan? runtime = null)
    {
        return new TriggerEvaluationContext(
            TriggerEventKind.Periodic,
            new DateOnly(2026, 7, 23),
            new TimeOnly(10, 0),
            activeConnectionCount: activeConnections,
            runtime: runtime);
    }

    private static TriggerCondition ActiveConnectionsCondition()
    {
        return new TriggerCondition(
            "connections",
            TriggerConditionKind.ActiveConnections,
            new ActiveConnectionsConditionParameters(5));
    }

    private static TriggerTaskDefinition Definition(
        bool isEnabled = true,
        IReadOnlyList<TriggerCondition>? conditions = null)
    {
        return new TriggerTaskDefinition(
            "task",
            1,
            "Task",
            isEnabled,
            conditions ?? [ActiveConnectionsCondition()],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("matched")),
            ]);
    }

    private static TriggerTrafficContextSnapshot TrafficSnapshot()
    {
        return new TriggerTrafficContextSnapshot(
            900,
            new Dictionary<TimeSpan, long>
            {
                [TimeSpan.FromMinutes(5)] = 100,
            });
    }

    private static RuntimeTrafficRateSnapshot RuntimeSnapshot()
    {
        return new RuntimeTrafficRateSnapshot(100, 200, 3, 300, 400);
    }

    private static TaskCompletionSource<object?> Signal()
    {
        return new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeContextProvider : ITriggerContextProvider
    {
        private readonly Func<TriggerContextRequest, CancellationToken, Task<TriggerContextResult>> _acquire;

        public FakeContextProvider(TriggerContextResult result)
            : this((_, _) => Task.FromResult(result))
        {
        }

        public FakeContextProvider(
            Func<TriggerContextRequest, CancellationToken, Task<TriggerContextResult>> acquire)
        {
            _acquire = acquire;
        }

        public int CallCount { get; private set; }

        public TriggerContextRequest? LastRequest { get; private set; }

        public Task<TriggerContextResult> AcquireAsync(
            TriggerContextRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return _acquire(request, cancellationToken);
        }
    }

    private sealed class FakeTrafficContextSource : ITriggerTrafficContextSource
    {
        private readonly Func<
            IReadOnlyCollection<TimeSpan>,
            bool,
            DateTimeOffset,
            CancellationToken,
            Task<TriggerTrafficContextSnapshot>> _read;

        public FakeTrafficContextSource(TriggerTrafficContextSnapshot snapshot)
            : this((_, _, _, _) => Task.FromResult(snapshot))
        {
        }

        public FakeTrafficContextSource(Exception exception)
            : this((_, _, _, _) => Task.FromException<TriggerTrafficContextSnapshot>(exception))
        {
        }

        public FakeTrafficContextSource(
            Func<
                IReadOnlyCollection<TimeSpan>,
                bool,
                DateTimeOffset,
                CancellationToken,
                Task<TriggerTrafficContextSnapshot>> read)
        {
            _read = read;
        }

        public Task<TriggerTrafficContextSnapshot> ReadAsync(
            IReadOnlyCollection<TimeSpan> rollingWindows,
            bool includeAllTimeTraffic,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken)
        {
            return _read(rollingWindows, includeAllTimeTraffic, observedAt, cancellationToken);
        }
    }

    private sealed class FakeRuntimeContextSource : ITriggerRuntimeContextSource
    {
        private readonly Func<CancellationToken, Task<RuntimeTrafficRateSnapshot>> _read;

        public FakeRuntimeContextSource(RuntimeTrafficRateSnapshot snapshot)
            : this(_ => Task.FromResult(snapshot))
        {
        }

        public FakeRuntimeContextSource(Exception exception)
            : this(_ => Task.FromException<RuntimeTrafficRateSnapshot>(exception))
        {
        }

        public FakeRuntimeContextSource(
            Func<CancellationToken, Task<RuntimeTrafficRateSnapshot>> read)
        {
            _read = read;
        }

        public Task<RuntimeTrafficRateSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            return _read(cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
