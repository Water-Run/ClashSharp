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

    /// <summary>Verifies non-CJK languages do not contain copied Chinese resource values.</summary>
    [Fact]
    public void Translations_NonCjkLanguages_DoNotContainCjkValues()
    {
        AppLanguage[] languages =
        [
            AppLanguage.English,
            AppLanguage.Russian,
            AppLanguage.French,
            AppLanguage.German,
        ];

        foreach (AppLanguage language in languages)
        {
            string[] cjkValues = LocalizationResources.Translations[language]
                .Where(pair => pair.Value.Any(IsCjk))
                .Select(pair => $"{language}:{pair.Key}={pair.Value}")
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Empty(cjkValues);
        }
    }

    /// <summary>Verifies core navigation keys are present in the English fallback resources.</summary>
    [Theory]
    [InlineData("Nav.MasterControl")]
    [InlineData("Nav.Settings")]
    [InlineData("Nav.About")]
    [InlineData("Page.About.Description")]
    [InlineData("About.GitHub.Title")]
    [InlineData("About.Mihomo.Title")]
    [InlineData("Command.Refresh")]
    [InlineData("Settings.MainlandChinaDisplay.Description")]
    [InlineData("Settings.MainlandChinaFeature.All")]
    [InlineData("Settings.ProxyInformation.CoreConfig.Format")]
    [InlineData("Settings.Section.Startup")]
    [InlineData("Settings.AppAccentColor.Title")]
    [InlineData("Settings.AppAccentColor.FollowSystem")]
    [InlineData("Settings.AppAccentColor.Custom")]
    [InlineData("Settings.AppAccentColor.Pick")]
    [InlineData("Settings.CheckStartupConflicts.Title")]
    [InlineData("Settings.StartupGuide.Title")]
    [InlineData("MihomoService.Status.NotDeployed")]
    [InlineData("MihomoService.Status.DeployedRunning")]
    [InlineData("Tray.Menu.Mode")]
    [InlineData("Tray.Settings")]
    [InlineData("Tray.SafeExit")]
    [InlineData("Links.Dialog.AddTitle")]
    [InlineData("Links.Dialog.Name")]
    [InlineData("Links.Dialog.DefaultName")]
    [InlineData("Links.Dialog.Uri")]
    [InlineData("ProxyNodes.Command.RefreshRuntime")]
    [InlineData("NetworkTakeover.Disabled")]
    [InlineData("NetworkTakeover.Standby")]
    [InlineData("NetworkTakeover.StartupRecovered")]
    [InlineData("NetworkTakeover.SystemProxy.Full")]
    [InlineData("NetworkTakeover.SystemProxy.Rule")]
    [InlineData("NetworkTakeover.TransparentProxy.Full")]
    [InlineData("NetworkTakeover.TransparentProxy.Rule")]
    [InlineData("NetworkTakeover.TransparentProxyServiceMissing.Full")]
    [InlineData("NetworkTakeover.TransparentProxyServiceMissing.Rule")]
    [InlineData("ProxyRecovery.CheckDisabled")]
    [InlineData("ProxyRecovery.NoStaleProxy")]
    [InlineData("ProxyRecovery.Disabled")]
    [InlineData("ProxyRecovery.DoNothing")]
    [InlineData("ProxyRecovery.StartupFailed")]
    [InlineData("RuntimeShutdown.CleanupFailed")]
    [InlineData("StartupLaunch.UpdateFailed")]
    [InlineData("Maintenance.LogClearFailed")]
    [InlineData("Maintenance.FileDeleteFailed")]
    [InlineData("Maintenance.DirectoryDeleteFailed")]
    [InlineData("ConnectionSampling.Failed")]
    [InlineData("ConnectionSampling.Recovered")]
    [InlineData("ConnectionSampling.RecoveredDetail.Format")]
    [InlineData("Diagnostic.Failed.Diagnose")]
    [InlineData("Diagnostic.Failed.Apply")]
    [InlineData("Diagnostic.Failed.Reset")]
    [InlineData("CoreConfiguration.Imported")]
    [InlineData("CoreConfiguration.Validated")]
    [InlineData("ProfileCatalog.Status.Added")]
    [InlineData("ProfileCatalog.Status.Available")]
    [InlineData("ProfileCatalog.Status.Canceled")]
    [InlineData("ProfileCatalog.Subscription.CheckSucceeded.Format")]
    [InlineData("ProfileCatalog.Subscription.CheckFailed")]
    [InlineData("ProfileCatalog.Subscription.Downloading")]
    [InlineData("ProfileCatalog.Subscription.Updated")]
    [InlineData("ProfileCatalog.Subscription.UpdateFailed")]
    [InlineData("ProfileCatalog.Profile.BuiltInDirectAvailable")]
    [InlineData("ProfileCatalog.Profile.ValidationSucceeded")]
    [InlineData("ProfileCatalog.Profile.ValidationFailed")]
    [InlineData("ProfileCatalog.BuiltInDirect.Name")]
    [InlineData("ProfilePreview.CurrentConfiguration")]
    [InlineData("RuleCatalog.BuiltInDirect.Name")]
    [InlineData("Region.CN")]
    [InlineData("Region.HK")]
    [InlineData("Region.MO")]
    [InlineData("Region.TW")]
    [InlineData("Region.JP")]
    [InlineData("Region.KR")]
    [InlineData("Region.SG")]
    [InlineData("Region.US")]
    [InlineData("Region.GB")]
    [InlineData("Region.DE")]
    [InlineData("Region.FR")]
    [InlineData("Region.MainlandChina.HK")]
    [InlineData("Region.MainlandChina.MO")]
    [InlineData("Region.MainlandChina.TW")]
    [InlineData("WindowsDiagnostic.Wsl.Ready")]
    [InlineData("WindowsDiagnostic.Wsl.ProxyEnvironmentMissing")]
    [InlineData("WindowsDiagnostic.Wsl.BridgeMissing")]
    [InlineData("WindowsDiagnostic.Wsl.Unavailable")]
    [InlineData("WindowsDiagnostic.Target.Terminal")]
    [InlineData("WindowsDiagnostic.Terminal.Ready")]
    [InlineData("WindowsDiagnostic.Terminal.ProxyEnvironmentMissing")]
    [InlineData("WindowsDiagnostic.MicrosoftStore.Ready")]
    [InlineData("WindowsDiagnostic.MicrosoftStore.LoopbackMissing")]
    public void Translations_EnglishFallback_ContainsRequiredCoreKeys(string key)
    {
        Assert.True(
            LocalizationResources.Translations[AppLanguage.English].ContainsKey(key),
            $"Missing English fallback key {key}.");
    }

    private static bool IsCjk(char value)
    {
        return value is >= '\u4E00' and <= '\u9FFF';
    }
}
