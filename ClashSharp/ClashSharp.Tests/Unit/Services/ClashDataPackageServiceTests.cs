using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for Clash# XML data package import and export behavior.</summary>
public sealed class ClashDataPackageServiceTests
{
    private const string DefaultMasterHeroStatusLayout =
        "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability";

    /// <summary>Guards package/reset generation coverage against additions to the canonical registry.</summary>
    [Fact]
    public void SettingsContract_CoversEveryRegistryPackageSetting()
    {
        string[] expected = SettingsRegistry.Default.Definitions
            .Where(static definition => definition.IncludeInDataPackage)
            .Select(static definition => definition.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = typeof(IClashDataPackageSettings)
            .GetProperties()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

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
            MasterInfoTileLayout = "core,latency",
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
        Assert.Equal("core,latency", SettingValue(root, nameof(IClashDataPackageSettings.MasterInfoTileLayout)));
        Assert.Empty(root.Element("Files")?.Elements("File") ?? []);
    }

    /// <summary>Verifies an unsafe persisted information-tile layout cannot be emitted into a package.</summary>
    [Fact]
    public async Task ExportAsync_WhenMasterInfoTileLayoutIsUnsafe_RejectsExport()
    {
        using TemporaryDirectory directory = new();
        FakeClashDataPackageSettings settings = new()
        {
            MasterInfoTileLayout = "core,../unknown",
        };
        ClashDataPackageService service = new(settings, directory.Path);
        string packagePath = Path.Combine(directory.Path, "unsafe-settings.clashsharp.xml");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ExportAsync(packagePath, ClashDataPackageScope.Settings, CancellationToken.None));

        Assert.False(File.Exists(packagePath));
    }

