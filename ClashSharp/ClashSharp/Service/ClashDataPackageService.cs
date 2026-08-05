using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Service;

/// <summary>Settings contract required by <see cref="ClashDataPackageService"/>.</summary>
internal interface IClashDataPackageSettings
{
    /// <summary>Resets every persisted setting to its product default.</summary>
    void ResetAllSettings();

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

    string MasterHeroStatusLayout { get; set; }

    string MasterInfoTileLayout { get; set; }
}

/// <summary>Executes package settings batches under an explicitly owned process admission lease.</summary>
internal interface IClashDataPackageAdmittedSettings
{
    void WriteAdmitted(
        MutationAdmissionLease admissionLease,
        Action<IClashDataPackageSettings> mutation);
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
    private const string GeneratedMihomoConfigFileName = "config.yaml";

    private const string GeneratedMihomoStateFileName = "config.runtime-state.json";

    private const string RuntimeGenerationsDirectoryName = "runtime-generations";

    internal const long MaxPackageBytes = 192L * 1024 * 1024;

    private const int MaxPackageFileCount = 4096;

    private const int MaxPackageEntryBytes = 16 * 1024 * 1024;

    private const long MaxPackageDecodedBytes = 128L * 1024 * 1024;

    private readonly IClashDataPackageSettings _settings;
    private readonly IClashDataPackageAdmittedSettings? _admittedSettings;
    private readonly string _localDataDirectory;

    private static readonly SettingDescriptor[] SettingDescriptors =
    [
        EnumSetting(nameof(IClashDataPackageSettings.DisplayLanguage), settings => settings.DisplayLanguage, (settings, value) => settings.DisplayLanguage = value),
        EnumSetting(nameof(IClashDataPackageSettings.AppThemeMode), settings => settings.AppThemeMode, (settings, value) => settings.AppThemeMode = value),
        EnumSetting(nameof(IClashDataPackageSettings.AppAccentColorMode), settings => settings.AppAccentColorMode, (settings, value) => settings.AppAccentColorMode = value),
        StringSetting(nameof(IClashDataPackageSettings.AppAccentColorValue), settings => settings.AppAccentColorValue, (settings, value) => settings.AppAccentColorValue = value),
        BoolSetting(nameof(IClashDataPackageSettings.LaunchAtStartupEnabled), settings => settings.LaunchAtStartupEnabled, (settings, value) => settings.LaunchAtStartupEnabled = value),
        EnumSetting(
            nameof(IClashDataPackageSettings.CurrentMode),
            settings => settings.CurrentMode,
            (settings, value) => settings.CurrentMode = value,
            static value => value != ClashSharpMode.Faulted),
        StringSetting(nameof(IClashDataPackageSettings.ActiveProfileId), settings => settings.ActiveProfileId, (settings, value) => settings.ActiveProfileId = value),
        BoolSetting(nameof(IClashDataPackageSettings.TransparentProxyEnabled), settings => settings.TransparentProxyEnabled, (settings, value) => settings.TransparentProxyEnabled = value),
        RangedIntSetting(nameof(IClashDataPackageSettings.MixedPort), settings => settings.MixedPort, (settings, value) => settings.MixedPort = value, 1, 65535),
        BoolSetting(nameof(IClashDataPackageSettings.ConnectionSamplingEnabled), settings => settings.ConnectionSamplingEnabled, (settings, value) => settings.ConnectionSamplingEnabled = value),
        RangedIntSetting(nameof(IClashDataPackageSettings.ConnectionSamplingIntervalSeconds), settings => settings.ConnectionSamplingIntervalSeconds, (settings, value) => settings.ConnectionSamplingIntervalSeconds = value, 3, 300),
        BoolSetting(nameof(IClashDataPackageSettings.RestoreProxyOnExit), settings => settings.RestoreProxyOnExit, (settings, value) => settings.RestoreProxyOnExit = value),
        BoolSetting(nameof(IClashDataPackageSettings.CheckStaleProxyOnStartup), settings => settings.CheckStaleProxyOnStartup, (settings, value) => settings.CheckStaleProxyOnStartup = value),
        BoolSetting(nameof(IClashDataPackageSettings.StartupConflictCheckEnabled), settings => settings.StartupConflictCheckEnabled, (settings, value) => settings.StartupConflictCheckEnabled = value),
        EnumSetting(nameof(IClashDataPackageSettings.StartupBehaviorMode), settings => settings.StartupBehaviorMode, (settings, value) => settings.StartupBehaviorMode = value),
        BoolSetting(nameof(IClashDataPackageSettings.ShowStartupGuideOnStartup), settings => settings.ShowStartupGuideOnStartup, (settings, value) => settings.ShowStartupGuideOnStartup = value),
        BoolSetting(nameof(IClashDataPackageSettings.TriggersEnabled), settings => settings.TriggersEnabled, (settings, value) => settings.TriggersEnabled = value),
        BoolSetting(nameof(IClashDataPackageSettings.TriggerNotificationsEnabled), settings => settings.TriggerNotificationsEnabled, (settings, value) => settings.TriggerNotificationsEnabled = value),
        EnumSetting(nameof(IClashDataPackageSettings.CloseBehaviorMode), settings => settings.CloseBehaviorMode, (settings, value) => settings.CloseBehaviorMode = value),
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
        RegisteredStringSetting(SettingsRegistry.Keys.MasterHeroStatusLayout, settings => settings.MasterHeroStatusLayout, (settings, value) => settings.MasterHeroStatusLayout = value),
        RegisteredStringSetting(SettingsRegistry.Keys.MasterInfoTileLayout, settings => settings.MasterInfoTileLayout, (settings, value) => settings.MasterInfoTileLayout = value),
    ];

