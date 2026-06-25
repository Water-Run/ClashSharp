/*
 * Core Configuration Service
 * Provides local mihomo configuration directory management and default configuration generation
 *
 * @author: WaterRun
 * @file: Service/CoreConfigurationService.cs
 * @date: 2026-06-17
 */

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides settings used when generating runtime mihomo configuration.</summary>
internal interface ICoreConfigurationSettings
{
    /// <summary>Gets whether transparent proxy is preferred for active takeover modes.</summary>
    bool TransparentProxyEnabled { get; }

    /// <summary>Gets the configured mixed proxy port.</summary>
    int MixedPort { get; }

    /// <summary>Gets the active profile identifier.</summary>
    string ActiveProfileId { get; }
}

/// <summary>Counts profile preview rows from configuration text.</summary>
internal interface ICoreConfigurationProfileMetrics
{
    /// <summary>Counts proxy node preview rows.</summary>
    int CountNodes(string configurationText);

    /// <summary>Counts rule preview rows.</summary>
    int CountRules(string configurationText);
}

/// <summary>Validates mihomo configuration files before import results are committed.</summary>
internal interface ICoreConfigurationValidator
{
    /// <summary>Validates <paramref name="configurationPath"/> using <paramref name="workingDirectory"/>.</summary>
    Task ValidateAsync(string workingDirectory, string configurationPath, CancellationToken cancellationToken);
}

/// <summary>Manages local mihomo configuration paths and default configuration generation.</summary>
/// <remarks>
/// Invariants: The configuration directory is created before a default configuration is written.
/// Thread safety: Public mutation methods serialize filesystem access through a private lock.
/// Side effects: Creates directories and writes the local mihomo configuration file.
/// </remarks>
public sealed partial class CoreConfigurationService
{
    /// <summary>Synchronization object guarding filesystem mutations for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Absolute directory path for mihomo runtime configuration.</summary>
    private readonly string _configurationDirectoryPath;

    /// <summary>Absolute file path for the generated mihomo configuration.</summary>
    private readonly string _configurationFilePath;

    private readonly ICoreConfigurationSettings _settings;

    private readonly ICoreConfigurationProfileMetrics _profileMetrics;

    private readonly ICoreConfigurationValidator _validator;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes the configuration service and resolves configuration paths.</summary>
    internal CoreConfigurationService(
        string configurationDirectoryPath,
        ICoreConfigurationSettings settings,
        ICoreConfigurationProfileMetrics profileMetrics,
        ICoreConfigurationValidator validator,
        Func<string, string> getString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectoryPath);

        _configurationDirectoryPath = Path.GetFullPath(configurationDirectoryPath);
        _configurationFilePath = Path.Combine(_configurationDirectoryPath, "config.yaml");
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _profileMetrics = profileMetrics ?? throw new ArgumentNullException(nameof(profileMetrics));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Gets the current local mihomo configuration state.</summary>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot for the managed configuration file.</returns>
    public CoreConfigurationState GetState()
    {
        return new CoreConfigurationState(
            _configurationDirectoryPath,
            _configurationFilePath,
            File.Exists(_configurationFilePath));
    }

    /// <summary>Ensures the local configuration directory and default configuration file exist.</summary>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot after the ensure operation completes.</returns>
    public CoreConfigurationState EnsureDefaultConfiguration()
    {
        return EnsureConfiguration(ClashSharpMode.Standby);
    }