    /// <summary>Verifies proxy-configuration export excludes generated runtime configuration and logs.</summary>
    [Fact]
    public async Task ExportAsync_SettingsAndProxyConfigurationScope_IncludesProfileCatalogAndMihomoFilesOnly()
    {
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(Path.Combine(directory.Path, "mihomo", "providers"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "mihomo", "profiles", "profile-1"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "mihomo", "runtime-generations"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "ProfileCatalog.json"), "catalog");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "mihomo", "config.yaml"), "secret: private-runtime-secret");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "mihomo", "config.runtime-state.json"),
            "{\"appliedContentHash\":\"private\"}");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "mihomo", "runtime-generations", "0000000000000000001-private.yaml"),
            "secret: private-runtime-secret");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "mihomo", "config.yaml.runtime-staging.private"),
            "secret: private-runtime-secret");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "mihomo", "profiles", "profile-1", "config.yaml.runtime-backup.private"),
            "secret: private-source-secret");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "mihomo", "profiles", "profile-1", "config.yaml.staging.private"),
            "secret: private-source-secret");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "mihomo", "providers", "proxy.yaml"), "provider");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "logs.sqlite3"), "logs");
        ClashDataPackageService service = new(new FakeClashDataPackageSettings(), directory.Path);
        string packagePath = Path.Combine(directory.Path, "proxy.clashsharp.xml");

        await service.ExportAsync(packagePath, ClashDataPackageScope.SettingsAndProxyConfiguration, CancellationToken.None);

        string[] relativePaths = LoadExportedRelativePaths(packagePath);
        Assert.Contains("ProfileCatalog.json", relativePaths);
        Assert.DoesNotContain("mihomo/config.yaml", relativePaths);
        Assert.DoesNotContain("mihomo/config.runtime-state.json", relativePaths);
        Assert.DoesNotContain("mihomo/runtime-generations/0000000000000000001-private.yaml", relativePaths);
        Assert.DoesNotContain("mihomo/config.yaml.runtime-staging.private", relativePaths);
        Assert.DoesNotContain(
            "mihomo/profiles/profile-1/config.yaml.runtime-backup.private",
            relativePaths);
        Assert.DoesNotContain("mihomo/profiles/profile-1/config.yaml.staging.private", relativePaths);
        Assert.Contains("mihomo/providers/proxy.yaml", relativePaths);
        Assert.DoesNotContain("logs.sqlite3", relativePaths);
    }

    /// <summary>Verifies import applies settings and restores package files into local data.</summary>
    [Fact]
    public async Task ImportAsync_AppliesSettingsAndRestoresFiles()
    {
        using TemporaryDirectory sourceDirectory = new();
        Directory.CreateDirectory(Path.Combine(sourceDirectory.Path, "mihomo", "profiles", "profile-1"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory.Path, "ProfileCatalog.json"), "catalog");
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory.Path, "mihomo", "profiles", "profile-1", "config.yaml"),
            "config");
        FakeClashDataPackageSettings exportedSettings = new()
        {
            DisplayLanguage = AppLanguage.French,
            CurrentMode = ClashSharpMode.RuleTakeover,
            ActiveProfileId = "profile-1",
            MixedPort = 12002,
            ConnectionTestDirectUrl = "https://baidu.com",
            MasterInfoTileLayout = "latency,core",
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
        Assert.Equal("latency,core", importedSettings.MasterInfoTileLayout);
        Assert.Equal("catalog", await File.ReadAllTextAsync(Path.Combine(targetDirectory.Path, "ProfileCatalog.json")));
        Assert.Equal(
            "config",
            await File.ReadAllTextAsync(
                Path.Combine(targetDirectory.Path, "mihomo", "profiles", "profile-1", "config.yaml")));
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

    /// <summary>Verifies legacy packages preserve but cannot replace the generated authenticated runtime configuration.</summary>
    [Fact]
    public async Task ImportAsync_WhenPackageContainsGeneratedRuntimeConfig_IgnoresGeneratedFile()
    {
        using TemporaryDirectory directory = new();
        string generatedConfigPath = Path.Combine(directory.Path, "mihomo", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(generatedConfigPath)!);
        await File.WriteAllTextAsync(generatedConfigPath, "secret: current-private-secret");
        string packagePath = Path.Combine(directory.Path, "legacy-package.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.SettingsAndProxyConfiguration.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.DisplayLanguage)),
                        new XAttribute("Value", AppLanguage.French.ToString()))),
                new XElement("Files",
                    new XElement("File",
                        new XAttribute("Path", "mihomo/config.yaml"),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("external-controller: 0.0.0.0:9090"))),
                    new XElement("File",
                        new XAttribute("Path", "mihomo/config.runtime-state.json"),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("private-state"))),
                    new XElement("File",
                        new XAttribute("Path", "mihomo/runtime-generations/0000000000000000001-private.yaml"),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("secret: private-snapshot"))),
                    new XElement("File",
                        new XAttribute("Path", "mihomo/profiles/profile-1/config.yaml.runtime-backup.private"),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("secret: private-sidecar"))))));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        ClashDataPackageService service = new(settings, directory.Path);

        await service.ImportAsync(packagePath, CancellationToken.None);

        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        Assert.Equal("secret: current-private-secret", await File.ReadAllTextAsync(generatedConfigPath));
        Assert.False(File.Exists(Path.Combine(directory.Path, "mihomo", "config.runtime-state.json")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "mihomo", "runtime-generations")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "mihomo", "profiles")));
    }

    /// <summary>Verifies import validates the whole package before applying settings or writing files.</summary>
    [Fact]
    public async Task ImportAsync_WhenFilePayloadIsInvalid_DoesNotApplyPartialSettingsOrFiles()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "invalid-payload.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.SettingsAndProxyConfiguration.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.DisplayLanguage)),
                        new XAttribute("Value", AppLanguage.French.ToString())),
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.MixedPort)),
                        new XAttribute("Value", "12002"))),
                new XElement("Files",
                    new XElement("File",
                        new XAttribute("Path", "mihomo/profiles/imported/config.yaml"),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("config"))),
                    new XElement("File",
                        new XAttribute("Path", "mihomo/bad.yaml"),
                        "not-base64"))));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        FakeClashDataPackageSettings settings = new();
        ClashDataPackageService service = new(settings, directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(AppLanguage.AutoDetect, settings.DisplayLanguage);
        Assert.Equal(10000, settings.MixedPort);
        Assert.False(File.Exists(Path.Combine(directory.Path, "mihomo", "profiles", "imported", "config.yaml")));
    }

    [Fact]
    public async Task ImportAsync_WhenFileTargetsAreDuplicated_RejectsBeforeMutation()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "duplicate.xml");
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("config"));
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.SettingsAndProxyConfiguration.ToString()),
                new XElement("Settings"),
                new XElement("Files",
                    new XElement("File", new XAttribute("Path", "mihomo/profiles/a/config.yaml"), encoded),
                    new XElement("File", new XAttribute("Path", "mihomo/profiles/a/config.yaml"), encoded))));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        ClashDataPackageService service = new(new FakeClashDataPackageSettings(), directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(directory.Path, "mihomo", "profiles", "a", "config.yaml")));
    }

    /// <summary>Verifies invalid setting ranges are rejected before any package files are written.</summary>
    [Fact]
    public async Task ImportAsync_WhenSettingRangeIsInvalid_DoesNotWriteFiles()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "invalid-setting-range.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.SettingsAndProxyConfiguration.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.MixedPort)),
                        new XAttribute("Value", "70000"))),
                new XElement("Files",
                    new XElement("File",
                        new XAttribute("Path", "mihomo/profiles/imported/config.yaml"),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("config"))))));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        FakeClashDataPackageSettings settings = new();
        ClashDataPackageService service = new(settings, directory.Path);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(10000, settings.MixedPort);
        Assert.False(File.Exists(Path.Combine(directory.Path, "mihomo", "profiles", "imported", "config.yaml")));
    }

    /// <summary>Verifies unsafe information-tile ids are rejected before any setting is changed.</summary>
    [Fact]
    public async Task ImportAsync_WhenMasterInfoTileLayoutIsUnsafe_RejectsPackageWithoutChangingSettings()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "unsafe-info-tile-layout.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.Settings.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.DisplayLanguage)),
                        new XAttribute("Value", AppLanguage.French.ToString())),
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.MasterInfoTileLayout)),
                        new XAttribute("Value", "core,../unknown"))),
                new XElement("Files")));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        FakeClashDataPackageSettings settings = new()
        {
            DisplayLanguage = AppLanguage.English,
            MasterInfoTileLayout = "latency,core",
        };
        ClashDataPackageService service = new(settings, directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
        Assert.Equal("latency,core", settings.MasterInfoTileLayout);
    }

    /// <summary>Verifies import persists the registry's canonical information-tile layout text.</summary>
    [Fact]
    public async Task ImportAsync_WhenMasterInfoTileLayoutIsNoncanonical_WritesCanonicalText()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "noncanonical-info-tile-layout.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.Settings.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.MasterInfoTileLayout)),
                        new XAttribute("Value", " Latency, core,LATENCY, memory-usage "))),
                new XElement("Files")));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        FakeClashDataPackageSettings settings = new();
        ClashDataPackageService service = new(settings, directory.Path);

        await service.ImportAsync(packagePath, CancellationToken.None);

        Assert.Equal("latency,core,memory-usage", settings.MasterInfoTileLayout);
    }

    /// <summary>Verifies import enforces the registry's information-tile count limit.</summary>
    [Fact]
    public async Task ImportAsync_WhenMasterInfoTileLayoutExceedsLimit_RejectsPackage()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "too-many-info-tiles.xml");
        string importedLayout = string.Join(",", Enumerable.Range(1, 65).Select(index => $"tile-{index}"));
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.Settings.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.MasterInfoTileLayout)),
                        new XAttribute("Value", importedLayout))),
                new XElement("Files")));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        FakeClashDataPackageSettings settings = new()
        {
            MasterInfoTileLayout = "latency,core",
        };
        ClashDataPackageService service = new(settings, directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal("latency,core", settings.MasterInfoTileLayout);
    }

    /// <summary>Verifies legacy packages that omit the information-tile layout preserve the current value.</summary>
    [Fact]
    public async Task ImportAsync_WhenMasterInfoTileLayoutIsMissing_PreservesCurrentValue()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "legacy-settings.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.Settings.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.DisplayLanguage)),
                        new XAttribute("Value", AppLanguage.French.ToString()))),
                new XElement("Files")));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        FakeClashDataPackageSettings settings = new()
        {
            MasterInfoTileLayout = "latency,core",
        };
        ClashDataPackageService service = new(settings, directory.Path);

        await service.ImportAsync(packagePath, CancellationToken.None);

        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        Assert.Equal("latency,core", settings.MasterInfoTileLayout);
    }

    /// <summary>Verifies settings already applied during import are restored if a later setting fails.</summary>
    [Fact]
    public async Task ImportAsync_WhenSettingApplicationFails_RestoresPreviousSettings()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Combine(directory.Path, "setting-apply-fails.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", ClashDataPackageScope.Settings.ToString()),
                new XElement("Settings",
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.DisplayLanguage)),
                        new XAttribute("Value", AppLanguage.French.ToString())),
                    new XElement("Setting",
                        new XAttribute("Name", nameof(IClashDataPackageSettings.ActiveProfileId)),
                        new XAttribute("Value", "throw"))),
                new XElement("Files")));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        ThrowingClashDataPackageSettings settings = new()
        {
            DisplayLanguage = AppLanguage.English,
            ActiveProfileId = "direct",
            ThrowOnActiveProfileId = "throw",
        };
        ClashDataPackageService service = new(settings, directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
        Assert.Equal("direct", settings.ActiveProfileId);
    }

    [Theory]
    [InlineData((int)DataPackageTransactionCheckpoint.ManifestPersisted)]
    [InlineData((int)DataPackageTransactionCheckpoint.ImportSettingsApplied)]
    [InlineData((int)DataPackageTransactionCheckpoint.ImportFileApplied)]
    [InlineData((int)DataPackageTransactionCheckpoint.TransactionApplied)]
    [InlineData((int)DataPackageTransactionCheckpoint.TransactionCleanupStarting)]
    [InlineData((int)DataPackageTransactionCheckpoint.TransactionPayloadCleanupCompleted)]
    public async Task ImportAsync_WhenProcessStopsAtDurableCut_ReconcileCompletesDesiredGeneration(
        int crashCheckpoint)
    {
        DataPackageTransactionCheckpoint crashAt = (DataPackageTransactionCheckpoint)crashCheckpoint;
        using TemporaryDirectory directory = new();
        string firstTarget = Path.Combine(directory.Path, "mihomo", "profiles", "first.yaml");
        string secondTarget = Path.Combine(directory.Path, "mihomo", "profiles", "second.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(firstTarget)!);
        await File.WriteAllTextAsync(firstTarget, "first-old");
        await File.WriteAllTextAsync(secondTarget, "second-old");
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            [("mihomo/profiles/first.yaml", "first-new"), ("mihomo/profiles/second.yaml", "second-new")]);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        bool crashed = false;
        ClashDataPackageService crashingService = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (!crashed && checkpoint == crashAt)
                {
                    crashed = true;
                    throw new DataPackageSimulatedCrashException("simulated process stop");
                }
            });

        await Assert.ThrowsAsync<DataPackageSimulatedCrashException>(
            () => crashingService.ImportAsync(packagePath, CancellationToken.None));

        ClashDataPackageService recoveringService = new(settings, directory.Path);
        await recoveringService.ReconcilePendingTransactionAsync(CancellationToken.None);

        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        Assert.Equal("first-new", await File.ReadAllTextAsync(firstTarget));
        Assert.Equal("second-new", await File.ReadAllTextAsync(secondTarget));
        AssertTransactionClean(directory.Path);
    }

    [Theory]
    [InlineData((int)DataPackageTransactionCheckpoint.ManifestPersisted, false)]
    [InlineData((int)DataPackageTransactionCheckpoint.ResetMutationCompleted, false)]
    [InlineData((int)DataPackageTransactionCheckpoint.ResetSettingsApplied, true)]
    [InlineData((int)DataPackageTransactionCheckpoint.TransactionCleanupStarting, true)]
    [InlineData((int)DataPackageTransactionCheckpoint.TransactionPayloadCleanupCompleted, true)]
    public async Task ResetSettings_WhenProcessStopsAtDurableCut_ReconcileChoosesRecordedGeneration(
        int crashCheckpoint,
        bool resetWasCommitted)
    {
        DataPackageTransactionCheckpoint crashAt = (DataPackageTransactionCheckpoint)crashCheckpoint;
        using TemporaryDirectory directory = new();
        FakeClashDataPackageSettings settings = new()
        {
            DisplayLanguage = AppLanguage.English,
            MixedPort = 12000,
        };
        bool crashed = false;
        ClashDataPackageService crashingService = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (!crashed && checkpoint == crashAt)
                {
                    crashed = true;
                    throw new DataPackageSimulatedCrashException("simulated process stop");
                }
            });

        await Assert.ThrowsAsync<DataPackageSimulatedCrashException>(
            () => crashingService.ResetSettingsAsync(CancellationToken.None));

        ClashDataPackageService recoveringService = new(settings, directory.Path);
        await recoveringService.ReconcilePendingTransactionAsync(CancellationToken.None);

        Assert.Equal(
            resetWasCommitted ? AppLanguage.AutoDetect : AppLanguage.English,
            settings.DisplayLanguage);
        Assert.Equal(resetWasCommitted ? 10000 : 12000, settings.MixedPort);
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task StartupStyleRecovery_WithExclusiveLease_UsesOneAdmittedSettingsBatch()
    {
        using TemporaryDirectory directory = new();
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            []);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        ClashDataPackageService crashingService = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (checkpoint == DataPackageTransactionCheckpoint.ManifestPersisted)
                {
                    throw new DataPackageSimulatedCrashException("simulated process stop");
                }
            });
        await Assert.ThrowsAsync<DataPackageSimulatedCrashException>(
            () => crashingService.ImportAsync(packagePath, CancellationToken.None));

        MutationAdmissionBarrier barrier = new();
        settings.Admission = barrier;
        ClashDataPackageService recoveringService = new(settings, directory.Path);
        await using MutationAdmissionLease recoveryLease = await barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None);

        await recoveringService.ReconcilePendingTransactionAdmittedAsync(
            recoveryLease,
            CancellationToken.None);

        Assert.True(recoveryLease.IsExclusive);
        Assert.True(settings.AdmittedWriteCalls > 0);
        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task ReconcilePendingTransactionAsync_WhenManifestWasAltered_RejectsItBeforeMutation()
    {
        using TemporaryDirectory directory = new();
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            []);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        ClashDataPackageService crashingService = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (checkpoint == DataPackageTransactionCheckpoint.ManifestPersisted)
                {
                    throw new DataPackageSimulatedCrashException("simulated process stop");
                }
            });
        await Assert.ThrowsAsync<DataPackageSimulatedCrashException>(
            () => crashingService.ImportAsync(packagePath, CancellationToken.None));
        string manifestPath = Path.Combine(
            directory.Path,
            ".clashsharp-data-package-transaction",
            "manifest.json");
        string manifest = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("\"DisplayLanguage\":\"French\"", manifest, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.Replace(
                "\"DisplayLanguage\":\"French\"",
                "\"DisplayLanguage\":\"German\"",
                StringComparison.Ordinal));

        ClashDataPackageService recoveringService = new(settings, directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => recoveringService.ReconcilePendingTransactionAsync(CancellationToken.None));

        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
    }

    [Fact]
    public async Task ReconcilePendingTransactionAsync_WhenCommittedResetHasNoDesiredGeneration_RejectsIt()
    {
        using TemporaryDirectory directory = new();
        FakeClashDataPackageSettings settings = new()
        {
            DisplayLanguage = AppLanguage.English,
            MixedPort = 12000,
        };
        bool failedCleanup = false;
        ClashDataPackageService service = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (!failedCleanup && checkpoint == DataPackageTransactionCheckpoint.TransactionCleanupStarting)
                {
                    failedCleanup = true;
                    throw new IOException("simulated cleanup failure");
                }
            });
        await Assert.ThrowsAsync<IOException>(
            () => service.ResetSettingsAsync(CancellationToken.None));
        string manifestPath = Path.Combine(
            directory.Path,
            ".clashsharp-data-package-transaction",
            "manifest.json");
        JsonObject manifest = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(manifestPath)));
        Assert.Equal(7, manifest["phase"]!.GetValue<int>());
        manifest["desiredSettings"] = null;
        await WriteManifestWithValidHashAsync(manifestPath, manifest);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClashDataPackageService(settings, directory.Path)
                .ReconcilePendingTransactionAsync(CancellationToken.None));

        Assert.Equal(AppLanguage.AutoDetect, settings.DisplayLanguage);
        Assert.Equal(10000, settings.MixedPort);
    }

    [Fact]
    public async Task ImportAsync_WhenCommittedCleanupFails_DoesNotRollBackAppliedGeneration()
    {
        using TemporaryDirectory directory = new();
        string targetPath = Path.Combine(directory.Path, "mihomo", "profiles", "profile.yaml");
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            [("mihomo/profiles/profile.yaml", "new")]);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        bool failedCleanup = false;
        ClashDataPackageService service = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (!failedCleanup && checkpoint == DataPackageTransactionCheckpoint.TransactionCleanupStarting)
                {
                    failedCleanup = true;
                    throw new IOException("simulated cleanup failure");
                }
            });

        await Assert.ThrowsAsync<IOException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        Assert.Equal("new", await File.ReadAllTextAsync(targetPath));
        string retainedManifest = await File.ReadAllTextAsync(Path.Combine(
            directory.Path,
            ".clashsharp-data-package-transaction",
            "manifest.json"));
        Assert.Contains("\"phase\":7", retainedManifest, StringComparison.Ordinal);
        string retainedOperation = Directory.GetDirectories(Path.Combine(
            directory.Path,
            ".clashsharp-data-package-transaction")).Single();
        File.Delete(Path.Combine(retainedOperation, ".operation-owner"));
        await new ClashDataPackageService(settings, directory.Path)
            .ReconcilePendingTransactionAsync(CancellationToken.None);
        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        Assert.Equal("new", await File.ReadAllTextAsync(targetPath));
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task BeginImportAsync_HoldsCrossInstanceLeaseUntilRollbackCompletes()
    {
        using TemporaryDirectory directory = new();
        string targetPath = Path.Combine(directory.Path, "mihomo", "profiles", "profile.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "old");
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            [("mihomo/profiles/profile.yaml", "new")]);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        ClashDataPackageService firstInstance = new(settings, directory.Path);
        ClashDataPackageService secondInstance = new(settings, directory.Path);
        await using DataPackageTransactionReceipt receipt = await firstInstance.BeginImportAsync(
            packagePath,
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(
            () => secondInstance.ReconcilePendingTransactionAsync(CancellationToken.None));

        await receipt.RollbackAsync(CancellationToken.None);
        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
        Assert.Equal("old", await File.ReadAllTextAsync(targetPath));
        await secondInstance.ReconcilePendingTransactionAsync(CancellationToken.None);
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task BeginImportAsync_RollbackRestoresPreviousMasterHeroStatusLayout()
    {
        const string baselineLayout =
            "Latency,ActiveConnections,CurrentMode,ActiveProfile,MihomoService,StartupLaunch,SystemProxy,Availability";
        const string importedLayout =
            "UploadRate,DownloadRate,Latency,CoreStatus,SystemProxy,TransparentProxy,CurrentNode,Availability";
        using TemporaryDirectory directory = new();
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.MasterHeroStatusLayout), importedLayout)],
            []);
        FakeClashDataPackageSettings settings = new()
        {
            MasterHeroStatusLayout = baselineLayout,
        };
        ClashDataPackageService service = new(settings, directory.Path);
        await using DataPackageTransactionReceipt receipt = await service.BeginImportAsync(
            packagePath,
            CancellationToken.None);

        Assert.Equal(importedLayout, settings.MasterHeroStatusLayout);

        await receipt.RollbackAsync(CancellationToken.None);

        Assert.Equal(baselineLayout, settings.MasterHeroStatusLayout);
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task BeginImportAsync_AfterCommit_RejectsOppositeDecision()
    {
        using TemporaryDirectory directory = new();
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            []);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        await using DataPackageTransactionReceipt receipt = await new ClashDataPackageService(
            settings,
            directory.Path).BeginImportAsync(packagePath, CancellationToken.None);

        await receipt.CommitAsync(CancellationToken.None);
        await receipt.CommitAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receipt.RollbackAsync(CancellationToken.None));

        Assert.Equal(AppLanguage.French, settings.DisplayLanguage);
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task BeginImportAsync_WhenRollbackFails_RetainsRollbackPhaseForSameDecisionRetry()
    {
        using TemporaryDirectory directory = new();
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [
                (nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString()),
                (nameof(IClashDataPackageSettings.ActiveProfileId), "imported"),
            ],
            []);
        ThrowingClashDataPackageSettings settings = new()
        {
            DisplayLanguage = AppLanguage.English,
            ActiveProfileId = "direct",
        };
        await using DataPackageTransactionReceipt receipt = await new ClashDataPackageService(
            settings,
            directory.Path).BeginImportAsync(packagePath, CancellationToken.None);
        settings.ThrowOnActiveProfileId = "direct";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => receipt.RollbackAsync(CancellationToken.None));

        string retainedManifest = await File.ReadAllTextAsync(Path.Combine(
            directory.Path,
            ".clashsharp-data-package-transaction",
            "manifest.json"));
        Assert.Contains("\"phase\":6", retainedManifest, StringComparison.Ordinal);
        settings.ThrowOnActiveProfileId = null;
        await receipt.RollbackAsync(CancellationToken.None);
        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
        Assert.Equal("direct", settings.ActiveProfileId);
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task BeginResetSettings_RollbackRestoresPreviousSettingsGeneration()
    {
        const string baselineLayout =
            "Latency,ActiveConnections,CurrentMode,ActiveProfile,MihomoService,StartupLaunch,SystemProxy,Availability";
        using TemporaryDirectory directory = new();
        FakeClashDataPackageSettings settings = new()
        {
            DisplayLanguage = AppLanguage.English,
            MixedPort = 12000,
            MasterHeroStatusLayout = baselineLayout,
        };
        ClashDataPackageService service = new(settings, directory.Path);
        await using DataPackageTransactionReceipt receipt = service.BeginResetSettings();
        Assert.Equal(AppLanguage.AutoDetect, settings.DisplayLanguage);
        Assert.Equal(10000, settings.MixedPort);
        Assert.Equal(DefaultMasterHeroStatusLayout, settings.MasterHeroStatusLayout);

        await receipt.RollbackAsync(CancellationToken.None);

        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
        Assert.Equal(12000, settings.MixedPort);
        Assert.Equal(baselineLayout, settings.MasterHeroStatusLayout);
        AssertTransactionClean(directory.Path);
    }

    [Fact]
    public async Task ReconcilePendingTransactionAsync_WhenManifestExceedsAggregateBackupBudget_RejectsIt()
    {
        using TemporaryDirectory directory = new();
        List<(string Path, string Content)> files = [];
        for (int index = 0; index < 5; index++)
        {
            string relativePath = $"mihomo/profiles/{index}.yaml";
            string targetPath = Path.Combine(
                directory.Path,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, "old");
            files.Add((relativePath, "new"));
        }

        string packagePath = await WriteImportPackageAsync(directory.Path, [], files);
        FakeClashDataPackageSettings settings = new();
        ClashDataPackageService crashingService = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (checkpoint == DataPackageTransactionCheckpoint.ManifestPersisted)
                {
                    throw new DataPackageSimulatedCrashException("simulated process stop");
                }
            });
        await Assert.ThrowsAsync<DataPackageSimulatedCrashException>(
            () => crashingService.ImportAsync(packagePath, CancellationToken.None));
        string manifestPath = Path.Combine(
            directory.Path,
            ".clashsharp-data-package-transaction",
            "manifest.json");
        JsonObject manifest = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(manifestPath)));
        JsonArray manifestFiles = Assert.IsType<JsonArray>(manifest["files"]);
        foreach (JsonNode? file in manifestFiles)
        {
            Assert.IsType<JsonObject>(file)["backupLength"] = 64L * 1024 * 1024;
        }

        await WriteManifestWithValidHashAsync(manifestPath, manifest);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClashDataPackageService(settings, directory.Path)
                .ReconcilePendingTransactionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ImportAsync_WhenLaterRecoveryPayloadIsCorrupt_RollsBackFilesAndSettings()
    {
        using TemporaryDirectory directory = new();
        string firstTarget = Path.Combine(directory.Path, "mihomo", "profiles", "first.yaml");
        string secondTarget = Path.Combine(directory.Path, "mihomo", "profiles", "second.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(firstTarget)!);
        await File.WriteAllTextAsync(firstTarget, "first-old");
        await File.WriteAllTextAsync(secondTarget, "second-old");
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            [("mihomo/profiles/first.yaml", "first-new"), ("mihomo/profiles/second.yaml", "second-new")]);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        bool corrupted = false;
        ClashDataPackageService service = new(
            settings,
            directory.Path,
            checkpoint =>
            {
                if (!corrupted && checkpoint == DataPackageTransactionCheckpoint.ImportFileApplied)
                {
                    corrupted = true;
                    string operationDirectory = Directory.GetDirectories(
                        Path.Combine(directory.Path, ".clashsharp-data-package-transaction")).Single();
                    File.WriteAllText(Path.Combine(operationDirectory, "payload-00000001.new"), "corrupt");
                }
            });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
        Assert.Equal("first-old", await File.ReadAllTextAsync(firstTarget));
        Assert.Equal("second-old", await File.ReadAllTextAsync(secondTarget));
        AssertTransactionClean(directory.Path);
    }

    [Theory]
    [InlineData(".clashsharp-data-package-transaction/manifest.json")]
    [InlineData(".clashsharp-data-package-transaction.lock")]
    [InlineData("mihomo/profile.yaml:stream")]
    [InlineData("mihomo/../profile.yaml")]
    [InlineData("mihomo//profile.yaml")]
    [InlineData("NUL.txt")]
    public async Task ImportAsync_WhenTargetPathIsPrivateOrAliased_RejectsBeforeMutation(string target)
    {
        using TemporaryDirectory directory = new();
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            [(target, "payload")]);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };
        ClashDataPackageService service = new(settings, directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
    }

    [Theory]
    [InlineData(ClashDataPackageScope.Settings, "mihomo/profiles/profile.yaml")]
    [InlineData(ClashDataPackageScope.SettingsAndProxyConfiguration, "logs.sqlite3")]
    public async Task ImportAsync_WhenFileIsOutsideDeclaredScope_RejectsBeforeMutation(
        ClashDataPackageScope scope,
        string target)
    {
        using TemporaryDirectory directory = new();
        string packagePath = await WriteImportPackageAsync(
            directory.Path,
            [(nameof(IClashDataPackageSettings.DisplayLanguage), AppLanguage.French.ToString())],
            [(target, "payload")],
            scope);
        FakeClashDataPackageSettings settings = new() { DisplayLanguage = AppLanguage.English };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClashDataPackageService(settings, directory.Path)
                .ImportAsync(packagePath, CancellationToken.None));

        Assert.Equal(AppLanguage.English, settings.DisplayLanguage);
        Assert.False(File.Exists(Path.Combine(directory.Path, target)));
    }

    [Fact]
    public async Task ReconcilePendingTransactionAsync_CleansOnlyOwnedOrphanDirectories()
    {
        using TemporaryDirectory directory = new();
        string transactionRoot = Path.Combine(directory.Path, ".clashsharp-data-package-transaction");
        Directory.CreateDirectory(transactionRoot);
        await File.WriteAllTextAsync(
            Path.Combine(transactionRoot, ".owner"),
            "ClashSharp.DataPackageTransaction/1");
        Guid ownedId = Guid.NewGuid();
        string ownedDirectory = Path.Combine(transactionRoot, ownedId.ToString("N"));
        Directory.CreateDirectory(ownedDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(ownedDirectory, ".operation-owner"),
            $"ClashSharp.DataPackageTransaction/1/{ownedId:N}");
        await File.WriteAllTextAsync(Path.Combine(ownedDirectory, "payload"), "owned");
        string unownedDirectory = Path.Combine(transactionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unownedDirectory);
        await File.WriteAllTextAsync(Path.Combine(unownedDirectory, "keep.txt"), "foreign");

        ClashDataPackageService service = new(new FakeClashDataPackageSettings(), directory.Path);
        await service.ReconcilePendingTransactionAsync(CancellationToken.None);

        Assert.False(Directory.Exists(ownedDirectory));
        Assert.True(File.Exists(Path.Combine(unownedDirectory, "keep.txt")));
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

    private static async Task<string> WriteImportPackageAsync(
        string directory,
        IReadOnlyList<(string Name, string Value)> settings,
        IReadOnlyList<(string Path, string Content)> files,
        ClashDataPackageScope scope = ClashDataPackageScope.SettingsAndProxyConfiguration)
    {
        string packagePath = Path.Combine(directory, $"import-{Guid.NewGuid():N}.xml");
        XDocument document = new(
            new XElement("ClashSharpDataPackage",
                new XAttribute("Format", "ClashSharp.XmlDataPackage"),
                new XAttribute("Version", "1"),
                new XAttribute("Scope", scope.ToString()),
                new XElement(
                    "Settings",
                    settings.Select(setting => new XElement(
                        "Setting",
                        new XAttribute("Name", setting.Name),
                        new XAttribute("Value", setting.Value)))),
                new XElement(
                    "Files",
                    files.Select(file => new XElement(
                        "File",
                        new XAttribute("Path", file.Path),
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(file.Content)))))));
        await File.WriteAllTextAsync(packagePath, document.ToString(SaveOptions.DisableFormatting));
        return packagePath;
    }

    private static void AssertTransactionClean(string localDataDirectory)
    {
        string root = Path.Combine(localDataDirectory, ".clashsharp-data-package-transaction");
        Assert.False(File.Exists(Path.Combine(root, "manifest.json")));
        Assert.Empty(Directory.GetDirectories(root));
    }

    private static async Task WriteManifestWithValidHashAsync(string manifestPath, JsonObject manifest)
    {
        JsonSerializerOptions options = new() { WriteIndented = false };
        manifest["manifestSha256"] = string.Empty;
        string canonicalJson = manifest.ToJsonString(options);
        manifest["manifestSha256"] = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(options));
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

    private sealed class FakeClashDataPackageSettings :
        IClashDataPackageSettings,
        IClashDataPackageAdmittedSettings
    {
        public MutationAdmissionBarrier? Admission { get; set; }

        public int AdmittedWriteCalls { get; private set; }

        public void WriteAdmitted(
            MutationAdmissionLease admissionLease,
            Action<IClashDataPackageSettings> mutation)
        {
            MutationAdmissionBarrier admission = Assert.IsType<MutationAdmissionBarrier>(Admission);
            admission.EnsureActiveExclusiveLease(admissionLease);
            AdmittedWriteCalls++;
            mutation(this);
        }

        public void ResetAllSettings()
        {
            DisplayLanguage = AppLanguage.AutoDetect;
            AppThemeMode = AppThemeMode.FollowSystem;
            ActiveProfileId = "direct";
            CurrentMode = ClashSharpMode.Disabled;
            TransparentProxyEnabled = true;
            MixedPort = 10000;
            MasterHeroStatusLayout = DefaultMasterHeroStatusLayout;
        }

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

        public string MasterHeroStatusLayout { get; set; } = DefaultMasterHeroStatusLayout;

        public string MasterInfoTileLayout { get; set; } =
            "core,upload-rate,download-rate,active-connections,transparent-proxy,latency,active-profile,current-mode";
    }

    private sealed class ThrowingClashDataPackageSettings : IClashDataPackageSettings
    {
        private string _activeProfileId = "direct";

        public string? ThrowOnActiveProfileId { get; set; }

        public void ResetAllSettings()
        {
            DisplayLanguage = AppLanguage.AutoDetect;
            ActiveProfileId = "direct";
            CurrentMode = ClashSharpMode.Disabled;
            MixedPort = 10000;
            MasterHeroStatusLayout = DefaultMasterHeroStatusLayout;
        }

        public AppLanguage DisplayLanguage { get; set; } = AppLanguage.AutoDetect;

        public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.FollowSystem;

        public AppAccentColorMode AppAccentColorMode { get; set; } = AppAccentColorMode.FollowSystem;

        public string AppAccentColorValue { get; set; } = "#FF0078D4";

        public bool LaunchAtStartupEnabled { get; set; }

        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        public string ActiveProfileId
        {
            get => _activeProfileId;
            set
            {
                if (StringComparer.Ordinal.Equals(value, ThrowOnActiveProfileId))
                {
                    throw new InvalidOperationException("profile rejected");
                }

                _activeProfileId = value;
            }
        }

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

        public string MasterHeroStatusLayout { get; set; } = DefaultMasterHeroStatusLayout;

        public string MasterInfoTileLayout { get; set; } =
            "core,upload-rate,download-rate,active-connections,transparent-proxy,latency,active-profile,current-mode";
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
