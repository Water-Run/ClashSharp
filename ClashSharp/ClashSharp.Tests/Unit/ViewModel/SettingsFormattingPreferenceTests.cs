using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Fact]
    public async Task FormattingChoicesSurvivePageReloadAndParticipateInBasicReset()
    {
        FakeSettingsStore store = new();
        SettingsViewModel page = new(store, _ => { }, () => { });
        page.Load();
        Assert.Equal(2, page.DataUnitStyleIndex);
        Assert.Equal(0, page.RateUnitIndex);
        await page.ApplyPreferenceAsync(SettingsRegistry.Keys.DataUnitStyle, DataUnitStyle.Binary, CancellationToken.None);
        await page.ApplyPreferenceAsync(SettingsRegistry.Keys.RateUnit, RateUnit.Bits, CancellationToken.None);
        await page.ApplyPreferenceAsync(SettingsRegistry.Keys.ClockFormat, ClockFormat.TwelveHour, CancellationToken.None);

        SettingsViewModel reopened = new(store, _ => { }, () => { });
        reopened.Load();
        Assert.Equal(DataUnitStyle.Binary, reopened.DataUnitStyle);
        Assert.Equal(RateUnit.Bits, reopened.RateUnit);
        Assert.Equal(ClockFormat.TwelveHour, reopened.ClockFormat);
        Assert.Equal(3, reopened.DataUnitStyleOptions.Count);
        Assert.Equal(3, reopened.ClockFormatOptions.Count);
        await reopened.ResetPreferenceGroupAsync(SettingsResetScope.Basic, CancellationToken.None);
        Assert.Equal(DataUnitStyle.BinaryWithDecimalLabels, store.DataUnitStyle);
        Assert.Equal(ClockFormat.FollowSystem, store.ClockFormat);
        Assert.Equal(RateUnit.Bytes, store.RateUnit);
        Assert.Equal(store.DataUnitStyle, reopened.DataUnitStyle);
    }

    [Fact]
    public async Task FailedFormattingWriteDoesNotPublishTheRejectedSelection()
    {
        FakeSettingsStore store = new() { BeforePreferenceApply = (_, _) => throw new IOException() };
        SettingsViewModel page = new(store, _ => { }, () => { });
        page.Load();
        await Assert.ThrowsAsync<IOException>(() => page.ApplyPreferenceAsync(SettingsRegistry.Keys.DataUnitStyle, DataUnitStyle.Si, CancellationToken.None));
        Assert.Equal(DataUnitStyle.BinaryWithDecimalLabels, page.DataUnitStyle);
        Assert.True(page.IsPreferenceInputEnabled);
    }
}