    /// <summary>Initializes a data package service.</summary>
    /// <param name="settings">Settings store to read from and write to. Must not be null.</param>
    /// <param name="localDataDirectory">Local application data root. Must not be null or empty.</param>
    public ClashDataPackageService(IClashDataPackageSettings settings, string localDataDirectory)
        : this(settings, localDataDirectory, checkpoint: null)
    {
    }

    /// <summary>Initializes a data package service with an optional crash-test checkpoint.</summary>
    internal ClashDataPackageService(
        IClashDataPackageSettings settings,
        string localDataDirectory,
        Action<DataPackageTransactionCheckpoint>? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _admittedSettings = settings as IClashDataPackageAdmittedSettings;
        _localDataDirectory = Path.GetFullPath(localDataDirectory);
        _checkpoint = checkpoint;
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

#if UNIT_TESTS
    /// <summary>Imports settings and file payloads from an XML package.</summary>
    /// <param name="packagePath">Source XML path. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels file writes.</param>
    /// <returns>A task that completes after settings and files are imported.</returns>
    /// <exception cref="InvalidDataException">The package format is invalid or contains unsafe file paths.</exception>
    public async Task ImportAsync(string packagePath, CancellationToken cancellationToken)
    {
        DataPackageTransactionReceipt receipt = await BeginImportAsync(
            packagePath,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await receipt.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await receipt.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies an imported settings/files generation while retaining its previous generation until
    /// external runtime activation explicitly commits or rolls it back.
    /// </summary>
    internal async Task<DataPackageTransactionReceipt> BeginImportAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        return await BeginImportCoreAsync(
            packagePath,
            admissionLease: null,
            cancellationToken).ConfigureAwait(false);
    }
#endif

    /// <summary>Begins an import under the caller's already-drained settings lease.</summary>
    internal Task<DataPackageTransactionReceipt> BeginImportAdmittedAsync(
        string packagePath,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admissionLease);
        return BeginImportCoreAsync(packagePath, admissionLease, cancellationToken);
    }

    private async Task<DataPackageTransactionReceipt> BeginImportCoreAsync(
        string packagePath,
        MutationAdmissionLease? admissionLease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        XDocument document = LoadBoundedPackage(packagePath);
        XElement root = ValidatePackageRoot(document);
        ClashDataPackageScope scope = ParsePackageScope(root);
        IReadOnlyList<ImportFilePayload> files = BuildImportFilePayloads(
            root.Element("Files"),
            scope,
            cancellationToken);
        IReadOnlyDictionary<string, string> settings = ValidateSettings(root.Element("Settings"));
        return await BeginValidatedImportAsync(
            files,
            settings,
            admissionLease,
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<ImportFilePayload> BuildImportFilePayloads(
        XElement? filesElement,
        ClashDataPackageScope scope,
        CancellationToken cancellationToken)
    {
        List<ImportFilePayload> files = [];
        HashSet<string> targetPaths = new(StringComparer.OrdinalIgnoreCase);
        long decodedBytes = 0;
        int encounteredFileCount = 0;
        foreach (XElement fileElement in filesElement?.Elements("File") ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            encounteredFileCount++;
            if (encounteredFileCount > MaxPackageFileCount)
            {
                throw new InvalidDataException("Clash# data package contains too many file entries.");
            }

            string relativePath = fileElement.Attribute("Path")?.Value ?? string.Empty;
            string targetPath = ResolveImportFilePath(relativePath);
            if (scope != ClashDataPackageScope.SettingsAndProxyConfiguration
                || !IsProxyConfigurationImportPath(targetPath))
            {
                throw new InvalidDataException(
                    "Clash# data package contains a file outside its declared scope.");
            }

            if (IsTransactionInfrastructurePath(targetPath))
            {
                throw new InvalidDataException(
                    "Clash# data packages cannot target private transaction state.");
            }

            if (IsGeneratedRuntimeConfigPath(targetPath))
            {
                continue;
            }

            if (!targetPaths.Add(targetPath))
            {
                throw new InvalidDataException("Clash# data package contains duplicate file targets.");
            }

            string encodedContent = fileElement.Value;
            if (encodedContent.Length > ((MaxPackageEntryBytes + 2L) / 3L) * 4L + 4096L)
            {
                throw new InvalidDataException("Clash# data package file entry exceeds the size limit.");
            }

            byte[] content;
            try
            {
                content = Convert.FromBase64String(encodedContent);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Clash# data package contains invalid file content.", exception);
            }

            if (content.Length > MaxPackageEntryBytes
                || decodedBytes > MaxPackageDecodedBytes - content.Length)
            {
                throw new InvalidDataException("Clash# data package decoded file budget was exceeded.");
            }

            decodedBytes += content.Length;
            files.Add(new ImportFilePayload(targetPath, content));
        }

        return files;
    }

    private XElement ExportSettings()
    {
        return new XElement(
            "Settings",
            SettingDescriptors.Select(descriptor => new XElement(
                "Setting",
                new XAttribute("Name", descriptor.Name),
                new XAttribute("Value", descriptor.Normalize(descriptor.Read(_settings))))));
    }

    private async Task<XElement> ExportFilesAsync(string packagePath, ClashDataPackageScope scope, CancellationToken cancellationToken)
    {
        List<XElement> files = [];
        long totalBytes = 0;
        foreach (string filePath in EnumerateScopedFiles(scope, packagePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count >= MaxPackageFileCount)
            {
                throw new InvalidDataException("Clash# data package export contains too many files.");
            }

            long fileLength = new FileInfo(filePath).Length;
            if (fileLength > MaxPackageEntryBytes
                || totalBytes > MaxPackageDecodedBytes - fileLength)
            {
                throw new InvalidDataException("Clash# data package export exceeds the file budget.");
            }

            string relativePath = ToPackageRelativePath(filePath);
            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            if (bytes.Length > MaxPackageEntryBytes
                || totalBytes > MaxPackageDecodedBytes - bytes.Length)
            {
                throw new InvalidDataException(
                    "Clash# data package export changed while reading and exceeded the file budget.");
            }

            totalBytes += bytes.Length;
            files.Add(new XElement(
                "File",
                new XAttribute("Path", relativePath),
                Convert.ToBase64String(bytes)));
        }

        return new XElement("Files", files);
    }

    internal static XDocument LoadBoundedPackage(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        FileInfo package = new(Path.GetFullPath(packagePath));
        if (!package.Exists)
        {
            throw new FileNotFoundException("Clash# data package was not found.", package.FullName);
        }

        if (package.Length > MaxPackageBytes)
        {
            throw new InvalidDataException("Clash# data package exceeds the size limit.");
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPackageBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using XmlReader reader = XmlReader.Create(package.FullName, settings);
        return XDocument.Load(reader, LoadOptions.None);
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
            if ((File.GetAttributes(profileCatalogPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Clash# data package export cannot include a reparse-point profile catalog.");
            }

            yield return profileCatalogPath;
        }

        string mihomoDirectory = Path.Combine(_localDataDirectory, MihomoDirectoryName);
        if (!Directory.Exists(mihomoDirectory))
        {
            yield break;
        }

        foreach (string filePath in EnumerateFilesWithoutReparsePoints(mihomoDirectory))
        {
            if (!IsGeneratedRuntimeConfigPath(filePath))
            {
                yield return filePath;
            }
        }
    }

    /// <summary>Enumerates a managed tree without following file or directory reparse points.</summary>
    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string rootDirectory)
    {
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(Path.GetFullPath(rootDirectory));
        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Pop();
            if ((File.GetAttributes(currentDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Clash# data package export cannot traverse a directory reparse point.");
            }

            foreach (string filePath in Directory.EnumerateFiles(
                currentDirectory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Clash# data package export cannot include a file reparse point.");
                }

                yield return filePath;
            }

            foreach (string directoryPath in Directory.EnumerateDirectories(
                currentDirectory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Clash# data package export cannot traverse a directory reparse point.");
                }

                pendingDirectories.Push(directoryPath);
            }
        }
    }

    private void ImportSettings(
        IReadOnlyDictionary<string, string> values,
        MutationAdmissionLease? admissionLease)
    {
        WriteSettings(admissionLease, settings =>
        {
            foreach (SettingDescriptor descriptor in SettingDescriptors)
            {
                if (values.TryGetValue(descriptor.Name, out string? value))
                {
                    descriptor.Write(settings, value);
                }
            }
        });
    }

    private IReadOnlyDictionary<string, string> ValidateSettings(XElement? settingsElement)
    {
        if (settingsElement is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, SettingDescriptor> descriptors = SettingDescriptors.ToDictionary(
            static descriptor => descriptor.Name,
            StringComparer.Ordinal);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (XElement element in settingsElement.Elements("Setting"))
        {
            string? name = element.Attribute("Name")?.Value;
            if (name is null || !descriptors.TryGetValue(name, out SettingDescriptor descriptor))
            {
                continue;
            }

            string value = descriptor.Normalize(element.Attribute("Value")?.Value ?? string.Empty);
            if (!values.TryAdd(name, value))
            {
                throw new InvalidDataException(
                    $"Clash# data package contains duplicate setting '{name}'.");
            }
        }

        return values;
    }

    private Dictionary<string, string> CaptureSettings()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (SettingDescriptor descriptor in SettingDescriptors)
        {
            values[descriptor.Name] = descriptor.Read(_settings);
        }

        return values;
    }

    private void RestoreSettings(
        IReadOnlyDictionary<string, string> values,
        MutationAdmissionLease? admissionLease)
    {
        WriteSettings(admissionLease, settings =>
        {
            foreach (SettingDescriptor descriptor in SettingDescriptors)
            {
                if (values.TryGetValue(descriptor.Name, out string? value))
                {
                    descriptor.Write(settings, value);
                }
            }
        });
    }

    private void WriteSettings(
        MutationAdmissionLease? admissionLease,
        Action<IClashDataPackageSettings> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (admissionLease is null)
        {
            mutation(_settings);
            return;
        }

        IClashDataPackageAdmittedSettings admittedSettings = _admittedSettings
            ?? throw new InvalidOperationException(
                "The configured package settings store does not support admitted settings batches.");
        admittedSettings.WriteAdmitted(admissionLease, mutation);
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

    private static ClashDataPackageScope ParsePackageScope(XElement root)
    {
        string? scopeText = root.Attribute("Scope")?.Value;
        if (!Enum.TryParse(scopeText, ignoreCase: false, out ClashDataPackageScope scope)
            || !Enum.IsDefined(scope))
        {
            throw new InvalidDataException("Clash# data package scope is not supported.");
        }

        return scope;
    }

    private bool IsProxyConfigurationImportPath(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        string profileCatalogPath = Path.GetFullPath(Path.Combine(
            _localDataDirectory,
            ProfileCatalogFileName));
        string mihomoRoot = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(
            _localDataDirectory,
            MihomoDirectoryName)));
        return StringComparer.OrdinalIgnoreCase.Equals(normalizedPath, profileCatalogPath)
            || normalizedPath.StartsWith(mihomoRoot, StringComparison.OrdinalIgnoreCase);
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

        ValidateImportRelativePathSyntax(relativePath);
        string normalizedRelativePath = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string root = EnsureTrailingSeparator(Path.GetFullPath(_localDataDirectory));
        string targetPath = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
        if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Clash# data package contains an unsafe file path.");
        }

        EnsureImportPathHasNoReparsePoints(root, targetPath);
        return targetPath;
    }

    /// <summary>Rejects path aliases, alternate data streams, and Windows device names.</summary>
    private static void ValidateImportRelativePathSyntax(string relativePath)
    {
        char[] separators = ['/', '\\'];
        foreach (string segment in relativePath.Split(separators, StringSplitOptions.None))
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.Contains(':', StringComparison.Ordinal)
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("Clash# data package contains an unsafe file path.");
            }

            if (!OperatingSystem.IsWindows())
            {
                continue;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new InvalidDataException("Clash# data package contains an unsafe file path.");
            }

            string deviceName = segment.Split('.', 2)[0];
            bool isReservedDeviceName = deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || deviceName.Length == 4
                    && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                        || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && deviceName[3] is >= '1' and <= '9';
            if (isReservedDeviceName)
            {
                throw new InvalidDataException("Clash# data package contains an unsafe file path.");
            }
        }
    }

    /// <summary>Rejects existing junctions/symlinks below LocalData before staging an import target.</summary>
    private static void EnsureImportPathHasNoReparsePoints(string root, string targetPath)
    {
        string rootPath = Path.TrimEndingDirectorySeparator(root);
        string relativePath = Path.GetRelativePath(rootPath, targetPath);
        string currentPath = rootPath;
        foreach (string segment in relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Clash# data package import targets cannot traverse a reparse point.");
            }
        }
    }

