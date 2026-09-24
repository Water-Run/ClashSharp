using System.Globalization;
using System.Linq;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests language catalog membership, auto-detect mapping, and RTL classification.</summary>
public sealed class LocalizationServiceTests
{
    /// <summary>Verifies the settings picker exposes Russian, Persian, and Korean with native names.</summary>
    [Fact]
    public void GetSupportedLanguages_IncludesRussianPersianAndKoreanNativeNames()
    {
        (AppLanguage Language, string DisplayName)[] languages =
            LocalizationService.GetSupportedLanguages().ToArray();

        Assert.Contains(languages, item => item.Language == AppLanguage.Russian && item.DisplayName == "Русский");
        Assert.Contains(languages, item => item.Language == AppLanguage.Persian && item.DisplayName == "فارسی");
        Assert.Contains(languages, item => item.Language == AppLanguage.Korean && item.DisplayName == "한국어");
        Assert.Equal(9, languages.Length);
    }

    /// <summary>Verifies explicit language values resolve to themselves.</summary>
    [Theory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Persian)]
    [InlineData(AppLanguage.Korean)]
    [InlineData(AppLanguage.English)]
    public void ResolveEffectiveLanguage_ExplicitValue_ReturnsSameLanguage(AppLanguage language)
    {
        Assert.Equal(language, LocalizationService.ResolveEffectiveLanguage(language, CultureInfo.InvariantCulture));
    }

    /// <summary>Verifies auto-detect maps Windows UI cultures onto authored catalogs.</summary>
    [Theory]
    [InlineData("ru-RU", AppLanguage.Russian)]
    [InlineData("fa-IR", AppLanguage.Persian)]
    [InlineData("fa", AppLanguage.Persian)]
    [InlineData("ko-KR", AppLanguage.Korean)]
    [InlineData("ko", AppLanguage.Korean)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("de-DE", AppLanguage.German)]
    [InlineData("zh-CN", AppLanguage.SimplifiedChinese)]
    [InlineData("zh-TW", AppLanguage.TraditionalChinese)]
    public void ResolveEffectiveLanguage_AutoDetect_MapsKnownCultures(string cultureName, AppLanguage expected)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);

        Assert.Equal(expected, LocalizationService.ResolveEffectiveLanguage(AppLanguage.AutoDetect, culture));
    }

    /// <summary>Verifies only Persian is classified as a right-to-left interface language.</summary>
    [Theory]
    [InlineData(AppLanguage.Persian, true)]
    [InlineData(AppLanguage.Russian, false)]
    [InlineData(AppLanguage.Korean, false)]
    [InlineData(AppLanguage.English, false)]
    [InlineData(AppLanguage.SimplifiedChinese, false)]
    public void IsRightToLeft_ClassifiesPersianOnly(AppLanguage language, bool expected)
    {
        Assert.Equal(expected, LocalizationService.IsRightToLeft(language, CultureInfo.InvariantCulture));
    }

    /// <summary>Verifies auto-detect Persian cultures also select a right-to-left layout.</summary>
    [Fact]
    public void IsRightToLeft_AutoDetectPersianCulture_IsRightToLeft()
    {
        Assert.True(
            LocalizationService.IsRightToLeft(
                AppLanguage.AutoDetect,
                CultureInfo.GetCultureInfo("fa-IR")));
    }
}
