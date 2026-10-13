using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class DisplayFormatterTests
{
    [Theory]
    [InlineData(DataUnitStyle.Si, "MB", 1000000)]
    [InlineData(DataUnitStyle.Binary, "MiB", 1048576)]
    [InlineData(DataUnitStyle.BinaryWithDecimalLabels, "MB", 1048576)]
    public void QuantityInputAndDisplayUseTheSameScale(DataUnitStyle style, string label, long bytes)
    {
        DisplayFormatter formatter = new(() => new(style), () => CultureInfo.InvariantCulture);
        Assert.Equal(label, formatter.MegaUnitLabel);
        Assert.Equal(bytes, formatter.FromMegaUnits(1));
        Assert.Equal("1 " + label, formatter.Bytes(bytes));
    }

    [Fact]
    public void TwelveHourTimeRetainsEveryFractionalTick()
    {
        CultureInfo regional = CultureInfo.GetCultureInfo("en-US");
        DisplayFormatter formatter = new(() => new(ClockFormat: ClockFormat.TwelveHour), () => regional);
        TimeOnly original = new TimeOnly(23, 15, 42).Add(TimeSpan.FromTicks(1234567));
        Assert.Equal(original, TimeOnly.Parse(formatter.Time(original), regional));
    }

    [Theory]
    [InlineData(DataUnitStyle.Si, 1000, "1 KB")]
    [InlineData(DataUnitStyle.Si, 1024, "1.02 KB")]
    [InlineData(DataUnitStyle.Binary, 1024, "1 KiB")]
    [InlineData(DataUnitStyle.BinaryWithDecimalLabels, 1024, "1 KB")]
    [InlineData(DataUnitStyle.BinaryWithDecimalLabels, 1000, "1000 B")]
    [InlineData(DataUnitStyle.Binary, 1048576, "1 MiB")]
    [InlineData(DataUnitStyle.Si, 1000000, "1 MB")]
    [InlineData(DataUnitStyle.Binary, -1, "0 B")]
    public void QuantityHonorsBothScaleAndLabel(DataUnitStyle style, long bytes, string expected)
    {
        DisplayFormatter formatter = new(() => new(style), () => CultureInfo.InvariantCulture);
        Assert.Equal(expected, formatter.Bytes(bytes));
    }

    [Theory]
    [InlineData(DataUnitStyle.Si, RateUnit.Bits, 125, "1 kb/s")]
    [InlineData(DataUnitStyle.Binary, RateUnit.Bits, 128, "1 Kib/s")]
    [InlineData(DataUnitStyle.BinaryWithDecimalLabels, RateUnit.Bytes, 1024, "1 KB/s")]
    public void RateConvertsBytesOnlyWhenBitsAreSelected(DataUnitStyle style, RateUnit rate, long bytes, string expected)
    {
        DisplayFormatter formatter = new(() => new(style, rate), () => CultureInfo.InvariantCulture);
        Assert.Equal(expected, formatter.Rate(bytes));
    }

    [Fact]
    public void ReadsChangedPreferencesAndKeepsRegionalNumberSeparators()
    {
        DisplayFormattingOptions options = new();
        DisplayFormatter formatter = new(() => options, () => CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("1,5 KB", formatter.Bytes(1536));
        options = options with { DataUnitStyle = DataUnitStyle.Binary };
        Assert.Equal("1,5 KiB", formatter.Bytes(1536));
    }

    [Theory]
    [InlineData(ClockFormat.FollowSystem, "04:05:06 PM")]
    [InlineData(ClockFormat.TwelveHour, "4:05:06 PM")]
    [InlineData(ClockFormat.TwentyFourHour, "16:05:06")]
    public void ClockUsesUserOverridesOrTheExplicitChoice(ClockFormat clock, string expected)
    {
        CultureInfo regional = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        regional.DateTimeFormat.LongTimePattern = "hh:mm:ss tt";
        DisplayFormatter formatter = new(() => new(ClockFormat: clock), () => regional);
        Assert.Equal(expected, formatter.Time(new TimeOnly(16, 5, 6)));
        Assert.Equal("hh:mm:ss tt", regional.DateTimeFormat.LongTimePattern);
    }

    [Fact]
    public void LargeRateCannotOverflowAndAbsentTimeStaysAbsent()
    {
        DisplayFormatter formatter = new(() => new(RateUnit: RateUnit.Bits), () => CultureInfo.InvariantCulture);
        Assert.Equal("64 Eb/s", formatter.Rate(long.MaxValue));
        Assert.Null(formatter.Timestamp(null));
    }
}
