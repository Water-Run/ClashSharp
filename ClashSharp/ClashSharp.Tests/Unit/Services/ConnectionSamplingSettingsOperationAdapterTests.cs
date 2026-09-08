using System.Reflection;
using System.Threading.Channels;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Runs the production adapter and supervisor against in-memory settings and isolated sampling ports.</summary>
public sealed class ConnectionSamplingSettingsOperationAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferenceObservers_SeeTheCompleteBatchWhileLoopIsQuiescent(bool baseline)
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = Prepare(admission, baseline);
        Clock clock = new();
        ConnectionSamplingService sampling = CreateService(settings, clock);
        ConnectionSamplingSettingsCoordinator coordinator = new(admission, new ConnectionSamplingSettingsOperationAdapter(settings, sampling));
        List<(ConnectionSamplingSettings Settings, bool Running)> notifications = [];
        Task<MutationAdmissionLease>? drain = null;
        void Changed(object? sender, AppSettingChangedEventArgs change)
        {
            notifications.Add((settings.ReadConnectionSamplingSettings(), sampling.IsRunning));
            drain ??= admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
            Assert.False(drain.IsCompleted);
        }

        await sampling.StartAsync(CancellationToken.None);
        if (baseline) { Assert.Equal(TimeSpan.FromSeconds(30), await clock.ReadAsync()); }
        settings.SettingChanged += Changed;
        try
        {
            await coordinator.ApplyAsync(new(!baseline, 60), CancellationToken.None);
            await using MutationAdmissionLease exclusive = await Assert.IsAssignableFrom<Task<MutationAdmissionLease>>(drain);
            Assert.Equal(2, notifications.Count);
            Assert.All(notifications, value =>
            {
                Assert.Equal(new(!baseline, 60), value.Settings);
                Assert.False(value.Running);
            });
            Assert.Equal(!baseline, sampling.IsRunning);
            if (!baseline) { Assert.Equal(TimeSpan.FromSeconds(60), await clock.ReadAsync()); }
        }
        finally
        {
            settings.SettingChanged -= Changed;
            await sampling.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DisableThenEnable_UsesTemporaryQuiescenceAndStartsWithTheNewInterval()
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = Prepare(admission, true);
        Clock clock = new();
        ConnectionSamplingService sampling = CreateService(settings, clock);
        ConnectionSamplingSettingsCoordinator coordinator = new(admission, new ConnectionSamplingSettingsOperationAdapter(settings, sampling));
        try
        {
            await sampling.StartAsync(CancellationToken.None);
            _ = await clock.ReadAsync();
            await coordinator.ApplyAsync(new(false, 60), CancellationToken.None);
            Assert.False(sampling.IsRunning);
            await coordinator.SetEnabledAsync(true, CancellationToken.None);
            Assert.True(sampling.IsRunning);
            Assert.Equal(TimeSpan.FromSeconds(60), await clock.ReadAsync());
        }
        finally
        {
            await sampling.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunningIteration_IsDrainedBeforeEitherPreferenceChanges()
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = Prepare(admission, true);
        Clock clock = new() { ImmediateFirstDelay = true };
        Source source = new() { Block = true };
        ConnectionSamplingService sampling = CreateService(settings, clock, source);
        Task? apply = null;
        bool held = false;
        try
        {
            await sampling.StartAsync(CancellationToken.None);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ConnectionSamplingSettingsCoordinator coordinator = new(admission, new ConnectionSamplingSettingsOperationAdapter(settings, sampling));
            apply = coordinator.ApplyAsync(new(false, 90), CancellationToken.None);
            held = !apply.IsCompleted && settings.ReadConnectionSamplingSettings() == new ConnectionSamplingSettings(true, 30);
        }
        finally
        {
            source.Release.TrySetResult();
            try
            {
                if (apply is not null) { await apply; }
            }
            finally
            {
                await sampling.StopAsync(CancellationToken.None);
            }
        }

        Assert.True(held);
        Assert.Equal(new(false, 90), settings.ReadConnectionSamplingSettings());
        Assert.False(sampling.IsRunning);
    }

    [Fact]
    public async Task TerminallyStoppedLoop_CannotBeReportedAsEnabledAndRestoresDisabledPreference()
    {
        MutationAdmissionBarrier admission = new();
        AppSettingsService settings = Prepare(admission, false);
        ConnectionSamplingService sampling = CreateService(settings, new Clock());
        await sampling.StopAsync(CancellationToken.None);
        ConnectionSamplingSettingsCoordinator coordinator = new(admission, new ConnectionSamplingSettingsOperationAdapter(settings, sampling));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(new(true, 60), CancellationToken.None));

        Assert.Equal(new(false, 30), settings.ReadConnectionSamplingSettings());
        Assert.False(sampling.IsRunning);
    }

    private static AppSettingsService Prepare(MutationAdmissionBarrier admission, bool enabled)
    {
        AppSettingsService settings = AppSettingsService.Instance;
        Assert.Null(typeof(AppSettingsService).GetField("_localSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings));
        settings.ConfigureMutationAdmission(admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        settings.WriteAdmitted(lease, editor =>
        {
            editor.ConnectionSamplingEnabled = enabled;
            editor.ConnectionSamplingIntervalSeconds = 30;
        });
        return settings;
    }

    private static ConnectionSamplingService CreateService(AppSettingsService settings, Clock clock, Source? source = null) =>
        new(new Preferences(settings), source ?? new Source(), new Storage(), key => key, clock);

    private sealed class Preferences(AppSettingsService settings) : IConnectionSamplingSettings
    {
        public bool IsEnabled => settings.ConnectionSamplingEnabled;
        public int IntervalSeconds => settings.ConnectionSamplingIntervalSeconds;
    }

    private sealed class Source : IConnectionSamplingSource
    {
        public bool Block { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (Block) { await Release.Task; }
            return [];
        }
    }

    private sealed class Storage : IConnectionSamplingStorage
    {
        public int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections) => 0;
        public void AppendLog(string level, string category, string message, string? detail) { }
    }

    private sealed class Clock : ISupervisorClock
    {
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
        private int _calls;
        public bool ImmediateFirstDelay { get; init; }
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Assert.True(_delays.Writer.TryWrite(delay));
            return Interlocked.Increment(ref _calls) == 1 && ImmediateFirstDelay
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async Task<TimeSpan> ReadAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            return await _delays.Reader.ReadAsync(timeout.Token);
        }
    }
}