    /// <summary>Ensures the local configuration directory and managed configuration file match <paramref name="mode"/>.</summary>
    /// <param name="mode">Master takeover mode whose mihomo mode should be represented in the generated configuration.</param>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot after the ensure operation completes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> cannot be mapped to a mihomo mode.</exception>
    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode)
    {
        return EnsureConfiguration(
            mode,
            MihomoRuntimeConfigurationBuilder.ShouldEnableTransparentProxy(mode, _settings.TransparentProxyEnabled));
    }

    /// <summary>Ensures the local configuration directory and managed configuration file match <paramref name="mode"/> and transparent proxy preference.</summary>
    /// <param name="mode">Master takeover mode whose mihomo mode should be represented in the generated configuration.</param>
    /// <param name="transparentProxyEnabled">True to enable mihomo TUN transparent proxy configuration.</param>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot after the ensure operation completes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> cannot be mapped to a mihomo mode.</exception>
    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled)
    {
        lock (_syncLock)
        {
            Directory.CreateDirectory(_configurationDirectoryPath);

            string configText = BuildRuntimeConfiguration(_settings.MixedPort, mode, transparentProxyEnabled);
            File.WriteAllText(_configurationFilePath, configText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return GetState();
        }
    }

    /// <summary>Downloads have already completed; validates and imports profile configuration text into the managed profile store.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null or whitespace.</param>
    /// <param name="profileName">User-facing profile name. Must not be null or whitespace.</param>
    /// <param name="configurationText">Downloaded mihomo configuration text. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels external mihomo validation.</param>
    /// <returns>Import result containing profile path and estimated counts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileId"/>, <paramref name="profileName"/>, or <paramref name="configurationText"/> is null.</exception>
    /// <exception cref="ArgumentException">A required argument is whitespace or the configuration does not look like a mihomo profile.</exception>
    /// <exception cref="InvalidOperationException">Bundled mihomo rejects the imported configuration.</exception>
    public async Task<ProfileImportResult> ImportProfileConfigurationAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(profileName);
        ArgumentNullException.ThrowIfNull(configurationText);

        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile identifier must not be whitespace.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("Profile name must not be whitespace.", nameof(profileName));
        }

        if (string.IsNullOrWhiteSpace(configurationText))
        {
            throw new ArgumentException("Profile configuration must not be whitespace.", nameof(configurationText));
        }

        string normalizedProfileId = NormalizeProfileId(profileId);
        string normalizedText = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(configurationText);
        MihomoProfileShapeValidator.Validate(normalizedText);

        string profileDirectory = GetProfileDirectoryPath(normalizedProfileId);
        string profileConfigPath = Path.Combine(profileDirectory, "config.yaml");
        string backupPath = Path.Combine(profileDirectory, "config.yaml.bak");
        int nodeCount = _profileMetrics.CountNodes(normalizedText);
        int ruleCount = _profileMetrics.CountRules(normalizedText);

        lock (_syncLock)
        {
            Directory.CreateDirectory(profileDirectory);
            if (File.Exists(profileConfigPath))
            {
                File.Copy(profileConfigPath, backupPath, overwrite: true);
            }

            File.WriteAllText(profileConfigPath, normalizedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        try
        {
            await _validator.ValidateAsync(profileDirectory, profileConfigPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RestoreProfileBackup(profileConfigPath, backupPath);
            throw;
        }

        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        return new ProfileImportResult(
            normalizedProfileId,
            profileName.Trim(),
            profileConfigPath,
            nodeCount,
            ruleCount,
            GetString("CoreConfiguration.Imported"));
    }

    /// <summary>Returns the imported profile configuration path for <paramref name="profileId"/>.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null.</param>
    /// <returns>Absolute imported profile configuration path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileId"/> is null.</exception>
    public string GetProfileConfigurationPath(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return Path.Combine(GetProfileDirectoryPath(NormalizeProfileId(profileId)), "config.yaml");
    }

    /// <summary>Validates an already-imported profile configuration with the bundled mihomo binary when available.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels external mihomo validation.</param>
    /// <returns>Import-style profile metrics for the validated configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="profileId"/> is whitespace or the configuration shape is invalid.</exception>
    /// <exception cref="FileNotFoundException">The imported profile configuration file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Bundled mihomo rejects the imported configuration.</exception>
    public async Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile identifier must not be whitespace.", nameof(profileId));
        }

        string normalizedProfileId = NormalizeProfileId(profileId);
        string profileDirectory = GetProfileDirectoryPath(normalizedProfileId);
        string profileConfigPath = Path.Combine(profileDirectory, "config.yaml");
        if (!File.Exists(profileConfigPath))
        {
            throw new FileNotFoundException("Imported profile configuration was not found.", profileConfigPath);
        }

        string configurationText = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(
            await File.ReadAllTextAsync(profileConfigPath, cancellationToken).ConfigureAwait(false));
        MihomoProfileShapeValidator.Validate(configurationText);
        await _validator.ValidateAsync(profileDirectory, profileConfigPath, cancellationToken).ConfigureAwait(false);

        return new ProfileImportResult(
            normalizedProfileId,
            normalizedProfileId,
            profileConfigPath,
            _profileMetrics.CountNodes(configurationText),
            _profileMetrics.CountRules(configurationText),
            GetString("CoreConfiguration.Validated"));
    }

    /// <summary>Builds runtime configuration from the active imported profile when available, otherwise from the default profile.</summary>
    /// <param name="mixedPort">Mixed HTTP and SOCKS proxy port in range [1, 65535].</param>
    /// <param name="mode">Master takeover mode whose equivalent mihomo mode should be emitted.</param>
    /// <returns>Runtime configuration text with deterministic line endings.</returns>
    private string BuildRuntimeConfiguration(int mixedPort, ClashSharpMode mode, bool transparentProxyEnabled)
    {
        string activeProfileId = _settings.ActiveProfileId;
        string profileConfigPath = GetProfileConfigurationPath(activeProfileId);
        if (File.Exists(profileConfigPath) && !StringComparer.Ordinal.Equals(activeProfileId, ProfileCatalogIds.BuiltInDirect))
        {
            string profileText = File.ReadAllText(profileConfigPath);
            return MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(profileText, mixedPort, mode, transparentProxyEnabled);
        }

        return MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(mixedPort, mode, transparentProxyEnabled);
    }

    /// <summary>Restores the previous imported profile configuration after failed validation.</summary>
    /// <param name="profileConfigPath">Current profile configuration path. Must not be null.</param>
    /// <param name="backupPath">Backup path. Must not be null.</param>
    private static void RestoreProfileBackup(string profileConfigPath, string backupPath)
    {
        ArgumentNullException.ThrowIfNull(profileConfigPath);
        ArgumentNullException.ThrowIfNull(backupPath);

        if (File.Exists(backupPath))
        {
            File.Copy(backupPath, profileConfigPath, overwrite: true);
            File.Delete(backupPath);
            return;
        }

        if (File.Exists(profileConfigPath))
        {
            File.Delete(profileConfigPath);
        }
    }

    /// <summary>Normalizes a profile identifier so it is safe for a local directory name.</summary>
    /// <param name="profileId">Profile identifier. Must not be null.</param>
    /// <returns>Filesystem-safe profile identifier; never null.</returns>
    private static string NormalizeProfileId(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        StringBuilder builder = new();
        foreach (char character in profileId.Trim())
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.Length == 0 ? "profile" : builder.ToString();
    }

    /// <summary>Returns the profile directory for <paramref name="profileId"/>.</summary>
    /// <param name="profileId">Filesystem-safe profile identifier. Must not be null.</param>
    /// <returns>Absolute profile directory path.</returns>
    private string GetProfileDirectoryPath(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return Path.Combine(_configurationDirectoryPath, "profiles", profileId);
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}
