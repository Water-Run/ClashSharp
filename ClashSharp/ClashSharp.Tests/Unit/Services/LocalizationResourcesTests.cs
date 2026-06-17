/*
 * Localization Resources Tests
 * Verifies localized string resource coverage across supported application languages
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/LocalizationResourcesTests.cs
 * @date: 2026-06-17
 */

using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.Model;
using ClashSharp.Strings;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests localized string resource coverage and fallback behavior.</summary>
/// <remarks>
/// Invariants: Every supported language must carry the same key set as English.
/// Thread safety: xUnit may run tests concurrently; resource tables are immutable after type initialization.
/// Side effects: None.
/// </remarks>
public sealed class LocalizationResourcesTests
{
    /// <summary>Verifies every <see cref="AppLanguage"/> has a resource dictionary.</summary>
    [Fact]
    public void Translations_AllSupportedLanguages_HaveDictionaries()
    {
        AppLanguage[] languages = Enum.GetValues<AppLanguage>();

        foreach (AppLanguage language in languages)
        {
            Assert.True(
                LocalizationResources.Translations.ContainsKey(language),
                $"Missing localization dictionary for {language}.");
        }
    }

    /// <summary>Verifies non-English resource dictionaries use the same key set as English.</summary>
    [Fact]
    public void Translations_NonEnglishLanguages_MatchEnglishKeySet()
    {
        IReadOnlySet<string> englishKeys = LocalizationResources.Translations[AppLanguage.English].Keys.ToHashSet(StringComparer.Ordinal);

        foreach ((AppLanguage language, IReadOnlyDictionary<string, string> translations) in LocalizationResources.Translations)
        {
            string[] missingKeys = englishKeys.Except(translations.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            string[] extraKeys = translations.Keys.Except(englishKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

            Assert.Empty(missingKeys);
            Assert.Empty(extraKeys);
        }
    }

    /// <summary>Verifies core navigation keys are present in the English fallback resources.</summary>
    [Theory]
    [InlineData("Nav.MasterControl")]
    [InlineData("Nav.Settings")]
    [InlineData("Command.Refresh")]
    [InlineData("Settings.MainlandChinaDisplay.Description")]
    public void Translations_EnglishFallback_ContainsRequiredCoreKeys(string key)
    {
        Assert.True(
            LocalizationResources.Translations[AppLanguage.English].ContainsKey(key),
            $"Missing English fallback key {key}.");
    }
}
