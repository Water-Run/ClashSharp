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
        Assert.Contains("PaneToggleRequested=\"AppTitleBar_PaneToggleRequested\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("IsPaneToggleButtonVisible=\"False\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"304,0,138,0\"", mainWindowXaml, StringComparison.Ordinal);
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
        Assert.Contains("x:Name=\"AppAccentColorRow\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AppAccentColorModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AppAccentColorModeOptions}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AppAccentColorPickerButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"AppAccentColorPickerButton_Click\"", settingsXaml, StringComparison.Ordinal);
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
        int transparentProxyIndex = settingsXaml.IndexOf("x:Name=\"TransparentProxySectionTitleText\"", StringComparison.Ordinal);

        Assert.True(startupSectionIndex >= 0, "Startup settings section is missing.");
        Assert.True(launchIndex > startupSectionIndex, "Launch-at-startup row must be under the startup section.");
        Assert.True(manualConflictIndex > launchIndex, "Manual conflict check row must follow launch-at-startup.");
        Assert.True(autoConflictIndex > manualConflictIndex, "Automatic startup conflict setting must follow manual conflict check.");
        Assert.True(guideIndex > autoConflictIndex, "Startup guide setting must follow conflict check settings.");
        Assert.True(behaviorIndex > guideIndex, "Startup behavior mode must stay with startup settings.");
        Assert.True(transparentProxyIndex > behaviorIndex, "Transparent proxy settings must follow the startup section.");
        Assert.Contains("x:Name=\"StartupConflictCheckToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CheckStartupConflictsButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShowStartupGuideToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartupBehaviorModeBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaunchAtStartupToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TunFallbackRow", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FallbackToSystemProxyWhenTunFails", settingsXaml, StringComparison.Ordinal);
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

    /// <summary>Verifies the master control page starts with a centered brand mark instead of a page title block.</summary>
    [Fact]
    public void MasterControlXaml_UsesCenteredLogoWithoutPageIntro()
    {
        string masterControlXamlPath = Path.Combine(AppContext.BaseDirectory, "View", "MasterControl.xaml");

        string masterControlXaml = File.ReadAllText(masterControlXamlPath);

        Assert.Contains("x:Name=\"HeaderLogo\"", masterControlXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", masterControlXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"PageTitleText\"", masterControlXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DescriptionText\"", masterControlXaml, StringComparison.Ordinal);
    }

    /// <summary>Verifies the about page uses a centered, bounded layout with complete app identity fields.</summary>
    [Fact]
    public void AboutXaml_UsesCenteredCompleteIdentityLayout()
    {
        string aboutXamlPath = FindSourceFile("ClashSharp", "ClashSharp", "View", "About.xaml");

        string aboutXaml = File.ReadAllText(aboutXamlPath);

        Assert.Contains("HorizontalAlignment=\"Center\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"720\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VersionLabelText\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RuntimeTitleText\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LicenseTitleText\"", aboutXaml, StringComparison.Ordinal);
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
        Assert.Contains("Padding=\"24,18,18,24\"", xaml, StringComparison.Ordinal);
    }
}
