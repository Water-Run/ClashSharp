using System.Diagnostics.CodeAnalysis;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies delivery after a real settings commit without opening Windows application storage.</summary>
public sealed class AppSettingsNotificationDeliveryTests
{
    [Fact]
    public async Task Commit_OneFailedObserverDoesNotHideEitherCommittedValueFromLaterObservers()
    {
        AppSettingsService settings = CreateSettings();
        IOException failure = new("First observer unavailable");
        List<string> observed = [];
        settings.SettingChanged += (_, change) =>
        {
            if (change.Key == nameof(settings.NotificationEnabled)) { throw failure; }
        };
        settings.SettingChanged += (sender, change) =>
        {
            Assert.Same(settings, sender);
            Assert.False(settings.NotificationEnabled);
            Assert.Equal(NotificationLevel.CriticalOnly, settings.NotificationLevel);
            observed.Add(change.Key);
        };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ChangeNotificationsAsync(settings)));

        Assert.Equal([nameof(settings.NotificationEnabled), nameof(settings.NotificationLevel)], observed);
    }

    [Fact]
    public async Task Commit_MultipleNotificationFailuresAreReportedAfterAllObserversAndKeys()
    {
        AppSettingsService settings = CreateSettings();
        IOException first = new("First notification failed");
        InvalidOperationException second = new("Second observer failed");
        IOException third = new("Later key notification failed");
        List<string> observed = [];
        settings.SettingChanged += (_, change) => throw (change.Key == nameof(settings.NotificationEnabled) ? first : third);
        settings.SettingChanged += (_, change) =>
        {
            if (change.Key == nameof(settings.NotificationEnabled)) { throw second; }
        };
        settings.SettingChanged += (_, change) => observed.Add(change.Key);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => ChangeNotificationsAsync(settings));

        Assert.Equal([first, second, third], failure.InnerExceptions);
        Assert.Equal([nameof(settings.NotificationEnabled), nameof(settings.NotificationLevel)], observed);
        Assert.False(settings.NotificationEnabled);
        Assert.Equal(NotificationLevel.CriticalOnly, settings.NotificationLevel);
    }

    [Fact]
    public void SynchronousSetter_ReportsFailureAfterDeliveringTheSavedValue()
    {
        AppSettingsService settings = CreateSettings();
        IOException failure = new("Observer unavailable");
        AppSettingChangedEventArgs? observed = null;
        settings.SettingChanged += (_, _) => throw failure;
        settings.SettingChanged += (_, change) => observed = change;

        Assert.Same(failure, Assert.Throws<IOException>(() => settings.NotificationEnabled = false));

        Assert.NotNull(observed);
        Assert.Equal(nameof(settings.NotificationEnabled), observed.Key);
        Assert.Equal(false, observed.NewValue);
        Assert.False(settings.NotificationEnabled);
    }

    [Fact]
    public async Task Reset_NotifiesEveryRemovedKeyEvenWhenAnEarlierCallbackFails()
    {
        Dictionary<string, object> values = new()
        {
            [nameof(AppSettingsService.NotificationEnabled)] = false,
            [nameof(AppSettingsService.NotificationLevel)] = (int)NotificationLevel.CriticalOnly,
            ["OtherOwner"] = "retained",
        };
        AppSettingsService settings = new(values);
        IOException failure = new("Reset observer unavailable");
        List<AppSettingChangedEventArgs> observed = [];
        settings.SettingChanged += (_, change) =>
        {
            if (change.Key == nameof(settings.NotificationEnabled)) { throw failure; }
        };
        settings.SettingChanged += (_, change) => observed.Add(change);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => settings.ResetPreferenceGroupAsync(
            SettingsResetScope.Notifications, CancellationToken.None)));

        Assert.Equal([nameof(settings.NotificationEnabled), nameof(settings.NotificationLevel)], observed.Select(change => change.Key));
        Assert.All(observed, change => { Assert.True(change.WasRemoved); Assert.Null(change.NewValue); });
        Assert.True(settings.NotificationEnabled);
        Assert.Equal(NotificationLevel.Default, settings.NotificationLevel);
        Assert.Equal("retained", Assert.Single(values).Value);
    }

    [Fact]
    public async Task Commit_CancelledObserverStillDrainsNotificationsBeforeExclusiveAdmission()
    {
        AppSettingsService settings = CreateSettings();
        MutationAdmissionBarrier barrier = new();
        settings.ConfigureMutationAdmission(barrier);
        using CancellationTokenSource cancellation = new();
        OperationCanceledException failure = new(cancellation.Token);
        ValueTask<MutationAdmissionLease> exclusive = default;
        List<string> observed = [];
        settings.SettingChanged += (_, change) =>
        {
            if (change.Key != nameof(settings.NotificationEnabled)) { return; }
            cancellation.Cancel();
            exclusive = barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            throw failure;
        };
        settings.SettingChanged += (_, change) =>
        {
            Assert.False(exclusive.IsCompleted);
            observed.Add(change.Key);
        };

        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChangeNotificationsAsync(settings, cancellation.Token)));

        await using MutationAdmissionLease lease = await exclusive;
        Assert.Equal([nameof(settings.NotificationEnabled), nameof(settings.NotificationLevel)], observed);
        Assert.False(settings.NotificationEnabled);
        Assert.Equal(NotificationLevel.CriticalOnly, settings.NotificationLevel);
    }

    [Fact]
    public async Task Commit_SubscriptionChangesApplyToTheNextNotificationWithoutInterruptingTheCurrentOne()
    {
        AppSettingsService settings = CreateSettings();
        IOException failure = new("Observer changed subscriptions then failed");
        List<string> removedObserver = [];
        List<string> addedObserver = [];
        EventHandler<AppSettingChangedEventArgs> removed = (_, change) => removedObserver.Add(change.Key);
        EventHandler<AppSettingChangedEventArgs> added = (_, change) => addedObserver.Add(change.Key);
        settings.SettingChanged += (_, change) =>
        {
            if (change.Key != nameof(settings.NotificationEnabled)) { return; }
            settings.SettingChanged -= removed;
            settings.SettingChanged += added;
            throw failure;
        };
        settings.SettingChanged += removed;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ChangeNotificationsAsync(settings)));

        Assert.Equal([nameof(settings.NotificationEnabled)], removedObserver);
        Assert.Equal([nameof(settings.NotificationLevel)], addedObserver);
    }

    [Fact]
    public async Task Commit_RetryOfAnAlreadySavedValueDoesNotReplayNotifications()
    {
        AppSettingsService settings = CreateSettings();
        IOException failure = new("Observer unavailable");
        int attempts = 0;
        settings.SettingChanged += (_, _) => { attempts++; throw failure; };

        AggregateException original = await Assert.ThrowsAsync<AggregateException>(() => ChangeNotificationsAsync(settings));
        await ChangeNotificationsAsync(settings);

        Assert.Equal([failure, failure], original.InnerExceptions);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage("Design", "CA2201:Do not raise reserved exception types", Justification = "Injects a process-fatal observer failure to verify immediate propagation without continuing callbacks.")]
    public async Task Commit_FatalExceptionGraphStopsFurtherDeliveryWithoutUndoingTheSavedBatch(bool wrapped)
    {
        AppSettingsService settings = CreateSettings();
        OutOfMemoryException fatal = new("Injected fatal observer failure");
        Exception failure = wrapped ? new AggregateException(new IOException("Ordinary failure"), fatal) : fatal;
        List<string> reachedFatalObserver = [];
        int laterCalls = 0;
        settings.SettingChanged += (_, _) => throw new IOException("Earlier observer unavailable");
        settings.SettingChanged += (_, change) => { reachedFatalObserver.Add(change.Key); throw failure; };
        settings.SettingChanged += (_, _) => laterCalls++;

        Exception? actual = await Record.ExceptionAsync(() => ChangeNotificationsAsync(settings));

        Assert.Same(failure, actual);
        Assert.Equal([nameof(settings.NotificationEnabled)], reachedFatalObserver);
        Assert.Equal(0, laterCalls);
        Assert.False(settings.NotificationEnabled);
        Assert.Equal(NotificationLevel.CriticalOnly, settings.NotificationLevel);
    }

    private static AppSettingsService CreateSettings() => new(new Dictionary<string, object>());

    private static Task ChangeNotificationsAsync(AppSettingsService settings, CancellationToken cancellationToken = default) =>
        settings.ApplyChangesAsync(
            [new(SettingsRegistry.Keys.NotificationEnabled, SettingsRegistry.Default.Get(SettingsRegistry.Keys.NotificationEnabled.Value).NormalizeValue(false).Value!),
             new(SettingsRegistry.Keys.NotificationLevel, SettingsRegistry.Default.Get(SettingsRegistry.Keys.NotificationLevel.Value).NormalizeValue(NotificationLevel.CriticalOnly).Value!)],
            cancellationToken);
}