    private bool IsGeneratedRuntimeConfigPath(string path)
    {
        string mihomoDirectoryPath = Path.Combine(
            _localDataDirectory,
            MihomoDirectoryName);
        string normalizedPath = Path.GetFullPath(path);
        string generatedRuntimeConfigPath = Path.Combine(
            mihomoDirectoryPath,
            GeneratedMihomoConfigFileName);
        string generatedRuntimeStatePath = Path.Combine(
            mihomoDirectoryPath,
            GeneratedMihomoStateFileName);
        string runtimeGenerationsRoot = EnsureTrailingSeparator(Path.Combine(
            mihomoDirectoryPath,
            RuntimeGenerationsDirectoryName));
        string fileName = Path.GetFileName(normalizedPath);
        bool isPrivateConfigurationSidecar =
            fileName.StartsWith("config.yaml.runtime-staging.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("config.yaml.runtime-backup.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("config.yaml.staging.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("config.yaml.backup.", StringComparison.OrdinalIgnoreCase);
        return string.Equals(normalizedPath, generatedRuntimeConfigPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, generatedRuntimeStatePath, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(runtimeGenerationsRoot, StringComparison.OrdinalIgnoreCase)
            || isPrivateConfigurationSidecar
            || string.Equals(
                Path.GetDirectoryName(normalizedPath),
                mihomoDirectoryPath,
                StringComparison.OrdinalIgnoreCase)
                && (fileName.StartsWith("config.yaml.restore.", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("config.runtime-state.json.tmp.", StringComparison.OrdinalIgnoreCase));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : $"{path}{Path.DirectorySeparatorChar}";
    }

    private static SettingDescriptor StringSetting(string name, Func<IClashDataPackageSettings, string> read, Action<IClashDataPackageSettings, string> write)
    {
        return new SettingDescriptor(name, read, write, static value => value);
    }

    private static SettingDescriptor RegisteredStringSetting(
        SettingKey key,
        Func<IClashDataPackageSettings, string> read,
        Action<IClashDataPackageSettings, string> write)
    {
        SettingDefinition definition = SettingsRegistry.Default.Get(key.Value);
        return new SettingDescriptor(
            key.Value,
            read,
            write,
            value =>
            {
                SettingNormalizationResult normalized = definition.Normalize(value);
                if (!normalized.IsSuccess)
                {
                    throw new InvalidDataException(
                        $"Clash# data package setting '{key.Value}' is invalid: {normalized.Error!.Code}.");
                }

                return normalized.Value!.CanonicalText;
            });
    }

    private static SettingDescriptor BoolSetting(string name, Func<IClashDataPackageSettings, bool> read, Action<IClashDataPackageSettings, bool> write)
    {
        return new SettingDescriptor(
            name,
            settings => read(settings).ToString(CultureInfo.InvariantCulture),
            (settings, value) => write(settings, bool.Parse(value)),
            value =>
            {
                _ = bool.Parse(value);
                return value;
            });
    }

    private static SettingDescriptor IntSetting(string name, Func<IClashDataPackageSettings, int> read, Action<IClashDataPackageSettings, int> write)
    {
        return new SettingDescriptor(
            name,
            settings => read(settings).ToString(CultureInfo.InvariantCulture),
            (settings, value) => write(settings, int.Parse(value, CultureInfo.InvariantCulture)),
            value =>
            {
                _ = int.Parse(value, CultureInfo.InvariantCulture);
                return value;
            });
    }

    private static SettingDescriptor RangedIntSetting(
        string name,
        Func<IClashDataPackageSettings, int> read,
        Action<IClashDataPackageSettings, int> write,
        int minimum,
        int maximum)
    {
        return new SettingDescriptor(
            name,
            settings => read(settings).ToString(CultureInfo.InvariantCulture),
            (settings, value) => write(settings, int.Parse(value, CultureInfo.InvariantCulture)),
            value =>
            {
                int parsed = int.Parse(value, CultureInfo.InvariantCulture);
                if (parsed < minimum || parsed > maximum)
                {
                    throw new ArgumentOutOfRangeException(name, $"Setting must be in the range [{minimum}, {maximum}].");
                }

                return value;
            });
    }

    private static SettingDescriptor EnumSetting<TEnum>(
        string name,
        Func<IClashDataPackageSettings, TEnum> read,
        Action<IClashDataPackageSettings, TEnum> write,
        Func<TEnum, bool>? isAllowed = null)
        where TEnum : struct, Enum
    {
        return new SettingDescriptor(
            name,
            settings => read(settings).ToString(),
            (settings, value) => write(settings, Enum.Parse<TEnum>(value)),
            value =>
            {
                if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed)
                    || !Enum.IsDefined(parsed)
                    || isAllowed is not null && !isAllowed(parsed))
                {
                    throw new InvalidDataException(
                        $"Clash# data package setting '{name}' is not a supported {typeof(TEnum).Name} value.");
                }

                return parsed.ToString();
            });
    }

    private readonly record struct SettingDescriptor(
        string Name,
        Func<IClashDataPackageSettings, string> Read,
        Action<IClashDataPackageSettings, string> Write,
        Func<string, string> Normalize);

    private readonly record struct ImportFilePayload(string TargetPath, byte[] Content);
}
