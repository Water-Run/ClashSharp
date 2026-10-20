using System.Globalization;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class DisplayPageViewModelTests
{
    [Fact]
    public async Task RulesLocalizationRefresh_UpdatesTaggedBuiltinSourceWithoutChangingProviderTextOrRows()
    {
        MutablePageLocalization localization = new();
        MutableRuleCatalog catalog = new()
        {
            Read = () => [new("old builtin", "MATCH", "*", "DIRECT", 2) { ProviderResourceKey = "RuleCatalog.BuiltInDirect.Name" },
                new("provider title", "DOMAIN", "example.test", "DIRECT", 3)],
        };
        RulesViewModel page = new(localization, catalog, new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        await page.LoadAsync(CancellationToken.None);
        RulePreviewDisplay row = page.Rules[0];
        RulePreview original = row.Model;
        localization.Language = AppLanguage.Persian;

        page.RefreshLocalization();

        Assert.Same(row, page.Rules[0]);
        Assert.Equal(original, row.Model);
        Assert.Equal(2, row.HitCount);
        Assert.Equal(localization.GetString("RuleCatalog.BuiltInDirect.Name"), row.ProviderNameDisplay);
        Assert.Equal("provider title", page.Rules[1].ProviderNameDisplay);
    }

    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_ReusesAboutStatisticsAndRulesObservations(AppLanguage language)
    {
        MutablePageLocalization localization = new() { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        FakeAboutCore core = new() { VersionText = "v1.19.11" };
        FakeApplicationUpdateChecker updates = new();
        AboutViewModel about = new(localization, core, updates, new FakeUriLauncher(), new TestApplicationErrorSink());
        FakeStatisticsStore store = new();
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        DateTimeOffset now = captured;
        StatisticsViewModel statistics = new(localization, store, new FakeStatisticsProfiles(), () => { },
            new TestApplicationErrorSink(), new ModelDisplayMapper(text => text), () => now);
        FakeRuleCatalog catalog = new();
        RulesViewModel rules = new(localization, catalog, new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        await about.LoadAsync(CancellationToken.None);
        await statistics.LoadAsync(CancellationToken.None);
        await rules.LoadAsync(CancellationToken.None);
        IReadOnlyList<RulePreviewDisplay> originalRules = rules.Rules;
        string shownAboutTitle = about.PageTitleText;
        string shownRulesTitle = rules.PageTitleText;
        about.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(about.PageTitleText)) { shownAboutTitle = about.PageTitleText; } };
        rules.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(rules.PageTitleText)) { shownRulesTitle = rules.PageTitleText; } };
        localization.Language = language;
        now = now.AddDays(1);

        about.RefreshLocalization();
        statistics.RefreshLocalization();
        rules.RefreshLocalization();

        Assert.Equal(localization.GetString("Nav.About"), shownAboutTitle);
        Assert.Equal(localization.GetString("Nav.Rules"), shownRulesTitle);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("About.Mihomo.Available.Format"), "v1.19.11"), about.MihomoStatusText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Statistics.ConnectionCount.Format"), 3), statistics.ConnectionCountText);
        Assert.Contains(new ClashSharp.ApplicationModel.Presentation.DisplayFormatter().Timestamp(captured)!, statistics.StatusText, StringComparison.Ordinal);
        Assert.Same(originalRules, rules.Rules);
        Assert.Equal(1, core.CallCount);
        Assert.Equal(1, updates.CallCount);
        Assert.Equal(1, store.SummaryReadCount);
        Assert.Equal(1, catalog.GetRulesCallCount);
    }
}
