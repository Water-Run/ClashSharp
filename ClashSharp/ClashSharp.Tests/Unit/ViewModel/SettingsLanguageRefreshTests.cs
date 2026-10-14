using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.Strings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Theory]
    [InlineData(AppLanguage.SimplifiedChinese)]
    [InlineData(AppLanguage.TraditionalChinese)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.German)]
    [InlineData(AppLanguage.Persian)]
    [InlineData(AppLanguage.Korean)]
    public async Task LanguageCommit_RefreshesVisibleTextAndOptionsWithoutRebasingOtherSettings(AppLanguage language)
    {
        AppLanguage initial = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English;
        AppLanguage applied = initial;
        FakeSettingsStore store = new() { DisplayLanguage = initial };
        store.AfterPreferenceCommit = () => { applied = store.DisplayLanguage; return Task.CompletedTask; };
        SettingsViewModel page = new(store, _ => { }, () => { }, key => LocalizationResources.Translations[applied].GetValueOrDefault(key, key));
        page.Load();
        await page.SetAppAccentColorModeIndexAsync((int)AppAccentColorMode.Custom);
        await page.ApplyPreferenceAsync(SettingsRegistry.Keys.RateUnit, RateUnit.Bits, CancellationToken.None);
        Assert.True(page.IsAppAccentColorRestartPending);
        string displayedTitle = page.PageTitleText;
        string displayedLanguage = page.LanguageTitleText;
        string[] displayedRates = page.RateUnitOptions.ToArray();
        IReadOnlyList<string> languageOptions = page.DisplayLanguageOptions;
        page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(page.PageTitleText)) { displayedTitle = page.PageTitleText; }
            if (args.PropertyName == nameof(page.LanguageTitleText)) { displayedLanguage = page.LanguageTitleText; }
            if (args.PropertyName == nameof(page.RateUnitOptions)) { displayedRates = page.RateUnitOptions.ToArray(); }
        };

        await page.SetDisplayLanguageIndexAsync((int)language + 1);

        Assert.Equal(LocalizationResources.Translations[language]["Nav.Settings"], displayedTitle);
        Assert.Equal(LocalizationResources.Translations[language]["Settings.Language.Title"], displayedLanguage);
        Assert.Equal<string>([
            LocalizationResources.Translations[language]["Settings.RateUnit.Bytes"],
            LocalizationResources.Translations[language]["Settings.RateUnit.Bits"],
        ], displayedRates);
        Assert.NotEqual(LocalizationResources.Translations[initial]["Settings.Language.Title"], displayedLanguage);
        Assert.Equal(RateUnit.Bits, page.RateUnit);
        Assert.Equal(1, page.RateUnitIndex);
        Assert.Same(languageOptions, page.DisplayLanguageOptions);
        Assert.Equal((int)language + 1, page.DisplayLanguageIndex);
        Assert.True(page.IsAppAccentColorRestartPending);
        Assert.True(page.IsPreferenceInputEnabled);
    }

    [Fact]
    public async Task LanguagePostCommitFailure_RefreshesAppliedTextAndPreservesTheError()
    {
        AppLanguage applied = AppLanguage.English;
        IOException failure = new("notification failed after language application");
        FakeSettingsStore store = new() { DisplayLanguage = applied };
        store.AfterPreferenceCommit = () => { applied = store.DisplayLanguage; return Task.FromException(failure); };
        SettingsViewModel page = new(store, _ => { }, () => { }, key => LocalizationResources.Translations[applied].GetValueOrDefault(key, key));
        page.Load();
        string displayedTitle = page.PageTitleText;
        page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(page.PageTitleText)) { displayedTitle = page.PageTitleText; }
        };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => page.SetDisplayLanguageIndexAsync((int)AppLanguage.German + 1)));

        Assert.Equal(LocalizationResources.Translations[AppLanguage.German]["Nav.Settings"], displayedTitle);
        Assert.Equal(AppLanguage.German, page.DisplayLanguage);
        Assert.NotEmpty(page.OperationErrorText);
        Assert.DoesNotContain(failure.Message, page.OperationErrorText, StringComparison.Ordinal);
        Assert.True(page.IsPreferenceInputEnabled);
    }
}
