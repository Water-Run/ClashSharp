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

    /// <summary>Verifies the custom drag region leaves the same caption-button spacing as the reference app.</summary>
    [Fact]
    public void MainWindowXaml_TitleBarReservesCaptionButtonSpace()
    {
        string mainWindowXamlPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");

        string mainWindowXaml = File.ReadAllText(mainWindowXamlPath);

        Assert.Contains("Margin=\"304,0,138,0\"", mainWindowXaml, StringComparison.Ordinal);
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

    /// <summary>Verifies immutable proxy and core paths are available from the settings proxy section.</summary>
    [Fact]
    public void SettingsXaml_ShowsProxyInformationInProxySection()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);
        int proxySectionIndex = settingsXaml.IndexOf("x:Name=\"ProxySectionTitleText\"", StringComparison.Ordinal);
        int proxyInformationIndex = settingsXaml.IndexOf("x:Name=\"ProxyInformationTitleText\"", StringComparison.Ordinal);
        int windowsSectionIndex = settingsXaml.IndexOf("x:Name=\"WindowsNativeSectionTitleText\"", StringComparison.Ordinal);

        Assert.True(proxySectionIndex >= 0, "Proxy settings section is missing.");
        Assert.True(proxyInformationIndex > proxySectionIndex, "Proxy information must be inside the proxy section.");
        Assert.True(proxyInformationIndex < windowsSectionIndex, "Proxy information must appear before the Windows native section.");
        Assert.Contains("x:Name=\"ProxyLocalEntryText\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProxyCoreConfigurationText\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProxyCoreBinaryText\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies mainland China feature settings use a level selector instead of a binary switch.</summary>
    [Fact]
    public void SettingsXaml_UsesMainlandChinaFeatureModeSelector()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("x:Name=\"MainlandChinaFeatureModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainlandChinaDisabledItem\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainlandChinaFlagOnlyItem\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainlandChinaFlagAndTextItem\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainlandChinaKeywordFilterItem\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainlandChinaAllItem\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MainlandChinaDisplayToggle", settingsXaml, StringComparison.Ordinal);
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
    }
}
