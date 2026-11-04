using System.Globalization;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Exercises the stat-tile, share-bar and recent-traffic chart projections of one statistics snapshot.</summary>
public sealed partial class StatisticsRefreshViewModelTests
{
    [Fact]
    public async Task DailyChart_ShowsChronologicalWindowEndingOnTheRefreshDayWithEmptyDaysFilled()
    {
        DateOnly today = DateOnly.FromDateTime(RefreshTime.ToLocalTime().DateTime);
        StatisticsStore store = WithRows();
        store.DailyRows =
        [
            new(Day(today), 100, 300, 2, RefreshTime),
            new(Day(today.AddDays(-3)), 50, 50, 1, RefreshTime),
            new(Day(today.AddDays(-20)), 9_999, 9_999, 1, RefreshTime),
        ];
        StatisticsViewModel viewModel = Create(store);

        await viewModel.LoadAsync(CancellationToken.None);

        IReadOnlyList<StatisticsDailyBarDisplay> bars = viewModel.DailyChartBars;
        Assert.Equal(StatisticsViewModel.DailyChartDays, bars.Count);
        Assert.Equal(today.AddDays(1 - StatisticsViewModel.DailyChartDays), bars[0].Date);
        Assert.Equal(today, bars[^1].Date);
        Assert.Equal(Enumerable.Range(0, bars.Count).Select(offset => bars[0].Date.AddDays(offset)), bars.Select(bar => bar.Date));
        Assert.Equal(1, bars[^1].Fraction);
        Assert.Equal(0.25, bars[^4].Fraction, 6);
        Assert.All(bars.Where(bar => bar.Date != today && bar.Date != today.AddDays(-3)), bar => Assert.Equal(0, bar.Fraction));
        Assert.Equal(today.Day.ToString(CultureInfo.CurrentCulture), bars[^1].DayLabel);
        Assert.All(bars, bar => Assert.False(string.IsNullOrWhiteSpace(bar.AccessibleText)));
        Assert.True(viewModel.HasDailyChartData);
        Assert.False(viewModel.HasNoDailyChartData);
        Assert.NotEmpty(viewModel.DailyChartRangeText);
        Assert.NotEmpty(viewModel.DailyChartPeakText);
    }

    [Fact]
    public async Task EmptyChartWindow_ClaimsNoDataOnlyAfterALoadedSnapshot()
    {
        StatisticsStore store = WithRows();
        store.DailyRows = [];
        StatisticsViewModel viewModel = Create(store);

        Assert.False(viewModel.HasNoDailyChartData);
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(StatisticsViewModel.DailyChartDays, viewModel.DailyChartBars.Count);
        Assert.All(viewModel.DailyChartBars, bar => Assert.Equal(0, bar.Fraction));
        Assert.False(viewModel.HasDailyChartData);
        Assert.True(viewModel.HasNoDailyChartData);
    }

    [Fact]
    public async Task Shares_AreRelativeToTheLargestRowAndKeepTinyValuesVisible()
    {
        StatisticsStore store = WithRows();
        store.NodeRows =
        [
            new("Node A", 500, 500, 3, RefreshTime),
            new("Node B", 1, 0, 1, RefreshTime),
            new("Node C", 0, 0, 1, RefreshTime),
        ];
        store.ReadRuleHits = () => new Dictionary<string, long> { ["MATCH"] = 40, ["DOMAIN,example.com,DIRECT"] = 10 };
        StatisticsViewModel viewModel = Create(store);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal([1, 0.02, 0], viewModel.NodeTrafficRows.Select(row => row.Share));
        Assert.Equal(100, viewModel.NodeTrafficRows[0].SharePercent);
        Assert.Equal([1, 0.25], viewModel.RuleHitRows.Select(row => row.Share));
        Assert.Equal(1, Assert.Single(viewModel.ProfileTrafficRows).Share);
    }

    [Fact]
    public async Task StatTiles_PublishCountsWithoutUnitLabelsFromTheSameSnapshot()
    {
        StatisticsViewModel viewModel = Create(WithRows());

        Assert.Empty(viewModel.ConnectionCountValueText);
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(7L.ToString("N0", CultureInfo.CurrentCulture), viewModel.ConnectionCountValueText);
        Assert.Equal(10L.ToString("N0", CultureInfo.CurrentCulture), viewModel.NodeCountValueText);
        Assert.Equal(8L.ToString("N0", CultureInfo.CurrentCulture), viewModel.ProfileCountValueText);
        Assert.NotEmpty(viewModel.TotalTransferValueText);
        Assert.NotEmpty(viewModel.NodeHealthText);
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
