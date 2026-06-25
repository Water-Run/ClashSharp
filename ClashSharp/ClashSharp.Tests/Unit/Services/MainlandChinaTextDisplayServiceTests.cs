/*
 * Mainland China Text Display Service Tests
 * Verifies mainland China feature levels apply only the configured UI display behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MainlandChinaTextDisplayServiceTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.Service;
using System.Reflection;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests mainland China UI-only display replacement behavior.</summary>
public sealed class MainlandChinaTextDisplayServiceTests
{
    /// <summary>Verifies display filtering can be tested without mutating global application settings.</summary>
    [Fact]
    public void Apply_InjectedDependencies_UsesInjectedPolicy()
    {
        ConstructorInfo? constructor = typeof(MainlandChinaTextDisplayService).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(Func<MainlandChinaFeatureMode>),
                typeof(Func<bool>),
            ],
            modifiers: null);

        Assert.NotNull(constructor);

        MainlandChinaTextDisplayService service = Assert.IsType<MainlandChinaTextDisplayService>(constructor.Invoke(
        [
            () => MainlandChinaFeatureMode.FlagReplacementAndTextCompletion,
            () => false,
        ]));

        string displayText = service.Apply("香港");

        Assert.Equal("中国香港", displayText);
    }

    /// <summary>Verifies blacklisted sensitive URLs are masked only in the all-inclusive mode.</summary>
    [Fact]
    public void Apply_AllIncludingUrlBlacklist_MasksSensitiveUrls()
    {
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;
        bool originalUrlBlocking = AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
        try
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementOnly;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = true;

            string displayText = MainlandChinaTextDisplayService.Instance.Apply("https://pincong.rocks/topic/1");

            Assert.Equal("***", displayText);
        }
        finally
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = originalUrlBlocking;
        }
    }

    /// <summary>Verifies keyword filtering does not also hide blacklisted URLs.</summary>
    [Fact]
    public void Apply_KeywordFilterOnly_DoesNotMaskSensitiveUrls()
    {
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;
        bool originalUrlBlocking = AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
        try
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = false;

            string displayText = MainlandChinaTextDisplayService.Instance.Apply("https://pincong.rocks/topic/1");

            Assert.Equal("https://pincong.rocks/topic/1", displayText);
        }
        finally
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = originalUrlBlocking;
        }
    }

    /// <summary>Verifies keyword filtering covers sensitive website names in mainland China mode.</summary>
    [Fact]
    public void Apply_KeywordFilter_MasksSensitiveWebsiteNames()
    {
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;
        bool originalUrlBlocking = AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
        try
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = true;

            string displayText = MainlandChinaTextDisplayService.Instance.Apply("品葱 大纪元");

            Assert.Equal("*** ***", displayText);
        }
        finally
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = originalUrlBlocking;
        }
    }
}
