extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using LocalizationService = ClashSharpUi::ClashSharp.Service.LocalizationService;
using WinUiAppearanceSettings = ClashSharpUi::ClashSharp.Hosting.Settings.WinUiAppearanceSettings;

namespace ClashSharp.Tests.Unit.Services;

public sealed class WinUiAppearanceUnavailableWindowTests
{
    [Fact]
    public void MissingWindow_RejectsEveryActualNativeOperationBeforeAnyUiSettingChanges()
    {
        int rootReads = 0;
        LocalizationService localization = LocalizationService.Instance;
        AppLanguage before = localization.CurrentLanguage;
        IAppearanceNativeSettings native = new WinUiAppearanceSettings(() => { ++rootReads; return null; }, localization);
        Assert.Equal(0, rootReads);
        Assert.Throws<InvalidOperationException>(() => native.CaptureConfiguration());
        Assert.Throws<InvalidOperationException>(() => native.ApplyLanguage(AppLanguage.English));
        Assert.Throws<InvalidOperationException>(() => native.ApplyTheme(AppThemeMode.Dark));
        Assert.Throws<InvalidOperationException>(() => native.ApplyAccent(new(AppAccentColorMode.Custom, "#804477AA")));
        Assert.Equal(before, localization.CurrentLanguage);
        Assert.Equal(4, rootReads);
    }
}
