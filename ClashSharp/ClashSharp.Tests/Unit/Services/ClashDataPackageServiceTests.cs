/*
 * Clash Data Package Service Tests
 * Verifies XML import, export, and backup package behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/ClashDataPackageServiceTests.cs
 * @date: 2026-06-25
 */

using System.Xml.Linq;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for Clash# XML data package import and export behavior.</summary>
public sealed class ClashDataPackageServiceTests
{
    /// <summary>Verifies settings-only export writes the XML format and excludes file payloads.</summary>
    [Fact]
    public async Task ExportAsync_SettingsScope_WritesSettingsXmlWithoutFiles()
    {
        using TemporaryDirectory directory = new();
        FakeClashDataPackageSettings settings = new()
        {
            DisplayLanguage = AppLanguage.English,
            AppThemeMode = AppThemeMode.Dark,
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF00AA00",
            MixedPort = 12001,
            ConnectionTestProxyUrl1 = "https://google.com",
        };
        ClashDataPackageService service = new(settings, directory.Path);
        string packagePath = Path.Combine(directory.Path, "settings.clashsharp.xml");

        await service.ExportAsync(packagePath, ClashDataPackageScope.Settings, CancellationToken.None);

        XDocument document = XDocument.Load(packagePath);
        XElement root = AssertRoot(document, ClashDataPackageScope.Settings);
        Assert.Equal("English", SettingValue(root, nameof(IClashDataPackageSettings.DisplayLanguage)));
        Assert.Equal("Dark", SettingValue(root, nameof(IClashDataPackageSettings.AppThemeMode)));
        Assert.Equal("Custom", SettingValue(root, nameof(IClashDataPackageSettings.AppAccentColorMode)));
        Assert.Equal("#FF00AA00", SettingValue(root, nameof(IClashDataPackageSettings.AppAccentColorValue)));
        Assert.Equal("12001", SettingValue(root, nameof(IClashDataPackageSettings.MixedPort)));
        Assert.Empty(root.Element("Files")?.Elements("File") ?? []);
    }

