using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>
/// Subscription reminders: one notification when a limit comes near, one when it is reached, never a repeat for the
/// same period, a new round after a renewal or a quota reset, and nothing from missing metadata.
/// </summary>
public sealed class SubscriptionReminderTests
{
    private const int Seed = 20261110;
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private const long Gib = 1L << 30;

    public static TheoryData<long, int> ExpiryBoundaries() => new()
    {
        { (long)SubscriptionReminderPolicy.ExpiryNotice.TotalSeconds + 1, (int)SubscriptionReminderStage.None },
        { (long)SubscriptionReminderPolicy.ExpiryNotice.TotalSeconds, (int)SubscriptionReminderStage.Approaching },
        { 1, (int)SubscriptionReminderStage.Approaching },
        { 0, (int)SubscriptionReminderStage.Reached },
        { -86400, (int)SubscriptionReminderStage.Reached },
    };

    [Theory]
    [MemberData(nameof(ExpiryBoundaries))]
    public void ExpiryStageFollowsTheNoticePeriodExactly(long secondsFromNow, int expectedStage)
    {
        long expire = Now.ToUnixTimeSeconds() + secondsFromNow;
        SubscriptionReminderState state = SubscriptionReminderPolicy.EvaluateExpiry(Usage(expire: expire), Now);

        Assert.Equal((SubscriptionReminderStage)expectedStage, state.Stage);
        Assert.Equal(expire.ToString(CultureInfo.InvariantCulture), state.Cycle);
    }

    [Fact]
    public void ExpiryWithoutUsableMetadataIsUnknownAndNeverExpiringIsAKnownPeriod()
    {
        Assert.Equal(new SubscriptionReminderState(SubscriptionReminderStage.None, null), SubscriptionReminderPolicy.EvaluateExpiry(null, Now));
        Assert.Equal(new SubscriptionReminderState(SubscriptionReminderStage.None, null), SubscriptionReminderPolicy.EvaluateExpiry(Usage(), Now));
        Assert.Equal(new SubscriptionReminderState(SubscriptionReminderStage.None, null), SubscriptionReminderPolicy.EvaluateExpiry(Usage(expire: -5), Now));
        Assert.Equal(new SubscriptionReminderState(SubscriptionReminderStage.None, null),
            SubscriptionReminderPolicy.EvaluateExpiry(Usage(expire: 253402300800), Now));
        Assert.Equal(new SubscriptionReminderState(SubscriptionReminderStage.None, "0"), SubscriptionReminderPolicy.EvaluateExpiry(Usage(expire: 0), Now));
    }

    [Theory]
    [InlineData(0, 100, (int)SubscriptionReminderStage.None)]
    [InlineData(89, 100, (int)SubscriptionReminderStage.None)]
    [InlineData(90, 100, (int)SubscriptionReminderStage.Approaching)]
    [InlineData(99, 100, (int)SubscriptionReminderStage.Approaching)]
    [InlineData(100, 100, (int)SubscriptionReminderStage.Reached)]
    [InlineData(250, 100, (int)SubscriptionReminderStage.Reached)]
    [InlineData(8999999999, 10000000000, (int)SubscriptionReminderStage.None)]
    [InlineData(9000000000, 10000000000, (int)SubscriptionReminderStage.Approaching)]
    public void QuotaStageFollowsTheUsedShareExactly(long used, long total, int expectedStage)
    {
        // The used share counts upload and download together, however the provider splits them.
        foreach (long upload in new[] { 0L, used / 3, used })
        {
            SubscriptionReminderState state = SubscriptionReminderPolicy.EvaluateQuota(Usage(upload, used - upload, total));
            Assert.Equal((SubscriptionReminderStage)expectedStage, state.Stage);
            Assert.Equal(total.ToString(CultureInfo.InvariantCulture), state.Cycle);
        }
    }

