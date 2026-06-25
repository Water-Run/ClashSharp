/*
 * Region Display Service Tests
 * Verifies localized proxy-node region names and mainland China display overrides
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/RegionDisplayServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;
using System.Reflection;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests localized region metadata resolution.</summary>
public sealed class RegionDisplayServiceTests
{
    /// <summary>Verifies region resolution can be tested without mutating global application settings.</summary>
    [Fact]
    public void Resolve_InjectedDependencies_UsesInjectedDisplayPolicyAndLocalizer()
    {
        ConstructorInfo? constructor = typeof(RegionDisplayService).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(Func<MainlandChinaFeatureMode>),
                typeof(Func<string, string>),
            ],
            modifiers: null);

        Assert.NotNull(constructor);

        RegionDisplayService service = Assert.IsType<RegionDisplayService>(constructor.Invoke(
        [
            () => MainlandChinaFeatureMode.FlagReplacementAndTextCompletion,
            (string key) => key switch
            {
                "Region.TW" => "Taiwan",
                "Region.MainlandChina.TW" => "China Taiwan",
                _ => key,
            },
        ]));

        RegionMetadata metadata = service.Resolve("TW");

        Assert.Equal("China Taiwan", metadata.DisplayName);
        Assert.Equal("CN", metadata.FlagAssetKey);
    }

    /// <summary>Verifies known default region names follow the current UI language.</summary>
    [Fact]
    public void Resolve_KnownRegion_UsesCurrentLanguageDisplayName()
    {
        AppLanguage originalLanguage = LocalizationService.Instance.CurrentLanguage;
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;

        try
        {
            LocalizationService.Instance.CurrentLanguage = AppLanguage.English;
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled;

            RegionMetadata metadata = RegionDisplayService.Instance.Resolve("US");

            Assert.Equal("US", metadata.RegionCode);
            Assert.Equal("United States", metadata.DisplayName);
            Assert.Equal("US", metadata.FlagAssetKey);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = originalLanguage;
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
        }
    }

    /// <summary>Verifies mainland China text completion also uses localized display names.</summary>
    [Fact]
    public void Resolve_MainlandChinaTextCompletion_UsesLocalizedOverrideName()
    {
        AppLanguage originalLanguage = LocalizationService.Instance.CurrentLanguage;
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;

        try
        {
            LocalizationService.Instance.CurrentLanguage = AppLanguage.English;
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementAndTextCompletion;

            RegionMetadata metadata = RegionDisplayService.Instance.Resolve("TW");

            Assert.Equal("TW", metadata.RegionCode);
            Assert.Equal("China Taiwan", metadata.DisplayName);
            Assert.Equal("CN", metadata.FlagAssetKey);
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = originalLanguage;
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
        }
    }

    /// <summary>Verifies CN display text is not mainland-completed until text completion is enabled.</summary>
    [Theory]
    [InlineData(MainlandChinaFeatureMode.Disabled, "China")]
    [InlineData(MainlandChinaFeatureMode.FlagReplacementOnly, "China")]
    [InlineData(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion, "Mainland China")]
    public void Resolve_CnName_UsesMainlandNameOnlyWhenTextCompletionIsEnabled(
        MainlandChinaFeatureMode mode,
        string expectedDisplayName)
    {
        RegionDisplayService service = CreateService(
            () => mode,
            key => key switch
            {
                "Region.CN" => "China",
                "Region.MainlandChina.CN" => "Mainland China",
                _ => key,
            });

        RegionMetadata metadata = service.Resolve("CN");

        Assert.Equal("CN", metadata.RegionCode);
        Assert.Equal(expectedDisplayName, metadata.DisplayName);
        Assert.Equal("CN", metadata.FlagAssetKey);
    }

    private static RegionDisplayService CreateService(
        Func<MainlandChinaFeatureMode> getFeatureMode,
        Func<string, string> getString)
    {
        ConstructorInfo? constructor = typeof(RegionDisplayService).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(Func<MainlandChinaFeatureMode>),
                typeof(Func<string, string>),
            ],
            modifiers: null);

        Assert.NotNull(constructor);
        return Assert.IsType<RegionDisplayService>(constructor.Invoke([getFeatureMode, getString]));
    }
}