    /// <summary>Verifies proxy-configuration export includes profile catalog and mihomo files while excluding logs.</summary>
    [Fact]
    public async Task ExportAsync_SettingsAndProxyConfigurationScope_IncludesProfileCatalogAndMihomoFilesOnly()
    {
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(Path.Combine(directory.Path, "mihomo", "providers"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "ProfileCatalog.json"), "catalog");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "mihomo", "config.yaml"), "config");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "mihomo", "providers", "proxy.yaml"), "provider");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "logs.sqlite3"), "logs");
        ClashDataPackageService service = new(new FakeClashDataPackageSettings(), directory.Path);
        string packagePath = Path.Combine(directory.Path, "proxy.clashsharp.xml");

        await service.ExportAsync(packagePath, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);

        string[] relativePaths = LoadExportedRelativePaths(packagePath);
        Assert.Contains("ProfileCatalog.json", relativePaths);
        Assert.Contains("mihomo/config.yaml", relativePaths);
        Assert.Contains("mihomo/providers/proxy.yaml", relativePaths);
        Assert.DoesNotContain("logs.sqlite3", relativePaths);
    }

    /// <summary>Verifies import applies settings and restores package files into local data.</summary>
    [Fact]
    public async Task ImportAsync_AppliesSettingsAndRestoresFiles()
    {
        using TemporaryDirectory sourceDirectory = new();
        Directory.CreateDirectory(Path.Combine(sourceDirectory.Path, "mihomo"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory.Path, "ProfileCatalog.json"), "catalog");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory.Path, "mihomo", "config.yaml"), "config");
        FakeClashDataPackageSettings exportedSettings = new()
        {
            DisplayLanguage = AppLanguage.French,
            CurrentMode = ClashSharpMode.RuleTakeover,
            ActiveProfileId = "profile-1",
            MixedPort = 12002,
            ConnectionTestDirectUrl = "https://baidu.com",
        };
        string packagePath = Path.Combine(sourceDirectory.Path, "package.xml");
        await new ClashDataPackageService(exportedSettings, sourceDirectory.Path)
            .ExportAsync(packagePath, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);

        using TemporaryDirectory targetDirectory = new();
        FakeClashDataPackageSettings importedSettings = new();
        ClashDataPackageService importService = new(importedSettings, targetDirectory.Path);

        await importService.ImportAsync(packagePath, CancellationToken.None);

        Assert.Equal(AppLanguage.French, importedSettings.DisplayLanguage);
        Assert.Equal(ClashSharpMode.RuleTakeover, importedSettings.CurrentMode);
        Assert.Equal("profile-1", importedSettings.ActiveProfileId);
        Assert.Equal(12002, importedSettings.MixedPort);
        Assert.Equal("https://baidu.com", importedSettings.ConnectionTestDirectUrl);
        Assert.Equal("catalog", await File.ReadAllTextAsync(Path.Combine(targetDirectory.Path, "ProfileCatalog.json")));
        Assert.Equal("config", await File.ReadAllTextAsync(Path.Combine(targetDirectory.Path, "mihomo", "config.yaml")));
    }

    /// <summary>Verifies import rejects package file entries that try to escape the local data directory.</summary>
    [Fact]
    public async Task ImportAsync_RejectsUnsafeRelativePath()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "unsafe.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.SettingsAndProxyConfiguration.ToString()),
                new XElement("Settings"),
                new XElement("Files",
                    new XElement("File",
                        new XAttribute("Path", "../escape.txt"),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("escape"))))));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        ClashDataPackageService service = new(new FakeClashDataPackageSettings(), directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));
    }

    private static XElement AssertRoot(XDocument document, ClashDataPackageScope scope)
    {
        XElement root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("ClashSharpDataPackage", root.Name.LocalName);
        Assert.Equal("ClashSharp.XmlDataPackage", root.Attribute("Format")?.Value);
        Assert.Equal("1", root.Attribute("Version")?.Value);
        Assert.Equal(scope.ToString(), root.Attribute("Scope")?.Value);
        return root;
    }

    private static string SettingValue(XElement root, string name)
    {
        return root.Element("Settings")?
            .Elements("Setting")
            .Single(element => element.Attribute("Name")?.Value == name)
            .Attribute("Value")?.Value ?? string.Empty;
    }

    private static string[] LoadExportedRelativePaths(string packagePath)
    {
        XDocument document = XDocument.Load(packagePath);
        return document.Root?
            .Element("Files")?
            .Elements("File")
            .Select(element => element.Attribute("Path")?.Value ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private sealed class FakeClashDataPackageSettings : IClashDataPackageSettings
    {
        public AppLanguage DisplayLanguage { get; set; } = AppLanguage.AutoDetect;

        public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.FollowSystem;

        public AppAccentColorMode AppAccentColorMode { get; set; } = AppAccentColorMode.FollowSystem;

        public string AppAccentColorValue { get; set; } = "#FF0078D4";

        public bool LaunchAtStartupEnabled { get; set; }

        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        public string ActiveProfileId { get; set; } = "direct";

        public bool TransparentProxyEnabled { get; set; } = true;

        public int MixedPort { get; set; } = 10000;

        public bool ConnectionSamplingEnabled { get; set; } = true;

        public int ConnectionSamplingIntervalSeconds { get; set; } = 30;

        public bool RestoreProxyOnExit { get; set; } = true;

        public bool CheckStaleProxyOnStartup { get; set; } = true;

        public bool StartupConflictCheckEnabled { get; set; } = true;

        public StartupBehaviorMode StartupBehaviorMode { get; set; } = StartupBehaviorMode.LastSetting;

        public bool ShowStartupGuideOnStartup { get; set; } = true;

        public bool TriggersEnabled { get; set; } = true;

        public bool TriggerNotificationsEnabled { get; set; } = true;

        public CloseBehaviorMode CloseBehaviorMode { get; set; } = CloseBehaviorMode.MinimizeToTray;

        public bool TrayFadeInactiveIcon { get; set; } = true;

        public bool TrayUseMonochromeInactiveIcon { get; set; } = true;

        public string TrayVisibleFeatureIds { get; set; } = "status,mode,pages,transparent-proxy,settings,safe-exit";

        public bool NotificationEnabled { get; set; } = true;

        public NotificationLevel NotificationLevel { get; set; } = NotificationLevel.Default;

        public MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; } = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;

        public bool MainlandChinaUrlBlockingEnabled { get; set; }

        public string ConnectionTestUrl { get; set; } = "https://www.google.com/generate_204";

        public string ConnectionTestProxyUrl1 { get; set; } = "https://www.google.com";

        public string ConnectionTestProxyUrl2 { get; set; } = "https://github.com";

        public string ConnectionTestDirectUrl { get; set; } = "https://www.baidu.com";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ClashSharpDataPackageTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
