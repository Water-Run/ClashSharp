using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

public sealed class RegionalDefaultsPolicyTests
{
    [Theory]
    [InlineData("en-US", "CN", "UTC", true)]
    [InlineData("zh-CN", "US", "China Standard Time", false)]
    [InlineData("zh-CN", "SG", "China Standard Time", false)]
    [InlineData("zh-TW", "TW", "China Standard Time", false)]
    [InlineData("zh-HK", "HK", "China Standard Time", false)]
    [InlineData("zh-MO", "MO", "China Standard Time", false)]
    [InlineData("zh-CN", "", "Asia/Shanghai", true)]
    [InlineData("zh-CN", "", "UTC", false)]
    [InlineData("en-US", "", "China Standard Time", false)]
    public void ExplicitRegionWinsOverLanguageAndTimeZone(string language, string region, string zone, bool mainland)
    {
        IReadOnlyDictionary<string, object> values = RegionalDefaultsPolicy.Create(language, region, zone, "24HourClock");
        Assert.Equal((int)(mainland ? MainlandChinaFeatureMode.FlagReplacementAndTextCompletion : MainlandChinaFeatureMode.Disabled),
            values["MainlandChinaFeatureMode"]);
        Assert.Equal(false, values["MainlandChinaUrlBlockingEnabled"]);
        Assert.Equal((int)DataUnitStyle.BinaryWithDecimalLabels, values["DataUnitStyle"]);
        Assert.Equal((int)RateUnit.Bytes, values["RateUnit"]);
        Assert.Equal((int)AppLanguage.AutoDetect, values["DisplayLanguage"]);
    }

    [Theory]
    [InlineData("12HourClock", ClockFormat.TwelveHour)]
    [InlineData("24HourClock", ClockFormat.TwentyFourHour)]
    [InlineData("", ClockFormat.FollowSystem)]
    public void ClockDoesNotAssumeTheLanguageDefault(string identifier, ClockFormat expected)
    {
        Assert.Equal((int)expected, RegionalDefaultsPolicy.Create("en-US", "US", "UTC", identifier)["ClockFormat"]);
    }
}
