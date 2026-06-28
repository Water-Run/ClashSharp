/*
 * Clash Data Package Service
 * Imports and exports Clash# settings and local data as an XML package
 *
 * @author: WaterRun
 * @file: Service/ClashDataPackageService.cs
 * @date: 2026-06-25
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Settings contract required by <see cref="ClashDataPackageService"/>.</summary>
internal interface IClashDataPackageSettings
{
    AppLanguage DisplayLanguage { get; set; }

    AppThemeMode AppThemeMode { get; set; }

    AppAccentColorMode AppAccentColorMode { get; set; }

    string AppAccentColorValue { get; set; }

    bool LaunchAtStartupEnabled { get; set; }

    ClashSharpMode CurrentMode { get; set; }

    string ActiveProfileId { get; set; }

    bool TransparentProxyEnabled { get; set; }

    int MixedPort { get; set; }

    bool ConnectionSamplingEnabled { get; set; }

    int ConnectionSamplingIntervalSeconds { get; set; }

    bool RestoreProxyOnExit { get; set; }

    bool CheckStaleProxyOnStartup { get; set; }

    bool StartupConflictCheckEnabled { get; set; }

    StartupBehaviorMode StartupBehaviorMode { get; set; }

    bool ShowStartupGuideOnStartup { get; set; }

    bool TriggersEnabled { get; set; }

    bool TriggerNotificationsEnabled { get; set; }

    CloseBehaviorMode CloseBehaviorMode { get; set; }

    bool TrayFadeInactiveIcon { get; set; }

    bool TrayUseMonochromeInactiveIcon { get; set; }

    string TrayVisibleFeatureIds { get; set; }

    bool NotificationEnabled { get; set; }

    NotificationLevel NotificationLevel { get; set; }

    MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; }

    bool MainlandChinaUrlBlockingEnabled { get; set; }

    string ConnectionTestUrl { get; set; }

    string ConnectionTestProxyUrl1 { get; set; }

    string ConnectionTestProxyUrl2 { get; set; }

    string ConnectionTestDirectUrl { get; set; }
}

/// <summary>Imports and exports Clash# user settings and local data as a versioned XML package.</summary>
/// <remarks>
/// Invariants: File entries are always relative to the local application data directory.
/// Thread safety: Not thread-safe; intended for one user-triggered import or export at a time.
/// Side effects: Reads and writes package files and may overwrite local application data files during import.
/// </remarks>
internal sealed partial class ClashDataPackageService
{
    private const string PackageRootName = "ClashSharpDataPackage";
    private const string PackageFormat = "ClashSharp.XmlDataPackage";
    private const string PackageVersion = "1";
    private const string ProfileCatalogFileName = "ProfileCatalog.json";
    private const string MihomoDirectoryName = "mihomo";

    private readonly IClashDataPackageSettings _settings;
    private readonly string _localDataDirectory;

    private static readonly SettingDescriptor[] SettingDescriptors =
    [
        EnumSetting(nameof(IClashDataPackageSettings.DisplayLanguage), settings => settings.DisplayLanguage, (settings, value) => settings.DisplayLanguage = value),
        EnumSetting(nameof(IClashDataPackageSettings.AppThemeMode), settings => settings.AppThemeMode, (settings, value) => settings.AppThemeMode = value),
        EnumSetting(nameof(IClashDataPackageSettings.AppAccentColorMode), settings => settings.AppAccentColorMode, (settings, value) => settings.AppAccentColorMode = value),
        StringSetting(nameof(IClashDataPackageSettings.AppAccentColorValue), settings => settings.AppAccentColorValue, (settings, value) => settings.AppAccentColorValue = value),
        BoolSetting(nameof(IClashDataPackageSettings.LaunchAtStartupEnabled), settings => settings.LaunchAtStartupEnabled, (settings, value) => settings.LaunchAtStartupEnabled = value),
        EnumSetting(nameof(IClashDataPackageSettings.CurrentMode), settings => settings.CurrentMode, (settings, value) => settings.CurrentMode = value),
        StringSetting(nameof(IClashDataPackageSettings.ActiveProfileId), settings => settings.ActiveProfileId, (settings, value) => settings.ActiveProfileId = value),
        BoolSetting(nameof(IClashDataPackageSettings.TransparentProxyEnabled), settings => settings.TransparentProxyEnabled, (settings, value) => settings.TransparentProxyEnabled = value),
        IntSetting(nameof(IClashDataPackageSettings.MixedPort), settings => settings.MixedPort, (settings, value) => settings.MixedPort = value),
        BoolSetting(nameof(IClashDataPackageSettings.ConnectionSamplingEnabled), settings => settings.ConnectionSamplingEnabled, (settings, value) => settings.ConnectionSamplingEnabled = value),
        IntSetting(nameof(IClashDataPackageSettings.ConnectionSamplingIntervalSeconds), settings => settings.ConnectionSamplingIntervalSeconds, (settings, value) => settings.ConnectionSamplingIntervalSeconds = value),
        BoolSetting(nameof(IClashDataPackageSettings.RestoreProxyOnExit), settings => settings.RestoreProxyOnExit, (settings, value) => settings.RestoreProxyOnExit = value),
        BoolSetting(nameof(IClashDataPackageSettings.CheckStaleProxyOnStartup), settings => settings.CheckStaleProxyOnStartup, (settings, value) => settings.CheckStaleProxyOnStartup = value),
        BoolSetting(nameof(IClashDataPackageSettings.StartupConflictCheckEnabled), settings => settings.StartupConflictCheckEnabled, (settings, value) => settings.StartupConflictCheckEnabled = value),
        EnumSetting(nameof(IClashDataPackageSettings.StartupBehaviorMode), settings => settings.StartupBehaviorMode, (settings, value) => settings.StartupBehaviorMode = value),
        BoolSetting(nameof(IClashDataPackageSettings.ShowStartupGuideOnStartup), settings => settings.ShowStartupGuideOnStartup, (settings, value) => settings.ShowStartupGuideOnStartup = value),
        BoolSetting(nameof(IClashDataPackageSettings.TriggersEnabled), settings => settings.TriggersEnabled, (settings, value) => settings.TriggersEnabled = value),
        BoolSetting(nameof(IClashDataPackageSettings.TriggerNotificationsEnabled), settings => settings.TriggerNotificationsEnabled, (settings, value) => settings.TriggerNotificationsEnabled = value),
        EnumSetting(nameof(IClashDataPackageSettings.CloseBehaviorMode), settings => settings.CloseBehaviorMode, (settings, value) => settings.CloseBehaviorMode = value),
        BoolSetting(nameof(IClashDataPackageSettings.TrayFadeInactiveIcon), settings => settings.TrayFadeInactiveIcon, (settings, value) => settings.TrayFadeInactiveIcon = value),
        BoolSetting(nameof(IClashDataPackageSettings.TrayUseMonochromeInactiveIcon), settings => settings.TrayUseMonochromeInactiveIcon, (settings, value) => settings.TrayUseMonochromeInactiveIcon = value),
        StringSetting(nameof(IClashDataPackageSettings.TrayVisibleFeatureIds), settings => settings.TrayVisibleFeatureIds, (settings, value) => settings.TrayVisibleFeatureIds = value),
        BoolSetting(nameof(IClashDataPackageSettings.NotificationEnabled), settings => settings.NotificationEnabled, (settings, value) => settings.NotificationEnabled = value),
        EnumSetting(nameof(IClashDataPackageSettings.NotificationLevel), settings => settings.NotificationLevel, (settings, value) => settings.NotificationLevel = value),
        EnumSetting(nameof(IClashDataPackageSettings.MainlandChinaFeatureMode), settings => settings.MainlandChinaFeatureMode, (settings, value) => settings.MainlandChinaFeatureMode = value),
        BoolSetting(nameof(IClashDataPackageSettings.MainlandChinaUrlBlockingEnabled), settings => settings.MainlandChinaUrlBlockingEnabled, (settings, value) => settings.MainlandChinaUrlBlockingEnabled = value),
        StringSetting(nameof(IClashDataPackageSettings.ConnectionTestUrl), settings => settings.ConnectionTestUrl, (settings, value) => settings.ConnectionTestUrl = value),
        StringSetting(nameof(IClashDataPackageSettings.ConnectionTestProxyUrl1), settings => settings.ConnectionTestProxyUrl1, (settings, value) => settings.ConnectionTestProxyUrl1 = value),
        StringSetting(nameof(IClashDataPackageSettings.ConnectionTestProxyUrl2), settings => settings.ConnectionTestProxyUrl2, (settings, value) => settings.ConnectionTestProxyUrl2 = value),
        StringSetting(nameof(IClashDataPackageSettings.ConnectionTestDirectUrl), settings => settings.ConnectionTestDirectUrl, (settings, value) => settings.ConnectionTestDirectUrl = value),
    ];

    /// <summary>Initializes a data package service.</summary>
    /// <param name="settings">Settings store to read from and write to. Must not be null.</param>
    /// <param name="localDataDirectory">Local application data root. Must not be null or empty.</param>
    public ClashDataPackageService(IClashDataPackageSettings settings, string localDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localDataDirectory = Path.GetFullPath(localDataDirectory);
    }

    /// <summary>Exports settings and selected local files into an XML package.</summary>
    /// <param name="packagePath">Destination XML path. Must not be null or whitespace.</param>
    /// <param name="scope">Package coverage scope.</param>
    /// <param name="cancellationToken">Cancels file reads and package writing.</param>
    /// <returns>A task that completes when the package has been written.</returns>
    public async Task ExportAsync(string packagePath, ClashDataPackageScope scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        string fullPackagePath = Path.GetFullPath(packagePath);
        string? packageDirectory = Path.GetDirectoryName(fullPackagePath);
        if (!string.IsNullOrEmpty(packageDirectory))
        {
            Directory.CreateDirectory(packageDirectory);
        }

        XElement root = new(
            PackageRootName,
            new XAttribute("Format", PackageFormat),
            new XAttribute("Version", PackageVersion),
            new XAttribute("Scope", scope.ToString()),
            ExportSettings(),
            await ExportFilesAsync(fullPackagePath, scope, cancellationToken));
        XDocument document = new(new XDeclaration("1.0", "utf-8", null), root);
        await File.WriteAllTextAsync(fullPackagePath, document.ToString(SaveOptions.DisableFormatting), cancellationToken);
    }

    /// <summary>Imports settings and file payloads from an XML package.</summary>
    /// <param name="packagePath">Source XML path. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels file writes.</param>
    /// <returns>A task that completes after settings and files are imported.</returns>
    /// <exception cref="InvalidDataException">The package format is invalid or contains unsafe file paths.</exception>
    public async Task ImportAsync(string packagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        XDocument document = XDocument.Load(packagePath);
        XElement root = ValidatePackageRoot(document);
        ImportSettings(root.Element("Settings"));

        foreach (XElement fileElement in root.Element("Files")?.Elements("File") ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = fileElement.Attribute("Path")?.Value ?? string.Empty;
            string targetPath = ResolveImportFilePath(relativePath);
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            byte[] content = Convert.FromBase64String(fileElement.Value);
            await File.WriteAllBytesAsync(targetPath, content, cancellationToken);
        }
    }

    private XElement ExportSettings()
    {
        return new XElement(
            "Settings",
            SettingDescriptors.Select(descriptor => new XElement(
                "Setting",
                new XAttribute("Name", descriptor.Name),
                new XAttribute("Value", descriptor.Read(_settings)))));
    }

    private async Task<XElement> ExportFilesAsync(string packagePath, ClashDataPackageScope scope, CancellationToken cancellationToken)
    {
        List<XElement> files = [];
        foreach (string filePath in EnumerateScopedFiles(scope, packagePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = ToPackageRelativePath(filePath);
            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            files.Add(new XElement(
                "File",
                new XAttribute("Path", relativePath),
                Convert.ToBase64String(bytes)));
        }

        return new XElement("Files", files);
    }

    private IEnumerable<string> EnumerateScopedFiles(ClashDataPackageScope scope, string packagePath)
    {
        if (scope == ClashDataPackageScope.Settings || !Directory.Exists(_localDataDirectory))
        {
            return [];
        }

        string normalizedPackagePath = Path.GetFullPath(packagePath);
        IEnumerable<string> files = scope switch
        {
            ClashDataPackageScope.SettingsAndProxyConfiguration => EnumerateProxyConfigurationFiles(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported data package scope."),
        };

        return files
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, normalizedPackagePath, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> EnumerateProxyConfigurationFiles()
    {
        string profileCatalogPath = Path.Combine(_localDataDirectory, ProfileCatalogFileName);
        if (File.Exists(profileCatalogPath))
        {
            yield return profileCatalogPath;
        }

        string mihomoDirectory = Path.Combine(_localDataDirectory, MihomoDirectoryName);
        if (!Directory.Exists(mihomoDirectory))
        {
            yield break;
        }

        foreach (string filePath in Directory.EnumerateFiles(mihomoDirectory, "*", SearchOption.AllDirectories))
        {
            yield return filePath;
        }
    }

    private void ImportSettings(XElement? settingsElement)
    {
        if (settingsElement is null)
        {
            return;
        }

        Dictionary<string, string> values = settingsElement
            .Elements("Setting")
            .Where(element => element.Attribute("Name") is not null)
            .ToDictionary(
                element => element.Attribute("Name")!.Value,
                element => element.Attribute("Value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        foreach (SettingDescriptor descriptor in SettingDescriptors)
        {
            if (values.TryGetValue(descriptor.Name, out string? value))
            {
                descriptor.Write(_settings, value);
            }
        }
    }

    private XElement ValidatePackageRoot(XDocument document)
    {
        XElement root = document.Root
            ?? throw new InvalidDataException("Clash# data package is empty.");
        if (root.Name.LocalName != PackageRootName
            || root.Attribute("Format")?.Value != PackageFormat
            || root.Attribute("Version")?.Value != PackageVersion)
        {
            throw new InvalidDataException("Clash# data package format is not supported.");
        }

        return root;
    }

    private string ToPackageRelativePath(string filePath)
    {
        return Path.GetRelativePath(_localDataDirectory, filePath).Replace('\\', '/');
    }

    private string ResolveImportFilePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Clash# data package contains an unsafe file path.");
        }

        string normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string root = EnsureTrailingSeparator(Path.GetFullPath(_localDataDirectory));
        string targetPath = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
        if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Clash# data package contains an unsafe file path.");
        }

        return targetPath;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : $"{path}{Path.DirectorySeparatorChar}";
    }

    private static SettingDescriptor StringSetting(string name, Func<IClashDataPackageSettings, string> read, Action<IClashDataPackageSettings, string> write)
    {
        return new SettingDescriptor(name, read, write);
    }

    private static SettingDescriptor BoolSetting(string name, Func<IClashDataPackageSettings, bool> read, Action<IClashDataPackageSettings, bool> write)
    {
        return new SettingDescriptor(
            name,
            settings => read(settings).ToString(CultureInfo.InvariantCulture),
            (settings, value) => write(settings, bool.Parse(value)));
    }

    private static SettingDescriptor IntSetting(string name, Func<IClashDataPackageSettings, int> read, Action<IClashDataPackageSettings, int> write)
    {
        return new SettingDescriptor(
            name,
            settings => read(settings).ToString(CultureInfo.InvariantCulture),
            (settings, value) => write(settings, int.Parse(value, CultureInfo.InvariantCulture)));
    }

    private static SettingDescriptor EnumSetting<TEnum>(string name, Func<IClashDataPackageSettings, TEnum> read, Action<IClashDataPackageSettings, TEnum> write)
        where TEnum : struct, Enum
    {
        return new SettingDescriptor(
            name,
            settings => read(settings).ToString(),
            (settings, value) => write(settings, Enum.Parse<TEnum>(value)));
    }

    private readonly record struct SettingDescriptor(
        string Name,
        Func<IClashDataPackageSettings, string> Read,
        Action<IClashDataPackageSettings, string> Write);
}