    [Fact]
    public void QuotaWithoutAPlanOrWithoutCountersIsUnknown()
    {
        SubscriptionReminderState unknown = new(SubscriptionReminderStage.None, null);
        Assert.Equal(unknown, SubscriptionReminderPolicy.EvaluateQuota(null));
        Assert.Equal(unknown, SubscriptionReminderPolicy.EvaluateQuota(Usage(1, 1, 0)));
        Assert.Equal(unknown, SubscriptionReminderPolicy.EvaluateQuota(Usage(null, 1, 10)));
        Assert.Equal(unknown, SubscriptionReminderPolicy.EvaluateQuota(Usage(1, null, 10)));
        Assert.Equal(unknown, SubscriptionReminderPolicy.EvaluateQuota(Usage(1, 1, null)));
    }

    [Fact]
    public void QuotaNearLongMaxValueDoesNotOverflow()
    {
        SubscriptionReminderState state = SubscriptionReminderPolicy.EvaluateQuota(Usage(long.MaxValue, long.MaxValue, long.MaxValue));
        Assert.Equal(SubscriptionReminderStage.Reached, state.Stage);
    }

    /// <summary>
    /// Random observation sequences, threaded through the decision exactly as the monitor does, keep the contract:
    /// a notification only for a known limit that is near or reached; never a repeat of an observation just made;
    /// missing metadata changes nothing; within one period the announced stages strictly increase (so at most two);
    /// and a period that starts again announces again.
    /// </summary>
    [Theory]
    [InlineData((int)SubscriptionReminderDimension.Expiry)]
    [InlineData((int)SubscriptionReminderDimension.Quota)]
    public void RandomObservationSequencesKeepTheReminderContract(int dimensionValue)
    {
        SubscriptionReminderDimension dimension = (SubscriptionReminderDimension)dimensionValue;
        Random random = new(Seed + dimensionValue);
        string[] cycles = ["c1", "c2", "c3"];
        for (int run = 0; run < 400; run++)
        {
            SubscriptionReminderState? memory = null;
            SubscriptionReminderStage announcedInPeriod = SubscriptionReminderStage.None;
            string? period = null;
            SubscriptionReminderState? previous = null;
            for (int step = 0; step < 40; step++)
            {
                SubscriptionReminderState current = random.Next(8) == 0
                    ? new(SubscriptionReminderStage.None, null)
                    : new((SubscriptionReminderStage)random.Next(3), cycles[random.Next(random.Next(4) == 0 ? 3 : 1)]);
                (bool notify, SubscriptionReminderState? next) = SubscriptionReminderPolicy.Decide(dimension, memory, current);

                if (current.Cycle is null)
                {
                    Assert.False(notify);
                    Assert.Equal(memory, next);
                }

                if (notify) { Assert.NotEqual(SubscriptionReminderStage.None, current.Stage); }
                if (previous == current && current.Cycle is not null)
                {
                    Assert.False(notify, $"run {run} step {step}: the same observation was announced twice");
                }

                // Track what the user has been told for the current period, from the outside.
                bool restarted = current.Cycle is not null && (period != current.Cycle || next is null);
                if (current.Cycle is not null && period != current.Cycle) { announcedInPeriod = SubscriptionReminderStage.None; }
                if (notify)
                {
                    Assert.True(current.Stage > announcedInPeriod || restarted,
                        $"run {run} step {step}: {current.Stage} announced again in period {current.Cycle}");
                    announcedInPeriod = current.Stage;
                }

                if (next is null) { announcedInPeriod = SubscriptionReminderStage.None; }
                if (current.Cycle is not null) { period = current.Cycle; }
                memory = next;
                previous = current;
            }
        }
    }

