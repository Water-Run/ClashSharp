using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Strings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Exercises visible statistics states and overlapping background reads without Windows state.</summary>
public sealed class StatisticsRefreshViewModelTests
{
    private static readonly DateTimeOffset RefreshTime = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Construction_DoesNotReadOrClaimEmptyStatistics()
    {
        StatisticsStore store = new();
        StatisticsViewModel viewModel = Create(store);

        Assert.Equal(0, store.ReadCount);
        Assert.False(viewModel.HasSnapshot);
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanRefresh);
        Assert.False(viewModel.HasLoadError);
        Assert.Empty(viewModel.StatusText);
        Assert.Empty(viewModel.TotalTrafficText);
        AssertNoEmptyClaims(viewModel);
    }

    [Fact]
    public async Task InitialRead_ShowsLoadingUntilAnEmptySnapshotIsReady()
    {
        using ReadGate gate = new();
        StatisticsStore store = new() { ReadSummary = () => gate.Read(default) };
        StatisticsViewModel viewModel = Create(store);

        Task load = viewModel.LoadAsync(CancellationToken.None);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(viewModel.IsLoading);
            Assert.False(viewModel.CanRefresh);
            Assert.False(viewModel.HasSnapshot);
            Assert.Equal("Loading statistics", viewModel.StatusText);
            AssertNoEmptyClaims(viewModel);
        }
        finally
        {
            gate.Release();
        }
        await load;

        Assert.True(viewModel.HasSnapshot);
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanRefresh);
        Assert.True(viewModel.HasNoProfileTraffic);
        Assert.True(viewModel.HasNoDailyTraffic);
        Assert.True(viewModel.HasNoNodeTraffic);
        Assert.Equal("0.0 B / 0.0 B", viewModel.TotalTrafficText);
        Assert.Equal($"Updated {RefreshTime.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)}", viewModel.StatusText);
    }

    [Fact]
    public async Task FirstReadFailure_ShowsAnErrorAndCanRecoverOnRefresh()
    {
        StatisticsStore store = new() { ReadSummary = () => throw new IOException("private path") };
        TestApplicationErrorSink sink = new();
        StatisticsViewModel viewModel = Create(store, sink);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(viewModel.HasLoadError);
        Assert.Equal("Could not load statistics", viewModel.LoadErrorText);
        Assert.True(viewModel.CanRefresh);
        Assert.False(viewModel.HasSnapshot);
        Assert.Empty(viewModel.TotalTrafficText);
        AssertNoEmptyClaims(viewModel);
        Assert.Equal("statistics-load", Assert.Single(sink.Errors).OperationName);

        store.ReadSummary = () => Summary(7);
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.False(viewModel.HasLoadError);
        Assert.Empty(viewModel.LoadErrorText);
        Assert.True(viewModel.HasSnapshot);
        Assert.Equal("7 connections", viewModel.ConnectionCountText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshFailure_PreservesTheEntireLastSuccessfulSnapshot(bool failDuringFormatting)
    {
        StatisticsStore store = WithRows();
        Localization localization = new();
        TestApplicationErrorSink sink = new();
        DateTimeOffset now = RefreshTime;
        StatisticsViewModel viewModel = Create(store, sink, localization, () => now);
        await viewModel.LoadAsync(CancellationToken.None);
        DisplayedSnapshot previous = Capture(viewModel);

        now = now.AddMinutes(5);
        store.ReadSummary = failDuringFormatting ? () => Summary(42) : () => throw new IOException("read failed");
        store.ProfileRows = [];
        store.DailyRows = [];
        store.NodeRows = [];
        localization.BrokenRuleFormat = failDuringFormatting;
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(previous, Capture(viewModel));
        Assert.True(viewModel.HasLoadError);
        Assert.Equal("Showing the last loaded statistics", viewModel.LoadErrorText);
        Assert.True(viewModel.CanRefresh);
        AssertNoEmptyClaims(viewModel);
        Assert.Single(sink.Errors);
    }

    [Fact]
    public async Task Refresh_PublicationNotifiesObserversAfterAllValuesAreInstalled()
    {
        StatisticsStore store = WithRows();
        TestApplicationErrorSink sink = new();
        StatisticsViewModel viewModel = Create(store, sink);
        bool observed = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(StatisticsViewModel.TotalTrafficText)) { return; }
            observed = true;
            Assert.True(viewModel.HasSnapshot);
            Assert.Equal("7 connections", viewModel.ConnectionCountText);
            Assert.Equal("8 profiles", viewModel.ProfileStatisticText);
            Assert.Equal("9 snapshots", viewModel.SnapshotStatisticText);
            Assert.Equal("10 nodes / 11 health", viewModel.NodeStatisticText);
            Assert.Equal("12 rules", viewModel.RuleStatisticText);
            Assert.Equal("Current profile", Assert.Single(viewModel.ProfileTrafficRows).Label);
            Assert.Equal(new DateOnly(2026, 9, 30).ToString("d", CultureInfo.CurrentCulture), Assert.Single(viewModel.DailyTrafficRows).Label);
            Assert.Equal("Node A", Assert.Single(viewModel.NodeTrafficRows).Label);
        };

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(observed);
        Assert.Empty(sink.Errors);
        AssertNoEmptyClaims(viewModel);
    }

    [Fact]
    public async Task EmptyMessages_AreIndependentForEachBreakdown()
    {
        StatisticsStore store = WithRows();
        store.DailyRows = [];
        StatisticsViewModel viewModel = Create(store);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.False(viewModel.HasNoProfileTraffic);
        Assert.True(viewModel.HasNoDailyTraffic);
        Assert.False(viewModel.HasNoNodeTraffic);
        Assert.True(viewModel.HasNoRuleHits);
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.SimplifiedChinese)]
    [InlineData(AppLanguage.TraditionalChinese)]
    [InlineData(AppLanguage.German)]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Korean)]
    [InlineData(AppLanguage.Persian)]
    public async Task Rows_UseAuthoredLabelsAndDistinguishConnectionsFromSnapshots(AppLanguage language)
    {
        StatisticsStore store = WithRows();
        store.ReadRuleHits = () => new Dictionary<string, long> { ["MATCH"] = 0 };
        CatalogLocalization localization = new(language);
        StatisticsViewModel viewModel = new(localization, store, new Profiles(), static () => { },
            new TestApplicationErrorSink(), new ModelDisplayMapper(static text => text));

        await viewModel.LoadAsync(CancellationToken.None);

        StatisticsTrafficRowDisplay profile = Assert.Single(viewModel.ProfileTrafficRows);
        StatisticsTrafficRowDisplay daily = Assert.Single(viewModel.DailyTrafficRows);
        StatisticsTrafficRowDisplay node = Assert.Single(viewModel.NodeTrafficRows);
        Assert.Equal(store.ProfileRows[0], profile.Model);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Statistics.Total.Format"), "3.0 B"), profile.TotalDisplay);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Statistics.TotalTraffic.Format"), "1.0 B", "2.0 B"), profile.TransferDisplay);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Statistics.ConnectionCount.Format"), 3), profile.SampleCountDisplay);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Statistics.SnapshotCount.Format"), 3), daily.SampleCountDisplay);
        Assert.NotEqual(profile.SampleCountDisplay, daily.SampleCountDisplay);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Statistics.Updated.Format"), RefreshTime.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)), node.UpdatedAtDisplay);
        Assert.Equal(0, Assert.Single(viewModel.RuleHitRows).HitCount);
        Assert.False(viewModel.HasNoRuleHits);
        Assert.DoesNotContain("Statistics.", viewModel.ByRuleTitleText);
        Assert.DoesNotContain("Statistics.", Assert.Single(viewModel.RuleHitRows).HitCountDisplay);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    public async Task RuleHits_AreBoundedAndOrderedBeforeDisplayFiltering(int count)
    {
        StatisticsStore store = WithRows();
        Dictionary<string, long> hits = Enumerable.Range(0, count).Reverse()
            .ToDictionary(index => $"private-{index:D2}", index => index / 2L, StringComparer.Ordinal);
        store.ReadRuleHits = () => hits;
        StatisticsViewModel viewModel = new(new Localization(), store, new Profiles(), static () => { },
            new TestApplicationErrorSink(), new ModelDisplayMapper(static _ => "filtered"));

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(Math.Min(10, count), viewModel.RuleHitRows.Count);
        Assert.Equal(hits.OrderByDescending(row => row.Value).ThenBy(row => row.Key, StringComparer.Ordinal)
            .Take(10).Select(row => row.Value), viewModel.RuleHitRows.Select(row => row.HitCount));
        Assert.All(viewModel.RuleHitRows, row => Assert.Equal("filtered", row.Label));
        Assert.Equal("filtered", Assert.Single(viewModel.NodeTrafficRows).Label);
        Assert.Equal("Node A", Assert.Single(viewModel.NodeTrafficRows).Model.Label);
        Assert.Equal("profile-1", Assert.Single(viewModel.ProfileTrafficRows).Model.Label);
        Assert.Equal(count, hits.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuleHitFailure_PreservesTheEntireSnapshotAndDoesNotClaimEmpty(bool failFormatting)
    {
        StatisticsStore store = WithRows();
        store.ReadRuleHits = () => new Dictionary<string, long> { ["MATCH"] = 17 };
        Localization localization = new();
        TestApplicationErrorSink sink = new();
        StatisticsViewModel viewModel = Create(store, sink, localization);
        await viewModel.LoadAsync(CancellationToken.None);
        DisplayedSnapshot previous = Capture(viewModel);

        store.ProfileRows = [];
        store.DailyRows = [];
        store.NodeRows = [];
        store.ReadRuleHits = failFormatting
            ? () => new Dictionary<string, long> { ["REPLACEMENT"] = 99 }
            : () => throw new IOException("Synthetic rule read failure.");
        localization.BrokenHitFormat = failFormatting;
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(previous, Capture(viewModel));
        Assert.True(viewModel.HasLoadError);
        Assert.False(viewModel.HasNoRuleHits);
        Assert.Equal("MATCH", Assert.Single(viewModel.RuleHitRows).Label);
        Assert.Single(sink.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedRead_CannotPublishOldDataOrAnOldFailure(bool failOldRead)
    {
        using ReadGate gate = new();
        StatisticsStore store = new() { ReadSummary = () => gate.Read(Summary(1), failOldRead) };
        TestApplicationErrorSink sink = new();
        StatisticsViewModel viewModel = Create(store, sink);
        Task oldLoad = viewModel.LoadAsync(CancellationToken.None);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.ReadSummary = () => Summary(2);
            await viewModel.LoadAsync(CancellationToken.None);
            Assert.Equal("2 connections", viewModel.ConnectionCountText);
        }
        finally
        {
            gate.Release();
        }
        await oldLoad;

        Assert.Equal("2 connections", viewModel.ConnectionCountText);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasLoadError);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public async Task OldReadCompletion_DoesNotClearTheNewReadsBusyState()
    {
        using ReadGate first = new();
        using ReadGate second = new();
        StatisticsStore store = new() { ReadSummary = () => first.Read(Summary(1)) };
        StatisticsViewModel viewModel = Create(store);
        Task oldLoad = viewModel.LoadAsync(CancellationToken.None);
        Task newLoad = Task.CompletedTask;
        try
        {
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.ReadSummary = () => second.Read(Summary(2));
            newLoad = viewModel.LoadAsync(CancellationToken.None);
            await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            first.Release();
            await oldLoad;

            Assert.True(viewModel.IsLoading);
            Assert.False(viewModel.CanRefresh);
            Assert.False(viewModel.HasSnapshot);
            Assert.Empty(viewModel.ConnectionCountText);
        }
        finally
        {
            first.Release();
            second.Release();
        }
        await newLoad;

        Assert.Equal("2 connections", viewModel.ConnectionCountText);
        Assert.True(viewModel.CanRefresh);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRead_DoesNotPublishDataOrShowAnErrorAfterLeaving(bool failRead)
    {
        using ReadGate gate = new();
        using CancellationTokenSource cancellation = new();
        StatisticsStore store = new() { ReadSummary = () => gate.Read(Summary(1), failRead) };
        TestApplicationErrorSink sink = new();
        StatisticsViewModel viewModel = Create(store, sink);
        Task load = viewModel.LoadAsync(cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        finally
        {
            gate.Release();
        }
        await load;

        Assert.False(viewModel.HasSnapshot);
        Assert.False(viewModel.HasLoadError);
        Assert.True(viewModel.CanRefresh);
        Assert.Empty(viewModel.StatusText);
        Assert.Empty(sink.Errors);
        AssertNoEmptyClaims(viewModel);
    }

    [Fact]
    public async Task AlreadyCancelledRefresh_LeavesTheVisibleErrorAndDataUntouched()
    {
        StatisticsStore store = new() { ReadSummary = () => throw new IOException("read failed") };
        StatisticsViewModel viewModel = Create(store);
        await viewModel.LoadAsync(CancellationToken.None);
        int reads = store.ReadCount;

        await viewModel.LoadAsync(new CancellationToken(canceled: true));

        Assert.Equal(reads, store.ReadCount);
        Assert.True(viewModel.HasLoadError);
        Assert.True(viewModel.CanRefresh);
    }

    private static StatisticsViewModel Create(
        StatisticsStore store,
        IApplicationErrorSink? sink = null,
        Localization? localization = null,
        Func<DateTimeOffset>? getNow = null) => new(
            localization ?? new Localization(), store, new Profiles(), static () => { },
            sink ?? new TestApplicationErrorSink(), new ModelDisplayMapper(static text => text),
            getNow ?? (() => RefreshTime));

    private static StatisticsSummary Summary(long connections) => new(1024, 2048, connections, 9, 8, 10, 11, 12);

    private static StatisticsStore WithRows() => new()
    {
        ReadSummary = () => Summary(7),
        ProfileRows = [new("profile-1", 1, 2, 3, RefreshTime)],
        DailyRows = [new("2026-09-30", 1, 2, 3, RefreshTime)],
        NodeRows = [new("Node A", 1, 2, 3, RefreshTime)],
    };

    private static void AssertNoEmptyClaims(StatisticsViewModel viewModel)
    {
        Assert.False(viewModel.HasNoProfileTraffic);
        Assert.False(viewModel.HasNoDailyTraffic);
        Assert.False(viewModel.HasNoNodeTraffic);
    }

    private static DisplayedSnapshot Capture(StatisticsViewModel viewModel) => new(
        viewModel.TotalTrafficText, viewModel.ConnectionCountText, viewModel.ProfileStatisticText,
        viewModel.SnapshotStatisticText, viewModel.NodeStatisticText, viewModel.RuleStatisticText,
        viewModel.ProfileTrafficRows, viewModel.DailyTrafficRows, viewModel.NodeTrafficRows, viewModel.RuleHitRows, viewModel.StatusText);

    private sealed record DisplayedSnapshot(
        string Total, string Connections, string Profiles, string Snapshots, string Nodes, string Rules,
        IReadOnlyList<StatisticsTrafficRowDisplay> ProfileRows, IReadOnlyList<StatisticsTrafficRowDisplay> DailyRows,
        IReadOnlyList<StatisticsTrafficRowDisplay> NodeRows, IReadOnlyList<StatisticsRuleHitDisplay> RuleRows, string Status);

    private sealed class StatisticsStore : IStatisticsStore
    {
        private int _readCount;
        public int ReadCount => _readCount;
        public Func<StatisticsSummary> ReadSummary { get; set; } = static () => default;
        public IReadOnlyList<TrafficStatisticRow> ProfileRows { get; set; } = [];
        public IReadOnlyList<TrafficStatisticRow> DailyRows { get; set; } = [];
        public IReadOnlyList<TrafficStatisticRow> NodeRows { get; set; } = [];
        public Func<IReadOnlyDictionary<string, long>> ReadRuleHits { get; set; } = static () => new Dictionary<string, long>();

        public StatisticsSummary GetTrafficStatisticsSummary()
        {
            Interlocked.Increment(ref _readCount);
            return ReadSummary();
        }

        public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit) => ProfileRows;
        public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit) => DailyRows;
        public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit) => NodeRows;
        public IReadOnlyDictionary<string, long> GetRuleHitCounts() => ReadRuleHits();
    }

    private sealed class Profiles : IStatisticsProfiles
    {
        public IReadOnlyDictionary<string, string> GetProfileDisplayNamesById() =>
            new Dictionary<string, string> { ["profile-1"] = "Current profile" };
    }

    private sealed class Localization : IDisplayPageLocalization
    {
        public bool BrokenRuleFormat { get; set; }
        public bool BrokenHitFormat { get; set; }

        public string GetString(string key) => key switch
        {
            "Statistics.TotalTraffic.Format" => "{0} / {1}",
            "Statistics.ConnectionCount.Format" => "{0} connections",
            "Statistics.ProfileCount.Format" => "{0} profiles",
            "Statistics.SnapshotCount.Format" => "{0} snapshots",
            "Statistics.NodeCount.Format" => "{0} nodes / {1} health",
            "Statistics.RuleCount.Format" => BrokenRuleFormat ? "{broken}" : "{0} rules",
            "Statistics.Total.Format" => "Total {0}",
            "Statistics.RuleHits.Format" => BrokenHitFormat ? "{broken}" : "{0:N0} hits",
            "Statistics.Loading" => "Loading statistics",
            "Statistics.LoadFailed" => "Could not load statistics",
            "Statistics.RefreshFailed" => "Showing the last loaded statistics",
            "Statistics.Updated.Format" => "Updated {0}",
            _ => key,
        };
    }

    private sealed class CatalogLocalization(AppLanguage language) : IDisplayPageLocalization
    {
        public string GetString(string key) => LocalizationResources.BuildExplicitTranslations()[language][key];
    }

    private sealed class ReadGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StatisticsSummary Read(StatisticsSummary result, bool fail = false)
        {
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Statistics test gate timed out."); }
            if (fail) { throw new IOException("Synthetic statistics read failure."); }
            return result;
        }

        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
