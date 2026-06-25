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

    /// <summary>Verifies Chinese resource dictionaries do not contain accidentally pasted Cyrillic strings.</summary>
    [Theory]
    [InlineData(AppLanguage.SimplifiedChinese)]
    [InlineData(AppLanguage.TraditionalChinese)]
    public void Translations_ChineseLanguages_DoNotContainCyrillicValues(AppLanguage language)
    {
        string[] cyrillicValues = LocalizationResources.Translations[language]
            .Where(pair => pair.Value.Any(IsCyrillic))
            .Select(pair => $"{language}:{pair.Key}={pair.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(cyrillicValues);
    }

    /// <summary>Verifies known previously mixed resource values stay in the matching language.</summary>
    [Theory]
    [InlineData(AppLanguage.SimplifiedChinese, "About.Runtime.Value", "基于.NET 10和WinUI3构建")]
    [InlineData(AppLanguage.SimplifiedChinese, "Settings.ConnectionTest.Succeeded.Format", "连接测试成功，HTTP 状态：{0}。")]
    [InlineData(AppLanguage.TraditionalChinese, "Settings.ConnectionTest.Succeeded.Format", "連線測試成功，HTTP 狀態：{0}。")]
    [InlineData(AppLanguage.TraditionalChinese, "StartupConflict.Mihomo.Title", "另一個 mihomo 核心正在執行")]
    [InlineData(AppLanguage.Russian, "About.ProxyInformation.Title", "Этот прокси")]
    [InlineData(AppLanguage.Russian, "Settings.ConnectionTest.Succeeded.Format", "Проверка подключения успешна, HTTP-статус: {0}.")]
    [InlineData(AppLanguage.Russian, "StartupConflict.Proxy.Repair", "Отключить прокси")]
    [InlineData(AppLanguage.French, "About.ProxyInformation.Title", "Ce proxy")]
    [InlineData(AppLanguage.French, "Settings.ConnectionTest.Succeeded.Format", "Test de connexion réussi, état HTTP : {0}.")]
    [InlineData(AppLanguage.French, "StartupConflict.Proxy.Repair", "Désactiver le proxy")]
    [InlineData(AppLanguage.German, "Settings.ConnectionTest.Succeeded.Format", "Verbindungstest erfolgreich, HTTP-Status: {0}.")]
    public void Translations_PreviouslyMixedValues_AreCorrectLanguage(AppLanguage language, string key, string expected)
    {
        Assert.Equal(expected, LocalizationResources.Translations[language][key]);
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
    [InlineData("Command.Edit")]
    [InlineData("Command.Export")]
    [InlineData("Command.Save")]
    [InlineData("Command.Backup")]
    [InlineData("Command.Register")]
    [InlineData("Command.Detect")]
    [InlineData("Settings.MainlandChinaDisplay.Description")]
    [InlineData("Settings.ConnectionTestUrl.Proxy1")]
    [InlineData("Settings.ConnectionTestUrl.Proxy2")]
    [InlineData("Settings.ConnectionTestUrl.Direct")]
    [InlineData("Settings.MainlandChinaFeature.All")]
    [InlineData("Settings.ProxyInformation.CoreConfig.Format")]
    [InlineData("Settings.Section.Startup")]
    [InlineData("Settings.AppAccentColor.Title")]
    [InlineData("Settings.AppAccentColor.FollowSystem")]
    [InlineData("Settings.AppAccentColor.Custom")]
    [InlineData("Settings.AppAccentColor.Pick")]
    [InlineData("Settings.RestartRequired.Title")]
    [InlineData("Settings.RestartRequired.Message")]
    [InlineData("Settings.ResetGroupToDefaults")]
    [InlineData("Settings.ResetGroupConfirm.Title")]
    [InlineData("Settings.ResetGroupConfirm.Message")]
    [InlineData("Settings.DataPackage.Title")]
    [InlineData("Settings.DataPackage.Description")]
    [InlineData("Settings.DataPackage.Scope.Settings")]
    [InlineData("Settings.DataPackage.Scope.SettingsAndProxyConfiguration")]
    [InlineData("Settings.DataPackage.Scope.All")]
    [InlineData("Settings.DataPackage.Scope.Settings.Description")]
    [InlineData("Settings.DataPackage.Scope.SettingsAndProxyConfiguration.Description")]
    [InlineData("Settings.DataPackage.Scope.All.Description")]
    [InlineData("Settings.DataExport.Title")]
    [InlineData("Settings.DataExport.Description")]
    [InlineData("Settings.DataImport.Warning.Title")]
    [InlineData("Settings.DataImport.Warning.Message")]
    [InlineData("Settings.DataImport.Warning.Scope.Format")]
    [InlineData("Settings.DataImport.SecondConfirm.Title")]
    [InlineData("Settings.DataImport.SecondConfirm.Message")]
    [InlineData("Settings.CheckStartupConflicts.Title")]
    [InlineData("Settings.ConnectionTestUrl.Summary.Default")]
    [InlineData("Settings.ConnectionTestUrl.Provider.Custom")]
    [InlineData("Settings.ConnectionTestUrl.Provider.Google")]
    [InlineData("Settings.ConnectionTestUrl.Provider.GitHub")]
    [InlineData("Settings.ConnectionTestUrl.Provider.Baidu")]
    [InlineData("Settings.ConnectionTestUrl.Provider.Bilibili")]
    [InlineData("Settings.ConnectionTestUrl.Provider.Zhihu")]
    [InlineData("Settings.ConnectionTestUrl.Provider.YouTube")]
    [InlineData("Settings.ConnectionTestUrl.Provider.ChatGPT")]
    [InlineData("Settings.ConnectionTestUrl.Provider.OpenAI")]
    [InlineData("Settings.StartupGuide.Title")]
    [InlineData("Settings.StartupGuide.ShowNow")]
    [InlineData("Settings.StartupRestoreFallback.Title")]
    [InlineData("Settings.StartupRestoreFallback.Status.Registered")]
    [InlineData("Settings.StartupRestoreFallback.Status.NotRegistered")]
    [InlineData("StartupPrompt.Check.Subscription.Title")]
    [InlineData("StartupPrompt.Check.Subscription.Ready")]
    [InlineData("StartupPrompt.Check.Subscription.Missing")]
    [InlineData("StartupPrompt.Check.TransparentProxy.Title")]
    [InlineData("StartupPrompt.Check.TransparentProxy.Missing")]
    [InlineData("StartupPrompt.Check.Fallback.Title")]
    [InlineData("StartupPrompt.Check.StaleProxy.Title")]
    [InlineData("MihomoService.Status.NotDeployed")]
    [InlineData("MihomoService.Status.DeployedRunning")]
    [InlineData("Tray.Menu.Mode")]
    [InlineData("Tray.Menu.Status")]
    [InlineData("Tray.Status.Mode.Format")]
    [InlineData("Tray.Status.Node.Format")]
    [InlineData("Tray.Status.NodeUnavailable")]
    [InlineData("Tray.Status.Latency.Format")]
    [InlineData("Tray.Status.LatencyUnavailable")]
    [InlineData("Master.BasicStatus.Unavailable")]
    [InlineData("Master.BasicStatus.Ready")]
    [InlineData("Master.BasicStatus.Active")]
    [InlineData("Master.Tile.Core")]
    [InlineData("Master.Tile.SystemProxy")]
    [InlineData("Master.Tile.TransparentProxy")]
    [InlineData("Master.Tile.Latency")]
    [InlineData("Master.Tile.StartupLaunch")]
    [InlineData("Master.Tile.ActiveProfile")]
    [InlineData("Master.Tile.MixedPort")]
    [InlineData("Master.Tile.ConnectionTest")]
    [InlineData("Master.Tile.ConnectionTestProxyUrl1")]
    [InlineData("Master.Tile.ConnectionTestProxyUrl2")]
    [InlineData("Master.Tile.ConnectionTestDirectUrl")]
    [InlineData("Master.Tile.StartupPrompt")]
    [InlineData("Master.Tile.StartupConflicts")]
    [InlineData("Master.Tile.Backup")]
    [InlineData("Master.Tile.Visible")]
    [InlineData("Master.Tile.Edit")]
    [InlineData("Master.Tile.EditTiles")]
    [InlineData("Master.Tile.SearchPlaceholder")]
    [InlineData("Master.Tile.Description.Core")]
    [InlineData("Master.Tile.Description.SystemProxy")]
    [InlineData("Master.Tile.Description.TransparentProxy")]
    [InlineData("Master.Tile.Description.Latency")]
    [InlineData("Master.Tile.Description.StartupLaunch")]
    [InlineData("Master.Tile.Description.ActiveProfile")]
    [InlineData("Master.Tile.Description.MixedPort")]
    [InlineData("Master.Tile.Description.ConnectionTest")]
    [InlineData("Master.Tile.Description.ConnectionTestProxyUrl1")]
    [InlineData("Master.Tile.Description.ConnectionTestProxyUrl2")]
    [InlineData("Master.Tile.Description.ConnectionTestDirectUrl")]
    [InlineData("Master.Tile.Description.StartupPrompt")]
    [InlineData("Master.Tile.Description.StartupConflicts")]
    [InlineData("Master.Tile.Description.Backup")]
    [InlineData("Settings.RestartRequiredNotice")]
    [InlineData("Master.Status.CurrentNodeUnavailable")]
    [InlineData("Master.Status.LatencyUnavailable")]
    [InlineData("Master.Status.Latency.Format")]
    [InlineData("Master.LatencyDialog.Title")]
    [InlineData("Master.LatencyDialog.Running")]
    [InlineData("Master.LatencyDialog.Completed.Format")]
    [InlineData("Master.LatencyDialog.Failed")]
    [InlineData("Master.Status.StartupLaunchOn")]
    [InlineData("Master.Status.StartupLaunchOff")]
    [InlineData("Master.Status.BackupAvailable")]
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
    [InlineData("StartupConflict.Dialog.Introduction")]
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
    [InlineData("Region.MainlandChina.CN")]
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

    /// <summary>Verifies the accent color picker command clearly communicates custom color selection.</summary>
    [Fact]
    public void Translations_AccentColorPickText_DescribesCustomColorSelection()
    {
        Assert.Equal("选择自定义颜色", LocalizationResources.Translations[AppLanguage.SimplifiedChinese]["Settings.AppAccentColor.Pick"]);
        Assert.Equal("選擇自訂顏色", LocalizationResources.Translations[AppLanguage.TraditionalChinese]["Settings.AppAccentColor.Pick"]);
        Assert.Equal("Choose custom color", LocalizationResources.Translations[AppLanguage.English]["Settings.AppAccentColor.Pick"]);
    }

    /// <summary>Verifies Simplified Chinese settings copy consistently uses the standard default-value wording.</summary>
    [Fact]
    public void Translations_SimplifiedChinese_DoesNotUseLegacyDefaultWording()
    {
        string[] legacyValues = LocalizationResources.Translations[AppLanguage.SimplifiedChinese]
            .Where(pair => pair.Value.Contains("缺省", StringComparison.Ordinal))
            .Select(pair => $"{pair.Key}={pair.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(legacyValues);
    }

    /// <summary>Verifies China and mainland China labels remain separate so completion level can choose between them.</summary>
    [Fact]
    public void Translations_ChinaAndMainlandChinaLabels_AreSeparate()
    {
        Assert.Equal("中国", LocalizationResources.Translations[AppLanguage.SimplifiedChinese]["Region.CN"]);
        Assert.Equal("中国大陆", LocalizationResources.Translations[AppLanguage.SimplifiedChinese]["Region.MainlandChina.CN"]);
        Assert.Equal("China", LocalizationResources.Translations[AppLanguage.English]["Region.CN"]);
        Assert.Equal("Mainland China", LocalizationResources.Translations[AppLanguage.English]["Region.MainlandChina.CN"]);
    }

    private static bool IsCjk(char value)
    {
        return value is >= '\u4E00' and <= '\u9FFF';
    }

    private static bool IsCyrillic(char value)
    {
        return value is >= '\u0400' and <= '\u04FF'
            or >= '\u0500' and <= '\u052F';
    }
}
