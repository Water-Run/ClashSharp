extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;
using AppThemeService = ClashSharpUi::ClashSharp.Service.AppThemeService;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Uses the actual main-assembly entry point in a test process that has no WinUI application.</summary>
public sealed class AppThemeUnavailableResourceTests
{
    [Theory]
    [InlineData(AppAccentColorMode.FollowSystem, "#FF0078D4")]
    [InlineData(AppAccentColorMode.Custom, "#804477AA")]
    public void MissingApplicationResources_CannotClaimTheRequestedAccentIsApplied(AppAccentColorMode mode, string color)
    {
        Assert.Null(ClashSharpUi::ClashSharp.App.MainWindow);
        Exception? failure = Record.Exception(() => AppThemeService.ApplyAccentColor(mode, color));
        if (failure is not null) { Assert.False(ExceptionGraphClassifier.IsProcessFatal(failure)); }
        Assert.True(AppThemeService.IsAccentColorRestartPending(mode, color));
    }
}
