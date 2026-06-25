/*
 * App Resource Packaging Tests
 * Verifies runtime-critical application resources stay in App.xaml
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Resources/AppResourcePackagingTests.cs
 * @date: 2026-06-17
 */

using System;
using System.IO;

namespace ClashSharp.Tests.Unit.Resources;

/// <summary>Tests source placement for resources that must be available when the application XAML loads.</summary>
public sealed class AppResourcePackagingTests
{
    /// <summary>Verifies shared app resources are declared directly in App.xaml instead of an unprocessed loose dictionary.</summary>
    [Fact]
    public void AppXaml_ContainsRuntimeCriticalResources()
    {
        string appXamlPath = Path.Combine(AppContext.BaseDirectory, "App.xaml");

        string appXaml = File.ReadAllText(appXamlPath);

        Assert.Contains("XamlControlsResources", appXaml, StringComparison.Ordinal);
        Assert.Contains("ClashPagePadding", appXaml, StringComparison.Ordinal);
        Assert.Contains("ClashCardGridStyle", appXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the app shell uses the system-theme-aware Mica backdrop.</summary>
    [Fact]
    public void MainWindowXaml_UsesSystemThemeAwareBackdrop()
    {
        string mainWindowXamlPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");

        string mainWindowXaml = File.ReadAllText(mainWindowXamlPath);

        Assert.Contains("<MicaBackdrop />", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopAcrylicBackdrop", mainWindowXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the shell uses the WinUI TitleBar control above NavigationView instead of fixed caption margins.</summary>
    [Fact]
    public void MainWindowXaml_UsesTitleBarAboveNavigationView()
    {
        string mainWindowXamlPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");

        string mainWindowXaml = File.ReadAllText(mainWindowXamlPath);

        Assert.Contains("<TitleBar", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AppTitleBar\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ms-appx:///Assets/Square44x44Logo.scale-200.png", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowAsMonochrome=\"False\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("PaneToggleRequested=\"AppTitleBar_PaneToggleRequested\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("IsPaneToggleButtonVisible=\"False\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavigationView.PaneHeader>", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"304,0,138,0\"", mainWindowXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies custom accent resources are applied before WinUI controls are created on startup.</summary>
    [Fact]
    public void MainWindowCode_AppliesAccentColorBeforeInitializeComponent()
    {
        string mainWindowCodePath = FindSourceFile("ClashSharp", "ClashSharp", "MainWindow.xaml.cs");

        string mainWindowCode = File.ReadAllText(mainWindowCodePath);
        int applyAccentIndex = mainWindowCode.IndexOf("AppThemeService.ApplyAccentColor", StringComparison.Ordinal);
        int initializeIndex = mainWindowCode.IndexOf("InitializeComponent();", StringComparison.Ordinal);

        Assert.True(applyAccentIndex >= 0, "Accent color application is missing.");
        Assert.True(initializeIndex >= 0, "InitializeComponent call is missing.");
        Assert.True(applyAccentIndex < initializeIndex, "Accent color resources must be applied before WinUI controls are created.");
    }

    /// <summary>Verifies logs are only reachable from statistics and not duplicated in the footer navigation.</summary>
    [Fact]
    public void MainWindowXaml_DoesNotExposeLogsAsFooterNavigation()
    {
        string mainWindowXamlPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");

        string mainWindowXaml = File.ReadAllText(mainWindowXamlPath);
        int aboutIndex = mainWindowXaml.IndexOf("x:Name=\"NavAboutItem\"", StringComparison.Ordinal);
        int settingsIndex = mainWindowXaml.IndexOf("x:Name=\"NavSettingsItem\"", StringComparison.Ordinal);

        Assert.DoesNotContain("x:Name=\"NavLogsItem\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Logs\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.True(aboutIndex >= 0, "About footer item is missing.");
        Assert.True(settingsIndex > aboutIndex, "Settings footer item must be after About.");
    }

    /// <summary>Verifies immutable core configuration details are not shown on the master control page.</summary>
    [Fact]
    public void MasterControlXaml_DoesNotShowCoreConfigurationDetails()
    {
        string masterControlXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "MasterControl.xaml");

        string masterControlXaml = File.ReadAllText(masterControlXamlPath);

        Assert.DoesNotContain("CoreConfigurationTitleText", masterControlXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreConfigurationText", masterControlXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies immutable proxy and core paths moved from settings to an about-page dialog entry.</summary>
    [Fact]
    public void ProxyInformation_IsOpenedFromAboutPage()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");
        string aboutXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "About.xaml");
        string aboutCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "About.xaml.cs");
        string settingsXaml = File.ReadAllText(settingsXamlPath);
        string aboutXaml = File.ReadAllText(aboutXamlPath);
        string aboutCode = File.ReadAllText(aboutCodePath);

        Assert.DoesNotContain("x:Name=\"ProxyInformationTitleText\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProxyInformationButton\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("OpenProxyInformationButton_Click", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("SettingsProxyInformationAdapter.CreateSnapshot", aboutCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies about page version and runtime summary live under the product name before the app description.</summary>
    [Fact]
    public void AboutXaml_PlacesVersionAndRuntimeUnderAppName()
    {
        string aboutXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "About.xaml");

        string aboutXaml = File.ReadAllText(aboutXamlPath);

        int appNameIndex = aboutXaml.IndexOf("Text=\"{Binding AppNameText}\"", StringComparison.Ordinal);
        int versionIndex = aboutXaml.IndexOf("Text=\"{Binding VersionSummaryText}\"", StringComparison.Ordinal);
        int runtimeIndex = aboutXaml.IndexOf("Text=\"{Binding RuntimeValueText}\"", StringComparison.Ordinal);
        int descriptionIndex = aboutXaml.IndexOf("Text=\"{Binding AppDescriptionText}\"", StringComparison.Ordinal);

        Assert.True(appNameIndex >= 0, "App name text is missing.");
        Assert.True(versionIndex > appNameIndex, "Version summary must be below the app name.");
        Assert.True(runtimeIndex > versionIndex, "Runtime summary must be below the version.");
        Assert.True(descriptionIndex > runtimeIndex, "App description must be below version and runtime.");
        Assert.Contains("Text=\"{Binding VersionSummaryText}\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AppDescriptionText}\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"VersionLabelText\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"RuntimeTitleText\"", aboutXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the add-subscription dialog uses localization keys instead of hard-coded Chinese labels.</summary>
    [Fact]
    public void LinksCodeBehind_UsesLocalizedAddDialogText()
    {
        string linksCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Links.xaml.cs");

        string linksCode = File.ReadAllText(linksCodePath);

        Assert.DoesNotContain("\"名称\"", linksCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"新订阅\"", linksCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"订阅链接\"", linksCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"添加订阅链接\"", linksCode, StringComparison.Ordinal);
        Assert.Contains("Links.Dialog.Name", linksCode, StringComparison.Ordinal);
        Assert.Contains("Links.Dialog.DefaultName", linksCode, StringComparison.Ordinal);
        Assert.Contains("Links.Dialog.Uri", linksCode, StringComparison.Ordinal);
        Assert.Contains("Links.Dialog.AddTitle", linksCode, StringComparison.Ordinal);
        Assert.Contains("Command.Add", linksCode, StringComparison.Ordinal);
        Assert.Contains("Command.Cancel", linksCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies network takeover result messages are localized instead of hard-coded English text.</summary>
    [Fact]
    public void NetworkTakeoverService_UsesLocalizedResultMessages()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "NetworkTakeoverService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("Full takeover is active through Windows system proxy.", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Rule takeover is active through Windows system proxy.", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Full takeover is active through TUN transparent proxy.", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Rule takeover is active through TUN transparent proxy.", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Mihomo service is not deployed; full takeover is active through Windows system proxy.", serviceCode, StringComparison.Ordinal);
        Assert.Contains("NetworkTakeover.SystemProxy.Full", serviceCode, StringComparison.Ordinal);
        Assert.Contains("NetworkTakeover.TransparentProxy.Rule", serviceCode, StringComparison.Ordinal);
        Assert.Contains("NetworkTakeover.TransparentProxyServiceMissing.Full", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies network takeover mode application is testable through injected dependencies.</summary>
    [Fact]
    public void NetworkTakeoverService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "NetworkTakeoverService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        foreach (string singletonAccess in new[]
        {
            "AppSettingsService.Instance",
            "CoreConfigurationService.Instance",
            "LocalizationService.Instance.GetString",
            "MihomoCoreService.Instance",
            "MihomoServiceManager.Instance",
            "ProxyRecoveryService.Instance",
            "WindowsProxyService.Instance",
        })
        {
            Assert.DoesNotContain(singletonAccess, serviceCode, StringComparison.Ordinal);
        }

        Assert.Contains("INetworkTakeoverSettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("INetworkTakeoverCoreConfiguration", serviceCode, StringComparison.Ordinal);
        Assert.Contains("INetworkTakeoverWindowsProxy", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies provider update actions use the view model command instead of page code-behind.</summary>
    [Fact]
    public void ProxiesXaml_BindsProviderUpdateCommand()
    {
        string proxiesXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Proxies.xaml");
        string proxiesCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Proxies.xaml.cs");

        string proxiesXaml = File.ReadAllText(proxiesXamlPath);
        string proxiesCode = File.ReadAllText(proxiesCodePath);

        Assert.DoesNotContain("Click=\"UpdateProviderButton_Click\"", proxiesXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateProviderButton_Click", proxiesCode, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DataContext.UpdateProviderCommand, ElementName=ProviderResourcesList}\"", proxiesXaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding}\"", proxiesXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies transparent proxy remains a user preference independent from service availability.</summary>
    [Fact]
    public void TransparentProxyPreference_IsNotClearedWhenUnavailable()
    {
        string settingsXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml");
        string takeoverServicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "NetworkTakeoverService.cs");

        string settingsXaml = File.ReadAllText(settingsXamlPath);
        string takeoverServiceCode = File.ReadAllText(takeoverServicePath);

        Assert.DoesNotContain("IsEnabled=\"{Binding CanToggleTransparentProxy}\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsService.Instance.TransparentProxyEnabled = false", takeoverServiceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies core configuration status messages use localization keys.</summary>
    [Fact]
    public void CoreConfigurationService_UsesLocalizedResultMessages()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "CoreConfigurationService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("\"配置已下载、校验并导入。\"", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"配置校验通过。\"", serviceCode, StringComparison.Ordinal);
        Assert.Contains("CoreConfiguration.Imported", serviceCode, StringComparison.Ordinal);
        Assert.Contains("CoreConfiguration.Validated", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies core configuration service receives settings, metrics, validation, and localization through injected boundaries.</summary>
    [Fact]
    public void CoreConfigurationService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "CoreConfigurationService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoProfileParserService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoCoreService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Process", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ICoreConfigurationSettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ICoreConfigurationProfileMetrics", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ICoreConfigurationValidator", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies profile catalog row statuses use localization keys.</summary>
    [Fact]
    public void ProfileCatalogService_UsesLocalizedStatusMessages()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ProfileCatalogService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        foreach (string literal in new[]
        {
            "\"已添加\"",
            "\"检查失败\"",
            "\"正在下载\"",
            "\"已更新\"",
            "\"已取消\"",
            "\"更新失败\"",
            "\"内置直连配置可用。\"",
            "\"可用\"",
            "\"校验通过\"",
            "\"校验失败\"",
            "\"本机直连默认配置\"",
        })
        {
            Assert.DoesNotContain(literal, serviceCode, StringComparison.Ordinal);
        }

        Assert.Contains("ProfileCatalog.Status.Added", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ProfileCatalog.Subscription.CheckSucceeded.Format", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ProfileCatalog.BuiltInDirect.Name", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies profile catalog service receives settings, core configuration, logging, and localization through injected boundaries.</summary>
    [Fact]
    public void ProfileCatalogService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ProfileCatalogService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreConfigurationService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LogStorageService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProfileCatalogSettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProfileCatalogCoreConfiguration", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProfileCatalogLog", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies profile and rule preview fallback names use localization keys.</summary>
    [Fact]
    public void ProfileAndRulePreviewFallbackNames_UseLocalizationKeys()
    {
        string previewParserPath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "MihomoProfilePreviewParser.cs");
        string ruleCatalogPath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "RuleCatalogService.cs");

        string previewParserCode = File.ReadAllText(previewParserPath);
        string ruleCatalogCode = File.ReadAllText(ruleCatalogPath);

        Assert.DoesNotContain("\"当前配置\"", previewParserCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"内置直连\"", ruleCatalogCode, StringComparison.Ordinal);
        Assert.Contains("ProfilePreview.CurrentConfiguration", previewParserCode, StringComparison.Ordinal);
        Assert.Contains("RuleCatalog.BuiltInDirect.Name", ruleCatalogCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies the profile preview parser remains pure and receives localization from callers.</summary>
    [Fact]
    public void MihomoProfilePreviewParser_UsesInjectedLocalization()
    {
        string previewParserPath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "MihomoProfilePreviewParser.cs");

        string previewParserCode = File.ReadAllText(previewParserPath);

        Assert.DoesNotContain("LocalizationService.Instance.GetString", previewParserCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", previewParserCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies profile parser service receives file, display, and localization dependencies from production composition.</summary>
    [Fact]
    public void MihomoProfileParserService_UsesInjectedDisplayDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "MihomoProfileParserService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreConfigurationService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RegionDisplayService.Instance.Resolve", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IMihomoProfileTextSource", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, RegionMetadata>", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies the rule catalog service composes data through injected dependencies.</summary>
    [Fact]
    public void RuleCatalogService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "RuleCatalogService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("MihomoProfileParserService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LogStorageService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IRuleCatalogProfileRules", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IRuleCatalogHitStorage", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies the proxy node catalog service receives active-profile and region dependencies from composition.</summary>
    [Fact]
    public void ProxyNodeCatalogService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ProxyNodeCatalogService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("MihomoProfileParserService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RegionDisplayService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProxyNodeCatalogProfileNodes", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, RegionMetadata>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings language options are sourced from the centralized language catalog.</summary>
    [Fact]
    public void SettingsViewModel_UsesCentralizedSupportedLanguageList()
    {
        string viewModelPath = FindSourceFile("ClashSharp", "ClashSharp", "ViewModel", "SettingsViewModel.cs");

        string viewModelCode = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("\"简体中文\"", viewModelCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"繁體中文\"", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("LocalizationService.GetSupportedLanguages", viewModelCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings view model helpers do not access the global localization singleton directly.</summary>
    [Fact]
    public void SettingsViewModel_DoesNotAccessLocalizationSingleton()
    {
        string viewModelPath = FindSourceFile("ClashSharp", "ClashSharp", "ViewModel", "SettingsViewModel.cs");

        string viewModelCode = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("LocalizationService.Instance.GetString", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", viewModelCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies tray menu state construction receives localization from callers.</summary>
    [Fact]
    public void TrayMenuStateBuilder_UsesInjectedLocalizationOnly()
    {
        string builderPath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "TrayMenuStateBuilder.cs");

        string builderCode = File.ReadAllText(builderPath);

        Assert.DoesNotContain("LocalizationService.Instance.GetString", builderCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", builderCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies custom accent colors update WinUI brush resources used by controls such as buttons.</summary>
    [Fact]
    public void AppThemeService_OverridesAccentBrushResources()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "AppThemeService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        foreach (string resourceKey in new[]
        {
            "AccentFillColorDefaultBrush",
            "AccentFillColorSecondaryBrush",
            "AccentFillColorTertiaryBrush",
            "AccentFillColorDisabledBrush",
            "AccentTextFillColorPrimaryBrush",
            "AccentTextFillColorSecondaryBrush",
            "AccentTextFillColorTertiaryBrush",
            "AccentTextFillColorDisabledBrush",
            "AccentButtonBackground",
            "AccentButtonBorderBrush",
            "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed",
            "AccentButtonForeground",
            "ToggleSwitchFillOn",
            "ToggleSwitchStrokeOn",
            "ToggleButtonBackgroundChecked",
            "SystemControlHighlightAccentBrush",
        })
        {
            Assert.Contains(resourceKey, serviceCode, StringComparison.Ordinal);
        }

        Assert.Contains("new SolidColorBrush", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies region display names are resolved through localization keys.</summary>
    [Fact]
    public void RegionDisplayService_UsesLocalizedRegionNames()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "RegionDisplayService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        foreach (string literal in new[]
        {
            "\"中国大陆\"",
            "\"香港\"",
            "\"澳门\"",
            "\"台湾\"",
            "\"日本\"",
            "\"韩国\"",
            "\"新加坡\"",
            "\"美国\"",
            "\"英国\"",
            "\"德国\"",
            "\"法国\"",
            "\"中国香港\"",
            "\"中国澳门\"",
            "\"中国台湾\"",
        })
        {
            Assert.DoesNotContain(literal, serviceCode, StringComparison.Ordinal);
        }

        Assert.Contains("Region.US", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Region.MainlandChina.TW", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsService.Instance.MainlandChinaFeatureMode", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies mainland China display filtering is not coupled directly to global settings.</summary>
    [Fact]
    public void MainlandChinaTextDisplayService_UsesInjectedSettings()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "MainlandChinaTextDisplayService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<MainlandChinaFeatureMode>", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<bool>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies startup conflict detection resolves user-facing text through an injected localizer.</summary>
    [Fact]
    public void StartupConflictDetectionService_UsesInjectedLocalization()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "StartupConflictDetectionService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsProxyService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetProcessesByName", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TcpListener", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultStartupConflictEnvironment", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IStartupConflictEnvironment", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings code-behind delegates connection testing to the view model.</summary>
    [Fact]
    public void SettingsCodeBehind_DoesNotOwnConnectionTestHttpRequest()
    {
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.DoesNotContain("new HttpClient", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpResponseMessage", settingsCode, StringComparison.Ordinal);
        Assert.Contains("RunConnectionTestAsync", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings code-behind delegates data-maintenance actions to the view model.</summary>
    [Fact]
    public void SettingsCodeBehind_DoesNotOwnDataMaintenanceActions()
    {
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.DoesNotContain("AppDataMaintenanceService.ResetAllSettings()", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AppDataMaintenanceService.ClearAllData()", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsService.Instance.DisplayLanguage", settingsCode, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ResetAllSettings()", settingsCode, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ClearAllData()", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings code-behind delegates startup conflict detection to the view model.</summary>
    [Fact]
    public void SettingsCodeBehind_DoesNotOwnStartupConflictDetection()
    {
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.DoesNotContain("StartupConflictDetectionService.Instance.CheckConflicts(_viewModel.MixedPort)", settingsCode, StringComparison.Ordinal);
        Assert.Contains("_viewModel.CheckStartupConflicts()", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies Windows diagnostic result messages use localization keys.</summary>
    [Fact]
    public void WindowsNetworkDiagnosticService_UsesLocalizedResultMessages()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "WindowsNetworkDiagnosticService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        foreach (string literal in new[]
        {
            "\"WSL 代理桥接已配置。\"",
            "\"WSL 已配置桥接，但用户代理环境变量未指向 Clash#。\"",
            "\"WSL 可用，但未配置代理桥接。\"",
            "\"WSL 不可用或未安装。\"",
            "\"终端代理环境变量已配置。\"",
            "\"终端代理环境变量未指向 Clash#。\"",
            "\"终端\"",
            "\"Microsoft Store 已允许访问本机代理。\"",
            "\"Microsoft Store 未配置本机代理访问豁免。\"",
        })
        {
            Assert.DoesNotContain(literal, serviceCode, StringComparison.Ordinal);
        }

        Assert.Contains("WindowsDiagnostic.Wsl.Ready", serviceCode, StringComparison.Ordinal);
        Assert.Contains("WindowsDiagnostic.Terminal.ProxyEnvironmentMissing", serviceCode, StringComparison.Ordinal);
        Assert.Contains("WindowsDiagnostic.MicrosoftStore.LoopbackMissing", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies Windows diagnostics use injected settings, environment, process, and localization dependencies.</summary>
    [Fact]
    public void WindowsNetworkDiagnosticService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "WindowsNetworkDiagnosticService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.SetEnvironmentVariable", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IWindowsDiagnosticSettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IWindowsDiagnosticEnvironment", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IWindowsDiagnosticProcessRunner", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies the mihomo service manager uses injected command, deployment, and localization dependencies.</summary>
    [Fact]
    public void MihomoServiceManager_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "MihomoServiceManager.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("LocalizationService.Instance.GetString", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreConfigurationService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoCoreService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Process", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IMihomoServiceCommandRunner", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IMihomoServiceDeploymentContext", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies stale proxy recovery result messages use localization keys.</summary>
    [Fact]
    public void ProxyRecoveryService_UsesLocalizedResultMessages()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ProxyRecoveryService.cs");
        string appPath = FindSourceFile("ClashSharp", "ClashSharp", "App.xaml.cs");

        string serviceCode = File.ReadAllText(servicePath);
        string appCode = File.ReadAllText(appPath);

        foreach (string literal in new[]
        {
            "\"Startup stale-proxy check is disabled.\"",
            "\"No stale Clash# proxy state was detected.\"",
            "\"Windows proxy was disabled because stale Clash# proxy state was detected.\"",
            "\"Stale Clash# proxy state was detected, but recovery policy is set to do nothing.\"",
            "\"Startup proxy recovery failed.\"",
        })
        {
            Assert.DoesNotContain(literal, serviceCode, StringComparison.Ordinal);
            Assert.DoesNotContain(literal, appCode, StringComparison.Ordinal);
        }

        Assert.Contains("ProxyRecovery.CheckDisabled", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ProxyRecovery.NoStaleProxy", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ProxyRecovery.Disabled", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ProxyRecovery.DoNothing", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ProxyRecovery.StartupFailed", appCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies stale proxy recovery composes platform dependencies through injected boundaries.</summary>
    [Fact]
    public void ProxyRecoveryService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ProxyRecoveryService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsProxyService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkTakeoverService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProxyRecoverySettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProxyRecoveryWindowsProxy", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProxyRecoveryTakeover", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies runtime shutdown cleanup composes dependencies and localized warning text through injected boundaries.</summary>
    [Fact]
    public void RuntimeShutdownService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "RuntimeShutdownService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("ConnectionSamplingService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoCoreService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsProxyService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LogStorageService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Runtime shutdown cleanup failed.\"", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IRuntimeShutdownSampling", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IRuntimeShutdownCore", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IRuntimeShutdownSettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IRuntimeShutdownWindowsProxy", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IRuntimeShutdownLog", serviceCode, StringComparison.Ordinal);
        Assert.Contains("RuntimeShutdown.CleanupFailed", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies startup launch synchronization composes Windows startup task and logging through injected boundaries.</summary>
    [Fact]
    public void StartupLaunchService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "StartupLaunchService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("Windows.ApplicationModel", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("StartupTask.GetAsync", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LogStorageService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Failed to update launch-at-startup setting.\"", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IStartupLaunchTaskProvider", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IStartupLaunchTask", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IStartupLaunchLog", serviceCode, StringComparison.Ordinal);
        Assert.Contains("StartupLaunch.UpdateFailed", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies proxy latency orchestration receives storage and TCP probing through injected boundaries.</summary>
    [Fact]
    public void ProxyLatencyService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ProxyLatencyService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("LogStorageService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new TcpClient", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch.StartNew", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProxyLatencyStorage", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IProxyLatencyProbe", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies destructive data maintenance composes storage, runtime, and file cleanup through injected boundaries.</summary>
    [Fact]
    public void AppDataMaintenanceService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "AppDataMaintenanceService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeShutdownService.Shutdown", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LogStorageService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileCatalogService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AppDataPathService.ResolveLocalDataDirectory", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Log storage could not be cleared before data deletion.\"", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IAppDataMaintenanceSettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IAppDataMaintenanceRuntime", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IAppDataMaintenanceLogStorage", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IAppDataMaintenanceLocalDataStore", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IAppDataMaintenanceProfileCatalog", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Maintenance.LogClearFailed", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies connection sampling composes settings, mihomo reads, storage, and localized logs through injected boundaries.</summary>
    [Fact]
    public void ConnectionSamplingService_UsesInjectedDependencies()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ConnectionSamplingService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MihomoConnectionService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("LogStorageService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Background connection sampling recovered.\"", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Background connection sampling failed.\"", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IConnectionSamplingSettings", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IConnectionSamplingSource", serviceCode, StringComparison.Ordinal);
        Assert.Contains("IConnectionSamplingStorage", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionSampling.Failed", serviceCode, StringComparison.Ordinal);
        Assert.Contains("ConnectionSampling.Recovered", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string, string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies log storage receives active profile context through an injected function.</summary>
    [Fact]
    public void LogStorageService_UsesInjectedActiveProfile()
    {
        string servicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "LogStorageService.cs");

        string serviceCode = File.ReadAllText(servicePath);

        Assert.DoesNotContain("AppSettingsService.Instance", serviceCode, StringComparison.Ordinal);
        Assert.Contains("Func<string>", serviceCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies mainland China feature settings split display level from URL blocking.</summary>
    [Fact]
    public void SettingsXaml_UsesMainlandChinaFeatureModeSelector()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("x:Name=\"MainlandChinaFeatureModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding MainlandChinaFeatureModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"{Binding MainlandChinaDisabledText}\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"MainlandChinaAllItem\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainlandChinaUrlBlockingToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ShowMainlandChinaUnfriendlyListButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MainlandChinaDisplayToggle", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the mainland China URL blocking row no longer exposes a blocked-URL viewer.</summary>
    [Fact]
    public void SettingsCodeBehind_DoesNotShowMainlandChinaBlockedUrlViewer()
    {
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.DoesNotContain("ShowMainlandChinaUnfriendlyListButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MainlandChinaTextDisplayService.GetUnfriendlyDisplayList()", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies language and recovery combo boxes use bindable option lists instead of empty ComboBoxItem bindings.</summary>
    [Fact]
    public void SettingsXaml_UsesOptionListsForComboBoxes()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("x:Name=\"LanguageBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding DisplayLanguageOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AppThemeModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AppThemeModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AppAccentColorRow\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AppAccentColorModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AppAccentColorModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AppAccentColorSwatchButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"AppAccentColorSwatchButton_Click\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Converter={StaticResource HexColorBrushConverter}", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("BooleanToVisibilityConverter", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsCustomAccentColorSelected, Converter={StaticResource BooleanToVisibilityConverter}}\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"AppAccentColorPickerButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<FontIcon Glyph=\"&#xE790;\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding AppAccentColorPickText}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ProxyRecoveryModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding StartupBehaviorModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"{Binding ProxyRecoveryIgnoreText}\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies theme color settings stay with display settings before startup settings begin.</summary>
    [Fact]
    public void SettingsXaml_PlacesAccentColorWithDisplaySettings()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        int appThemeIndex = settingsXaml.IndexOf("x:Name=\"AppThemeModeRow\"", StringComparison.Ordinal);
        int accentColorIndex = settingsXaml.IndexOf("x:Name=\"AppAccentColorRow\"", StringComparison.Ordinal);
        int startupSectionIndex = settingsXaml.IndexOf("x:Name=\"StartupSectionTitleText\"", StringComparison.Ordinal);

        Assert.True(appThemeIndex >= 0, "Display style row is missing.");
        Assert.True(accentColorIndex > appThemeIndex, "Accent color row must follow display style.");
        Assert.True(startupSectionIndex > accentColorIndex, "Startup section must follow display settings.");
    }

    /// <summary>Verifies accent color picker uses centered dialog content and shows a restart-required prompt after changes.</summary>
    [Fact]
    public void SettingsCodeBehind_CentersAccentColorPickerAndShowsRestartPrompt()
    {
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.Contains("AppAccentColorSwatchButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Center", settingsCode, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment = VerticalAlignment.Center", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ShowRestartRequiredDialogAsync", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer pickerScrollViewer", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeight = 180", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies each settings group exposes a compact right-aligned reset-to-defaults text action.</summary>
    [Fact]
    public void SettingsXaml_UsesCompactGroupResetLinks()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        string[] resetLinkNames =
        [
            "ResetBasicSettingsLink",
            "ResetStartupSettingsLink",
            "ResetProxySettingsLink",
            "ResetWindowsNativeSettingsLink",
            "ResetMainlandChinaSettingsLink",
        ];

        foreach (string resetLinkName in resetLinkNames)
        {
            Assert.Contains($"x:Name=\"{resetLinkName}\"", settingsXaml, StringComparison.Ordinal);
        }

        Assert.Equal(resetLinkNames.Length, CountOccurrences(settingsXaml, "Content=\"{Binding ResetGroupToDefaultsText}\""));
        Assert.Contains("HorizontalAlignment=\"Right\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ResetGroupSettingsRow\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies group reset actions share one confirmation dialog path before applying defaults.</summary>
    [Fact]
    public void SettingsCodeBehind_UsesSharedGroupResetConfirmation()
    {
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.Contains("ResetSettingsGroupAsync", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ResetGroupConfirmTitleText", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ResetGroupConfirmMessageText", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ResetBasicSettingsButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ResetProxySettingsButton_Click", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies proxy startup controls include conflict checks, startup behavior, and no TUN fallback switch.</summary>
    [Fact]
    public void SettingsXaml_UsesStartupControlsAndRemovesTunFallback()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        int startupSectionIndex = settingsXaml.IndexOf("x:Name=\"StartupSectionTitleText\"", StringComparison.Ordinal);
        int launchIndex = settingsXaml.IndexOf("x:Name=\"LaunchAtStartupRow\"", StringComparison.Ordinal);
        int manualConflictIndex = settingsXaml.IndexOf("x:Name=\"CheckStartupConflictsRow\"", StringComparison.Ordinal);
        int autoConflictIndex = settingsXaml.IndexOf("x:Name=\"StartupConflictCheckRow\"", StringComparison.Ordinal);
        int guideIndex = settingsXaml.IndexOf("x:Name=\"ShowStartupGuideRow\"", StringComparison.Ordinal);
        int behaviorIndex = settingsXaml.IndexOf("x:Name=\"StartupBehaviorModeRow\"", StringComparison.Ordinal);
        int proxySectionIndex = settingsXaml.IndexOf("x:Name=\"ProxySectionTitleText\"", StringComparison.Ordinal);
        int transparentProxyIndex = settingsXaml.IndexOf("x:Name=\"TransparentProxyRow\"", StringComparison.Ordinal);
        int startupRestoreIndex = settingsXaml.IndexOf("x:Name=\"StartupRestoreFallbackRow\"", StringComparison.Ordinal);

        Assert.True(startupSectionIndex >= 0, "Startup settings section is missing.");
        Assert.True(launchIndex > startupSectionIndex, "Launch-at-startup row must be under the startup section.");
        Assert.Equal(-1, manualConflictIndex);
        Assert.True(autoConflictIndex > launchIndex, "Startup conflict check row must follow launch-at-startup.");
        Assert.True(guideIndex > autoConflictIndex, "Startup guide setting must follow conflict check settings.");
        Assert.True(behaviorIndex > guideIndex, "Startup behavior mode must stay with startup settings.");
        Assert.True(proxySectionIndex > behaviorIndex, "Proxy settings must follow the startup section.");
        Assert.True(transparentProxyIndex > proxySectionIndex, "Transparent proxy rows must be integrated into the proxy section.");
        Assert.True(startupRestoreIndex > proxySectionIndex, "Startup restore fallback must not remain in the startup section.");
        Assert.DoesNotContain("x:Name=\"TransparentProxySectionTitleText\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartupConflictCheckToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CheckStartupConflictsButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CheckStartupConflictsNowText}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShowStartupGuideToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShowStartupPromptButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartupBehaviorModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchAtStartupToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TunFallbackRow", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FallbackToSystemProxyWhenTunFails", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies startup restore fallback is grouped under Windows-native repair settings.</summary>
    [Fact]
    public void SettingsXaml_PlacesStartupRestoreFallbackUnderWindowsNative()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        int windowsNativeIndex = settingsXaml.IndexOf("x:Name=\"WindowsNativeSectionTitleText\"", StringComparison.Ordinal);
        int restoreFallbackIndex = settingsXaml.IndexOf("x:Name=\"StartupRestoreFallbackRow\"", StringComparison.Ordinal);
        int networkRepairIndex = settingsXaml.IndexOf("x:Name=\"WindowsNativeRepairCard\"", StringComparison.Ordinal);
        int checkStaleProxyIndex = settingsXaml.IndexOf("x:Name=\"CheckStaleProxyRow\"", StringComparison.Ordinal);
        int resetWindowsNativeIndex = settingsXaml.IndexOf("x:Name=\"ResetWindowsNativeSettingsLink\"", StringComparison.Ordinal);

        Assert.True(windowsNativeIndex >= 0, "Windows-native section is missing.");
        Assert.True(restoreFallbackIndex > windowsNativeIndex, "Startup restore fallback must be in the Windows-native section.");
        Assert.True(networkRepairIndex > restoreFallbackIndex, "Network repair card should follow startup restore fallback.");
        Assert.True(checkStaleProxyIndex > networkRepairIndex, "Stale proxy checks should remain in the Windows-native section.");
        Assert.True(resetWindowsNativeIndex > checkStaleProxyIndex, "Windows-native reset link should cover startup restore fallback.");
    }

    /// <summary>Verifies the startup guide has a reusable dialog component reserved for future guide content.</summary>
    [Fact]
    public void StartupGuideDialog_ComponentExists()
    {
        string dialogXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "StartupGuideDialog.xaml");
        string dialogCodePath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "StartupGuideDialog.xaml.cs");

        string dialogXaml = File.ReadAllText(dialogXamlPath);
        string dialogCode = File.ReadAllText(dialogCodePath);

        Assert.Contains("x:Class=\"ClashSharp.Components.StartupGuideDialog\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class StartupGuideDialog : ContentDialog", dialogCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies the bundled mihomo binary is accompanied by redistributable license and source metadata.</summary>
    [Fact]
    public void MihomoBinary_IncludesLicenseAndSourceNotice()
    {
        string binaryDirectory = FindSourceDirectory("ClashSharp", "ClashSharp", "Binaries");
        string licensePath = Path.Combine(binaryDirectory, "mihomo-LICENSE.txt");
        string noticePath = Path.Combine(binaryDirectory, "mihomo-NOTICE.txt");
        string projectPath = FindSourceFile("ClashSharp", "ClashSharp", "ClashSharp.csproj");

        Assert.True(File.Exists(Path.Combine(binaryDirectory, "mihomo.exe")), "Bundled mihomo.exe is missing.");
        Assert.True(File.Exists(licensePath), "Bundled mihomo license file is missing.");
        Assert.True(File.Exists(noticePath), "Bundled mihomo notice file is missing.");

        string licenseText = File.ReadAllText(licensePath);
        string noticeText = File.ReadAllText(noticePath);
        string projectXml = File.ReadAllText(projectPath);

        Assert.Contains("GNU GENERAL PUBLIC LICENSE", licenseText, StringComparison.Ordinal);
        Assert.Contains("MetaCubeX/mihomo", noticeText, StringComparison.Ordinal);
        Assert.Contains("v1.19.27", noticeText, StringComparison.Ordinal);
        Assert.Contains("SHA256", noticeText, StringComparison.Ordinal);
        Assert.Contains("Binaries\\mihomo-LICENSE.txt", projectXml, StringComparison.Ordinal);
        Assert.Contains("Binaries\\mihomo-NOTICE.txt", projectXml, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings page uses the compact RunOnce-style scrolling and row spacing.</summary>
    [Fact]
    public void SettingsXaml_UsesCompactScrollLayout()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("Padding=\"24,18,18,24\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("<StackPanel x:Name=\"SettingsContentPanel\" Spacing=\"6\" HorizontalAlignment=\"Stretch\">", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"PageTitleText\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DescriptionText\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Padding=\"32,32,20,32\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StackPanel Spacing=\"18\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings rows are hosted by a stretching grid so row action controls remain visible.</summary>
    [Fact]
    public void SettingsXaml_UsesStretchingContentHostForRows()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("<StackPanel x:Name=\"SettingsContentPanel\" Spacing=\"6\" HorizontalAlignment=\"Stretch\">", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"{Binding ViewportWidth, ElementName=SettingsScrollViewer}\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0,0,360,0\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies reusable settings rows keep compact right-aligned actions without depending on page clipping.</summary>
    [Fact]
    public void SettingRowXaml_UsesCompactRightAlignedActionColumn()
    {
        string settingRowXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "SettingRow.xaml");

        string settingRowXaml = File.ReadAllText(settingRowXamlPath);

        Assert.Contains("<ColumnDefinition Width=\"*\" />", settingRowXaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", settingRowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ActionContentPresenter\"", settingRowXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", settingRowXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", settingRowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackPanel Spacing=\"10\">", settingRowXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the master control page keeps the brand mark without the old page title block.</summary>
    [Fact]
    public void MasterControlXaml_UsesCenteredLogoWithoutPageIntro()
    {
        string masterControlXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "MasterControl.xaml");

        string masterControlXaml = File.ReadAllText(masterControlXamlPath);

        Assert.Contains("x:Name=\"HeaderLogo\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"ms-appx:///Assets/Square150x150Logo.scale-200.png\"", masterControlXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"PageTitleText\"", masterControlXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DescriptionText\"", masterControlXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies master control page uses extracted reusable mode-button and info-tile components.</summary>
    [Fact]
    public void MasterControlXaml_UsesModeButtonAndTileComponents()
    {
        string masterControlXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "MasterControl.xaml");
        string masterControlCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "MasterControl.xaml.cs");
        string modeButtonXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "MasterModeButton.xaml");
        string infoTileXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "MasterInfoTile.xaml");

        string masterControlXaml = File.ReadAllText(masterControlXamlPath);
        string masterControlCode = File.ReadAllText(masterControlCodePath);
        string modeButtonXaml = File.ReadAllText(modeButtonXamlPath);
        string infoTileXaml = File.ReadAllText(infoTileXamlPath);

        Assert.Contains("xmlns:components=\"using:ClashSharp.Components\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(masterControlXaml, "components:MasterModeButton"));
        Assert.Contains("x:Name=\"InfoTileGrid\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"True\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("AllowDrop=\"True\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenLatencyDialogButton\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("OpenLatencyDialogButton_Click", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditInfoTilesLink\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding EditInfoTilesText}\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"EditInfoTilesButton_Click\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("ProgressBar", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"ClashSharp.Components.MasterModeButton\"", modeButtonXaml, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"ClashSharp.Components.MasterInfoTile\"", infoTileXaml, StringComparison.Ordinal);
        Assert.Contains("<Button", modeButtonXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SelectedOverlay\"", modeButtonXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<RadioButton", modeButtonXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupName=\"MasterControlMode\"", modeButtonXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource ClashCardGridStyle}\"", modeButtonXaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"", modeButtonXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ToggleButton", modeButtonXaml, StringComparison.Ordinal);
        Assert.Contains("Tapped=\"TileRoot_Tapped\"", infoTileXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SelectedOverlay\"", infoTileXaml, StringComparison.Ordinal);
        Assert.Contains("MaxLines=\"2\"", infoTileXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleSwitch", infoTileXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Id == \"edit-tiles\"", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("SearchableOptionList", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("SearchPlaceholder = _viewModel.SearchInfoTilesPlaceholderText", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("MaxListHeight = Math.Max(260, XamlRoot.Size.Height - 260)", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("SearchableOptionItem", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("MasterControlTileAction.ShowStartupPrompt", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("MasterControlTileAction.CheckStartupConflicts", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("MasterControlTileAction.RunLatencyTest", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"InfoTileGrid_SizeChanged\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("ContainerContentChanging=\"InfoTileGrid_ContainerContentChanging\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("MaxInfoTileColumns = 4", masterControlCode, StringComparison.Ordinal);
        Assert.Contains("UpdateInfoTileWidths", masterControlCode, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"Width\" Value=\"250\" />", masterControlXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings page exposes a restart-required notice and keeps reset links away from the edge.</summary>
    [Fact]
    public void SettingsXaml_ShowsRestartNoticeAndIndentsResetLinks()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("x:Name=\"SettingsRestartRequiredNoticeText\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RestartRequiredNoticeText}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding HasRestartRequiredSettings, Converter={StaticResource BooleanToVisibilityConverter}}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource SystemFillColorCriticalBrush}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResetBasicSettingsLink\" Content=\"{Binding ResetGroupToDefaultsText}\" HorizontalAlignment=\"Right\" Margin=\"0,0,8,0\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResetStartupSettingsLink\" Content=\"{Binding ResetGroupToDefaultsText}\" HorizontalAlignment=\"Right\" Margin=\"0,0,8,0\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResetProxySettingsLink\" Content=\"{Binding ResetGroupToDefaultsText}\" HorizontalAlignment=\"Right\" Margin=\"0,0,8,0\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the about page uses a centered, bounded layout with complete app identity fields.</summary>
    [Fact]
    public void AboutXaml_UsesCenteredCompleteIdentityLayout()
    {
        string aboutXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "About.xaml");

        string aboutXaml = File.ReadAllText(aboutXamlPath);

        Assert.Contains("HorizontalAlignment=\"Center\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"720\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding VersionSummaryText}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RuntimeValueText}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding VersionSummaryText}\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AppDescriptionText}\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AppDescriptionText}\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\" Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"VersionLabelText\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"RuntimeTitleText\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LicenseTitleText\"", aboutXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings action controls use bounded widths rather than unbounded minimum widths.</summary>
    [Fact]
    public void SettingsXaml_BoundsActionControlWidths()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.DoesNotContain("ConnectionTestUrlBox", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MainlandChinaFeatureModeBox\" MinWidth=", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditConnectionTestUrlsButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("MainlandChinaFeatureModeBox\" Width=\"280\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConnectionTestButton\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies connection test URLs are edited through a child dialog with restore-defaults support.</summary>
    [Fact]
    public void SettingsXaml_UsesConnectionTestUrlEditorDialog()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsXaml = File.ReadAllText(settingsXamlPath);
        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.Contains("x:Name=\"EditConnectionTestUrlsButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"EditConnectionTestUrlsButton_Click\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ConnectionTestUrlBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("BuildConnectionTestUrlsPanel", settingsCode, StringComparison.Ordinal);
        Assert.Contains("RestoreConnectionTestUrlsButton", settingsCode, StringComparison.Ordinal);
        Assert.Contains("SetConnectionTestUrls", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ResetConnectionTestUrlsToDefaults", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings opens backup/restore export scope selection in a dialog instead of pinning a scope dropdown on the page.</summary>
    [Fact]
    public void SettingsXaml_ExposesDataPackageImportExportBackup()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsXaml = File.ReadAllText(settingsXamlPath);
        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.Contains("x:Name=\"DataPackageRow\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DataPackageScopeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding DataPackageScopeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("BackupRestoreTitleText", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("BackupRestoreDescriptionText", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExportDataPackageButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ImportDataPackageButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"BackupDataPackageButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ExportDataPackageButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.Contains("SelectDataPackageExportScopeAsync", settingsCode, StringComparison.Ordinal);
        Assert.Contains("Settings.DataExport.Title", settingsCode, StringComparison.Ordinal);
        Assert.Contains("DataExportDescriptionText", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ImportDataPackageButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupDataPackageButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.Contains("Settings.DataImport.Warning.Title", settingsCode, StringComparison.Ordinal);
        Assert.Contains("Settings.DataImport.SecondConfirm.Title", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ReadPackageScope", settingsCode, StringComparison.Ordinal);
        Assert.Contains("FormatDataImportWarning", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ClashDataPackageService.Instance", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies the data section is backup/restore oriented and does not duplicate export with a backup button.</summary>
    [Fact]
    public void SettingsXaml_UsesBackupRestoreWithoutBackupButton()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsXaml = File.ReadAllText(settingsXamlPath);
        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.Contains("BackupRestoreTitleText", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("BackupRestoreDescriptionText", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupDataPackageButton", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupDataPackageButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.Contains("DataPackageExportScope.SystemLogs", settingsCode, StringComparison.Ordinal);
        Assert.Contains("DataPackageExportScope.SystemLogSqlite", settingsCode, StringComparison.Ordinal);
        Assert.Contains("ExportLogSqliteAsync", settingsCode, StringComparison.Ordinal);
        Assert.Contains("IsImportableDataPackageScope", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies trigger navigation sits above statistics and resolves to the triggers page.</summary>
    [Fact]
    public void MainWindowXaml_ExposesTriggersAboveStatistics()
    {
        string mainWindowXamlPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");
        string mainWindowCodePath = FindSourceFile("ClashSharp", "ClashSharp", "MainWindow.xaml.cs");
        string mainWindowViewModelPath = FindSourceFile("ClashSharp", "ClashSharp", "ViewModel", "MainWindowViewModel.cs");

        string mainWindowXaml = File.ReadAllText(mainWindowXamlPath);
        string mainWindowCode = File.ReadAllText(mainWindowCodePath);
        string mainWindowViewModel = File.ReadAllText(mainWindowViewModelPath);

        int triggersIndex = mainWindowXaml.IndexOf("x:Name=\"NavTriggersItem\"", StringComparison.Ordinal);
        int statisticsIndex = mainWindowXaml.IndexOf("x:Name=\"NavStatisticsItem\"", StringComparison.Ordinal);
        Assert.True(triggersIndex >= 0, "Triggers navigation item is missing.");
        Assert.True(statisticsIndex > triggersIndex, "Triggers must appear above statistics.");
        Assert.Contains("Content=\"{Binding TriggersText}\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("[\"Triggers\"] = typeof(View.Triggers)", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("public string TriggersText", mainWindowViewModel, StringComparison.Ordinal);
    }

    /// <summary>Verifies triggers have dedicated model/service/view files and searchable add dialogs.</summary>
    [Fact]
    public void TriggerFeature_UsesDedicatedArchitectureAndSearchableOptionList()
    {
        string triggerModelPath = FindSourceFile("ClashSharp", "ClashSharp", "Model", "TriggerTask.cs");
        string triggerServicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "TriggerService.cs");
        string triggerViewModelPath = FindSourceFile("ClashSharp", "ClashSharp", "ViewModel", "TriggersViewModel.cs");
        string triggerViewPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Triggers.xaml");
        string triggerCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Triggers.xaml.cs");
        string searchableComponentPath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "SearchableOptionList.xaml");

        string triggerModel = File.ReadAllText(triggerModelPath);
        string triggerService = File.ReadAllText(triggerServicePath);
        string triggerViewModel = File.ReadAllText(triggerViewModelPath);
        string triggerView = File.ReadAllText(triggerViewPath);
        string triggerCode = File.ReadAllText(triggerCodePath);
        string searchableComponent = File.ReadAllText(searchableComponentPath);

        foreach (string expected in new[]
        {
            "AppEntered",
            "ProxyStarted",
            "NotificationRaised",
            "TotalTraffic",
            "TrafficInWindow",
            "Runtime",
            "SystemTime",
            "CloseConnections",
            "SetTransparentProxy",
            "SwitchProxyMode",
            "ExitApplication",
            "SendNotification",
        })
        {
            Assert.Contains(expected, triggerModel, StringComparison.Ordinal);
        }

        Assert.Contains("MoveTask", triggerService, StringComparison.Ordinal);
        Assert.Contains("Evaluate", triggerService, StringComparison.Ordinal);
        Assert.Contains("TriggerLog", triggerService, StringComparison.Ordinal);
        Assert.Contains("DialogOptionRow", searchableComponent, StringComparison.Ordinal);
        Assert.Contains("SearchBox", searchableComponent, StringComparison.Ordinal);
        Assert.Contains("SearchableOptionList", triggerCode, StringComparison.Ordinal);
        Assert.Contains("MoveUpCommand", triggerViewModel, StringComparison.Ordinal);
        Assert.Contains("MoveDownCommand", triggerViewModel, StringComparison.Ordinal);
        Assert.Contains("TriggersEnabled", triggerView, StringComparison.Ordinal);
    }

    /// <summary>Verifies notification settings and a notification service are present.</summary>
    [Fact]
    public void Notifications_AreConfiguredThroughSettingsAndService()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");
        string settingsViewModelPath = FindSourceFile("ClashSharp", "ClashSharp", "ViewModel", "SettingsViewModel.cs");
        string notificationServicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "NotificationService.cs");
        string appSettingsPath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "AppSettingsService.cs");

        string settingsXaml = File.ReadAllText(settingsXamlPath);
        string settingsViewModel = File.ReadAllText(settingsViewModelPath);
        string notificationService = File.ReadAllText(notificationServicePath);
        string appSettings = File.ReadAllText(appSettingsPath);

        Assert.Contains("NotificationLevelBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("NotificationLevelOptions", settingsViewModel, StringComparison.Ordinal);
        Assert.Contains("NotificationLevel", appSettings, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.AppNotifications", notificationService, StringComparison.Ordinal);
        Assert.Contains("NotifyProxyModeChanged", notificationService, StringComparison.Ordinal);
        Assert.Contains("NotifyTriggerFired", notificationService, StringComparison.Ordinal);
        Assert.Contains("NotifyConnectionTestTimeout", notificationService, StringComparison.Ordinal);
    }

    /// <summary>Verifies master tiles are catalog-driven and expose action tiles beyond passive information.</summary>
    [Fact]
    public void MasterControl_UsesTileCatalogWithSharedActions()
    {
        string masterViewModelPath = FindSourceFile("ClashSharp", "ClashSharp", "ViewModel", "MasterControlViewModel.cs");
        string actionServicePath = FindSourceFile("ClashSharp", "ClashSharp", "Service", "ApplicationActionService.cs");

        string masterViewModel = File.ReadAllText(masterViewModelPath);
        string actionService = File.ReadAllText(actionServicePath);

        Assert.Contains("MasterTileCatalog", masterViewModel, StringComparison.Ordinal);
        Assert.Contains("IApplicationActionDispatcher", masterViewModel, StringComparison.Ordinal);
        foreach (string expectedTile in new[]
        {
            "export-config",
            "import-config",
            "mihomo-version",
            "port",
            "blocked-url",
            "startup-launch",
            "transparent-proxy",
            "connection-sampling",
        })
        {
            Assert.Contains(expectedTile, masterViewModel, StringComparison.Ordinal);
        }

        Assert.Contains("ExportConfiguration", actionService, StringComparison.Ordinal);
        Assert.Contains("ImportConfiguration", actionService, StringComparison.Ordinal);
        Assert.Contains("SetLaunchAtStartup", actionService, StringComparison.Ordinal);
        Assert.Contains("SetTransparentProxy", actionService, StringComparison.Ordinal);
    }

    /// <summary>Verifies dialog option rows are componentized for repeated title/description choice UI.</summary>
    [Fact]
    public void DialogOptionRowComponent_IsUsedBySettingsAndSearchableDialogs()
    {
        string componentXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "DialogOptionRow.xaml");
        string searchableComponentXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "Components", "SearchableOptionList.xaml");
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");
        string masterControlCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "MasterControl.xaml.cs");

        string componentXaml = File.ReadAllText(componentXamlPath);
        string searchableComponentXaml = File.ReadAllText(searchableComponentXamlPath);
        string settingsCode = File.ReadAllText(settingsCodePath);
        string masterControlCode = File.ReadAllText(masterControlCodePath);

        Assert.Contains("x:Class=\"ClashSharp.Components.DialogOptionRow\"", componentXaml, StringComparison.Ordinal);
        Assert.Contains("Title", componentXaml, StringComparison.Ordinal);
        Assert.Contains("Description", componentXaml, StringComparison.Ordinal);
        Assert.Contains("<ToggleButton", componentXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<CheckBox", componentXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DialogOptionHeadingGrid\"", componentXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SelectedGlyph\"", componentXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", componentXaml, StringComparison.Ordinal);
        Assert.Contains("DialogOptionRow", settingsCode, StringComparison.Ordinal);
        Assert.Contains("SelectionInvoked += (_, _) => SelectDataPackageScopeRow", settingsCode, StringComparison.Ordinal);
        Assert.Contains("DialogOptionRow", searchableComponentXaml, StringComparison.Ordinal);
        Assert.Contains("SearchableOptionList", masterControlCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies conflict dialogs use general conflict wording and place actions below status text.</summary>
    [Fact]
    public void StartupConflictDialogPresenter_UsesGeneralConflictCopyAndStackedActions()
    {
        string presenterPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "StartupConflictDialogPresenter.cs");

        string presenterCode = File.ReadAllText(presenterPath);

        Assert.Contains("StartupConflict.Dialog.Introduction", presenterCode, StringComparison.Ordinal);
        Assert.Contains("Orientation = Orientation.Vertical", presenterCode, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Right", presenterCode, StringComparison.Ordinal);
        Assert.Contains("statusText.Text = result.Succeeded", presenterCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies maintenance actions are right-aligned links instead of full-height row buttons.</summary>
    [Fact]
    public void SettingsXaml_UsesRightAlignedMaintenanceLinks()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("x:Name=\"ResetAllSettingsLink\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ClearAllDataLink\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ResetAllSettingsButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ClearAllDataButton\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies network repair dialog content is scroll-constrained and row buttons stay centered.</summary>
    [Fact]
    public void SettingsCodeBehind_ConstrainsNetworkRepairDialog()
    {
        string settingsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Settings.xaml.cs");

        string settingsCode = File.ReadAllText(settingsCodePath);

        Assert.Contains("new ScrollViewer", settingsCode, StringComparison.Ordinal);
        Assert.Contains("MaxHeight =", settingsCode, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", settingsCode, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment = VerticalAlignment.Center", settingsCode, StringComparison.Ordinal);
        Assert.Contains("MaxWidth = 640", settingsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies the logs page exposes an explicit back button.</summary>
    [Fact]
    public void LogsXaml_ContainsBackButton()
    {
        string logsXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Logs.xaml");
        string logsCodePath = FindSourceFile("ClashSharp", "ClashSharp", "View", "Logs.xaml.cs");

        string logsXaml = File.ReadAllText(logsXamlPath);
        string logsCode = File.ReadAllText(logsCodePath);

        Assert.Contains("x:Name=\"BackButton\"", logsXaml, StringComparison.Ordinal);
        Assert.Contains("BackButton_Click", logsXaml, StringComparison.Ordinal);
        Assert.Contains("Frame.CanGoBack", logsCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies startup dialogs wait for the content frame to enter the XAML tree.</summary>
    [Fact]
    public void MainWindowCodeBehind_RunsStartupFlowAfterContentFrameLoaded()
    {
        string mainWindowCodePath = FindSourceFile("ClashSharp", "ClashSharp", "MainWindow.xaml.cs");

        string mainWindowCode = File.ReadAllText(mainWindowCodePath);

        Assert.Contains("ContentFrame.Loaded += OnContentFrameLoaded", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("xamlRoot is null", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Activated += OnWindowActivated", mainWindowCode, StringComparison.Ordinal);
    }

    /// <summary>Verifies startup does not require unused Windows App SDK main/singleton deployment initialization.</summary>
    [Fact]
    public void AppProject_ConfiguresDirectExeWindowsAppSdkStartup()
    {
        string projectPath = FindSourceFile("ClashSharp", "ClashSharp", "ClashSharp.csproj");

        string projectXml = File.ReadAllText(projectPath);

        Assert.Contains("<WindowsAppSdkDeploymentManagerInitialize>false</WindowsAppSdkDeploymentManagerInitialize>", projectXml, StringComparison.Ordinal);
        Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", projectXml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the package manifest declares a packaged desktop startup task.</summary>
    [Fact]
    public void PackageManifest_DeclaresStartupTask()
    {
        string manifestPath = FindSourceFile("ClashSharp", "ClashSharp", "Package.appxmanifest");

        string manifestXml = File.ReadAllText(manifestPath);

        Assert.Contains("xmlns:uap5=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/5\"", manifestXml, StringComparison.Ordinal);
        Assert.Contains("Category=\"windows.startupTask\"", manifestXml, StringComparison.Ordinal);
        Assert.Contains("TaskId=\"ClashSharpStartup\"", manifestXml, StringComparison.Ordinal);
        Assert.Contains("EntryPoint=\"Windows.FullTrustApplication\"", manifestXml, StringComparison.Ordinal);
    }

    /// <summary>Counts non-overlapping occurrences of a string fragment.</summary>
    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }

    /// <summary>Finds a source file by walking upward from the test output directory.</summary>
    /// <param name="segments">Path segments relative to a repository root candidate.</param>
    /// <returns>Existing source file path.</returns>
    private static string FindSourceFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Source file was not found.", Path.Combine(segments));
    }

    /// <summary>Finds a source directory by walking upward from the test output directory.</summary>
    /// <param name="segments">Path segments relative to a repository root candidate.</param>
    /// <returns>Existing source directory path.</returns>
    private static string FindSourceDirectory(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(Path.Combine(segments));
    }

    /// <summary>Verifies long content pages opt into vertical scrolling without horizontal overflow bars.</summary>
    [Theory]
    [InlineData("View", "MasterControl.xaml")]
    [InlineData("View", "Settings.xaml")]
    [InlineData("View", "Statistics.xaml")]
    public void ScrollViewerPages_UseVerticalAutoScrolling(string directory, string fileName)
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, directory, fileName);

        string xaml = File.ReadAllText(xamlPath);

        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
        string expectedPadding = fileName == "MasterControl.xaml" ? "Padding=\"24,18,24,24\"" : "Padding=\"24,18,18,24\"";
        Assert.Contains(expectedPadding, xaml, StringComparison.Ordinal);
    }
}
