using ClashSharp.Model;
using ClashSharp.Strings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

internal sealed class MutablePageLocalization : IMasterControlLocalization, IDisplayPageLocalization,
    IConnectionsLocalization, IProxiesLocalization
{
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public string GetString(string key) => LocalizationResources.Translations[Language].GetValueOrDefault(key, key);
}

public static class PageLocalizationTestData
{
    public static TheoryData<AppLanguage> Languages => new()
    {
        AppLanguage.SimplifiedChinese, AppLanguage.TraditionalChinese, AppLanguage.English,
        AppLanguage.German, AppLanguage.French, AppLanguage.Russian, AppLanguage.Persian, AppLanguage.Korean,
    };
}
