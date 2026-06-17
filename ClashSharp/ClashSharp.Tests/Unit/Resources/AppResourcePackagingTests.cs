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
        Assert.DoesNotContain("MainlandChinaDisplayToggle", settingsXaml, StringComparison.Ordinal);
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
        Assert.Contains("ItemsSource=\"{Binding ProxyRecoveryModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding StartupBehaviorModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"{Binding ProxyRecoveryIgnoreText}\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies proxy startup controls include conflict checks, startup behavior, and no TUN fallback switch.</summary>
    [Fact]
    public void SettingsXaml_UsesStartupControlsAndRemovesTunFallback()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("x:Name=\"StartupConflictCheckToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartupBehaviorModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchAtStartupToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TunFallbackRow", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FallbackToSystemProxyWhenTunFails", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings page uses the compact RunOnce-style scrolling and row spacing.</summary>
    [Fact]
    public void SettingsXaml_UsesCompactScrollLayout()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.Contains("Padding=\"24,24,24,24\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("<StackPanel Spacing=\"2\" HorizontalAlignment=\"Stretch\">", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Padding=\"32,32,20,32\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StackPanel Spacing=\"18\"", settingsXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies settings action controls use bounded widths rather than unbounded minimum widths.</summary>
    [Fact]
    public void SettingsXaml_BoundsActionControlWidths()
    {
        string settingsXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "Settings.xaml");

        string settingsXaml = File.ReadAllText(settingsXamlPath);

        Assert.DoesNotContain("ConnectionTestUrlBox\" MinWidth=", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MainlandChinaFeatureModeBox\" MinWidth=", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionTestUrlBox\" Width=\"260\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("MainlandChinaFeatureModeBox\" Width=\"280\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConnectionTestButton\"", settingsXaml, StringComparison.Ordinal);
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