    [Fact]
    public void ApproachingThenReachedInOnePeriodAnnouncesBoth()
    {
        foreach (SubscriptionReminderDimension dimension in Enum.GetValues<SubscriptionReminderDimension>())
        {
            (bool first, SubscriptionReminderState? memory) = SubscriptionReminderPolicy.Decide(dimension, null, new(SubscriptionReminderStage.Approaching, "p"));
            (bool second, memory) = SubscriptionReminderPolicy.Decide(dimension, memory, new(SubscriptionReminderStage.Reached, "p"));
            (bool third, _) = SubscriptionReminderPolicy.Decide(dimension, memory, new(SubscriptionReminderStage.Approaching, "p"));

            Assert.True(first);
            Assert.True(second);
            Assert.False(third);
        }
    }

    [Fact]
    public void ARenewalOrAQuotaResetStartsANewRound()
    {
        SubscriptionReminderState reached = new(SubscriptionReminderStage.Reached, "1000");

        // Renewal: the expiry moved, so the old period is forgotten and the new one is announced when near.
        (bool renewedNotify, SubscriptionReminderState? afterRenewal) = SubscriptionReminderPolicy.Decide(
            SubscriptionReminderDimension.Expiry, reached, new(SubscriptionReminderStage.None, "9000"));
        Assert.False(renewedNotify);
        Assert.Null(afterRenewal);
        Assert.True(SubscriptionReminderPolicy.Decide(SubscriptionReminderDimension.Expiry, afterRenewal,
            new(SubscriptionReminderStage.Approaching, "9000")).Notify);

        // Quota reset: the same plan size, but usage fell back under the threshold.
        (bool resetNotify, SubscriptionReminderState? afterReset) = SubscriptionReminderPolicy.Decide(
            SubscriptionReminderDimension.Quota, reached, new(SubscriptionReminderStage.None, "1000"));
        Assert.False(resetNotify);
        Assert.Null(afterReset);
        Assert.True(SubscriptionReminderPolicy.Decide(SubscriptionReminderDimension.Quota, afterReset,
            new(SubscriptionReminderStage.Approaching, "1000")).Notify);

        // An expiry far away in the same period (a clock correction) does not re-arm it.
        Assert.Equal(reached, SubscriptionReminderPolicy.Decide(SubscriptionReminderDimension.Expiry, reached,
            new(SubscriptionReminderStage.None, "1000")).Remember);
    }

    [Fact]
    public void StoredStatesRoundTripAndAnythingMalformedIsForgotten()
    {
        foreach (SubscriptionReminderStage stage in new[] { SubscriptionReminderStage.Approaching, SubscriptionReminderStage.Reached })
        {
            foreach (string cycle in new[] { "0", "1700000000", "214748364800", "a|b" })
            {
                SubscriptionReminderState state = new(stage, cycle);
                Assert.Equal(state, SubscriptionReminderPolicy.Parse(SubscriptionReminderPolicy.Format(state)));
            }
        }

        foreach (string? malformed in new[] { null, "", "|", "1|", "|5", "0|5", "3|5", "-1|5", "x|5", "1", " 1|5", "+1|5" })
        {
            Assert.Null(SubscriptionReminderPolicy.Parse(malformed));
        }

        Random random = new(Seed);
        const string alphabet = "0123456789|-+ ax";
        for (int i = 0; i < 2000; i++)
        {
            string text = new(Enumerable.Range(0, random.Next(8)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            SubscriptionReminderState? parsed = SubscriptionReminderPolicy.Parse(text);
            if (parsed is { } value) { Assert.NotEqual(SubscriptionReminderStage.None, value.Stage); }
        }
    }

    [Fact]
    public void MonitorAnnouncesEachLimitOnceWithItsFactsAndLevel()
    {
        Rig rig = new(Usage(48 * Gib, 47 * Gib, 100 * Gib, Now.AddDays(2).ToUnixTimeSeconds()));

        rig.Monitor.Evaluate();

        Assert.Equal(2, rig.Shown.Count);
        (NotificationLevel level, string title, string message) expiry = rig.Shown.Single(n => n.Title.Contains("ExpiresSoon", StringComparison.Ordinal));
        Assert.Equal(NotificationLevel.Default, expiry.level);
        Assert.Equal($"Demo|{rig.Formatter.Timestamp(Now.AddDays(2), includeSeconds: false)}|2", expiry.message);
        (NotificationLevel level, string title, string message) quota = rig.Shown.Single(n => n.Title.Contains("QuotaLow", StringComparison.Ordinal));
        Assert.Equal(NotificationLevel.Default, quota.level);
        Assert.Equal($"Demo|{rig.Formatter.Bytes(95m * Gib)}|{rig.Formatter.Bytes(100m * Gib)}|{rig.Formatter.Bytes(5m * Gib)}", quota.message);
        Assert.Equal(2, rig.Store.Values.Count);

        // Every later pass in the same state is silent.
        for (int i = 0; i < 5; i++) { rig.Monitor.Evaluate(); }
        Assert.Equal(2, rig.Shown.Count);
    }

    [Fact]
    public void MonitorEscalatesWhenALimitIsReachedAsImportant()
    {
        Rig rig = new(Usage(40 * Gib, 50 * Gib, 100 * Gib, Now.AddHours(30).ToUnixTimeSeconds()));
        rig.Monitor.Evaluate();
        rig.Shown.Clear();

        rig.Time.Advance(TimeSpan.FromHours(31));
        rig.Link = rig.Link with { Usage = Usage(60 * Gib, 45 * Gib, 100 * Gib, Now.AddHours(30).ToUnixTimeSeconds()) };
        rig.Monitor.Evaluate();

        Assert.Equal(2, rig.Shown.Count);
        Assert.All(rig.Shown, n => Assert.Equal(NotificationLevel.CriticalOnly, n.Level));
        Assert.Contains(rig.Shown, n => n.Title.Contains("Expired", StringComparison.Ordinal));
        Assert.Contains(rig.Shown, n => n.Title.Contains("QuotaExhausted", StringComparison.Ordinal)
            && n.Message == $"Demo|{rig.Formatter.Bytes(105m * Gib)}|{rig.Formatter.Bytes(100m * Gib)}");

        rig.Shown.Clear();
        rig.Monitor.Evaluate();
        Assert.Empty(rig.Shown);
    }

    [Fact]
    public void MonitorRemindsAgainAfterARenewalAndAfterAQuotaReset()
    {
        long expire = Now.AddDays(1).ToUnixTimeSeconds();
        Rig rig = new(Usage(0, 95 * Gib, 100 * Gib, expire));
        rig.Monitor.Evaluate();
        Assert.Equal(2, rig.Shown.Count);

        // Renewed for a month and the monthly quota reset: everything is quiet and forgotten.
        long renewed = Now.AddDays(31).ToUnixTimeSeconds();
        rig.Link = rig.Link with { Usage = Usage(0, 1 * Gib, 100 * Gib, renewed) };
        rig.Monitor.Evaluate();
        Assert.Equal(2, rig.Shown.Count);
        Assert.Empty(rig.Store.Values);

        // A month later both limits come near again and are announced again.
        rig.Time.Advance(TimeSpan.FromDays(29));
        rig.Link = rig.Link with { Usage = Usage(0, 91 * Gib, 100 * Gib, renewed) };
        rig.Monitor.Evaluate();
        Assert.Equal(4, rig.Shown.Count);
    }

    [Fact]
    public void MonitorWithoutAnActiveSubscriptionOrMetadataDoesNothing()
    {
        Rig rig = new(null);
        rig.Active = false;
        rig.Monitor.Evaluate();
        Assert.Empty(rig.Shown);
        Assert.Empty(rig.Store.Values);
        Assert.Equal(0, rig.Store.Writes);

        rig.Active = true;
        rig.Monitor.Evaluate();
        Assert.Empty(rig.Shown);
        Assert.Equal(0, rig.Store.Writes);
    }

    [Fact]
    public void MonitorTreatsCorruptMemoryAsNothingRemembered()
    {
        Rig rig = new(Usage(0, 99 * Gib, 100 * Gib));
        rig.Store.Values[SubscriptionReminderMonitor.CreateKey("demo", SubscriptionReminderDimension.Quota)] = "garbage";

        rig.Monitor.Evaluate();

        Assert.Single(rig.Shown);
        Assert.Equal("1|" + (100 * Gib).ToString(CultureInfo.InvariantCulture),
            rig.Store.Values[SubscriptionReminderMonitor.CreateKey("demo", SubscriptionReminderDimension.Quota)]);
    }

    [Fact]
    public void AFailedNotificationIsNotRememberedSoTheNextPassTriesAgain()
    {
        Rig rig = new(Usage(0, 99 * Gib, 100 * Gib));
        rig.FailShow = true;
        Assert.Throws<InvalidOperationException>(rig.Monitor.Evaluate);
        Assert.Empty(rig.Store.Values);

        rig.FailShow = false;
        rig.Monitor.Evaluate();
        Assert.Single(rig.Shown);
    }

    [Fact]
    public void StorageKeysHideTheLinkIdentityAndSeparateTheLimits()
    {
        string expiry = SubscriptionReminderMonitor.CreateKey("secret-link-id", SubscriptionReminderDimension.Expiry);
        string quota = SubscriptionReminderMonitor.CreateKey("secret-link-id", SubscriptionReminderDimension.Quota);

        Assert.DoesNotContain("secret", expiry, StringComparison.Ordinal);
        Assert.NotEqual(expiry, quota);
        Assert.NotEqual(expiry, SubscriptionReminderMonitor.CreateKey("other", SubscriptionReminderDimension.Expiry));
        Assert.All(new[] { expiry, quota }, key => Assert.True(key.Length < 255));
    }

    private static SubscriptionUsage Usage(long? upload = null, long? download = null, long? total = null, long? expire = null) =>
        new(upload, download, total, expire);

    private sealed class Rig
    {
        internal Rig(SubscriptionUsage? usage)
        {
            Link = new ProfileSubscriptionLink("demo", "Demo", "https://example.com/demo", true, 24, Now, "ok", Usage: usage);
            Formatter = new DisplayFormatter(null, () => CultureInfo.InvariantCulture);
            Monitor = new SubscriptionReminderMonitor(() => Active ? Link : null, Store, Show, GetString, Formatter, Time);
        }

        internal ProfileSubscriptionLink Link { get; set; }

        internal bool Active { get; set; } = true;

        internal bool FailShow { get; set; }

        internal ManualClock Time { get; } = new(Now);

        internal MemoryStore Store { get; } = new();

        internal DisplayFormatter Formatter { get; }

        internal List<(NotificationLevel Level, string Title, string Message)> Shown { get; } = [];

        internal SubscriptionReminderMonitor Monitor { get; }

        private void Show(NotificationLevel level, string title, string message)
        {
            if (FailShow) { throw new InvalidOperationException("scripted notification failure"); }
            Shown.Add((level, title, message));
        }

        private static string GetString(string key) => key switch
        {
            "Notification.Subscription.ExpiresSoon.Message.Format" => "{0}|{1}|{2}",
            "Notification.Subscription.Expired.Message.Format" => "{0}|{1}",
            "Notification.Subscription.QuotaLow.Message.Format" => "{0}|{1}|{2}|{3}",
            "Notification.Subscription.QuotaExhausted.Message.Format" => "{0}|{1}|{2}",
            _ => key,
        };
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemoryStore : ISubscriptionReminderStore
    {
        internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        internal int Writes { get; private set; }

        public string? Read(string key) => Values.TryGetValue(key, out string? value) ? value : null;

        public void Write(string key, string? value)
        {
            Writes++;
            if (value is null) { Values.Remove(key); } else { Values[key] = value; }
        }
    }
}
