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

    /// <summary>Gets the private bearer secret for the Clash#-owned mihomo controller.</summary>
    string MihomoControllerSecret { get; }
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
/// Thread safety: Public filesystem reads and mutations serialize access through a private lock.
/// Side effects: Creates directories and writes the local mihomo configuration file.
/// </remarks>
public sealed partial class CoreConfigurationService
{
    /// <summary>Synchronization object guarding filesystem mutations for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Serializes complete import transactions by normalized profile path.</summary>
    private readonly ProfileImportGate _profileImportGate = new();

    /// <summary>Absolute directory path for mihomo runtime configuration.</summary>
    private readonly string _configurationDirectoryPath;

    /// <summary>Absolute file path for the generated mihomo configuration.</summary>
    private readonly string _configurationFilePath;

    private readonly ICoreConfigurationSettings _settings;

    private readonly ICoreConfigurationProfileMetrics _profileMetrics;

    private readonly ICoreConfigurationValidator _validator;

    private readonly Func<string, string> _getString;

    private readonly Func<string, string> _readAllText;

    private readonly Action<string, string, Encoding> _writeAllText;

    /// <summary>Initializes the configuration service and resolves configuration paths.</summary>
    internal CoreConfigurationService(
        string configurationDirectoryPath,
        ICoreConfigurationSettings settings,
        ICoreConfigurationProfileMetrics profileMetrics,
        ICoreConfigurationValidator validator,
        Func<string, string> getString)
        : this(
            configurationDirectoryPath,
            settings,
            profileMetrics,
            validator,
            getString,
            File.ReadAllText,
            File.WriteAllText)
    {
    }

    /// <summary>Initializes the configuration service with injectable text I/O boundaries.</summary>
    internal CoreConfigurationService(
        string configurationDirectoryPath,
        ICoreConfigurationSettings settings,
        ICoreConfigurationProfileMetrics profileMetrics,
        ICoreConfigurationValidator validator,
        Func<string, string> getString,
        Func<string, string> readAllText,
        Action<string, string, Encoding> writeAllText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectoryPath);

        _configurationDirectoryPath = Path.GetFullPath(configurationDirectoryPath);
        _configurationFilePath = Path.Combine(_configurationDirectoryPath, "config.yaml");
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _profileMetrics = profileMetrics ?? throw new ArgumentNullException(nameof(profileMetrics));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _readAllText = readAllText ?? throw new ArgumentNullException(nameof(readAllText));
        _writeAllText = writeAllText ?? throw new ArgumentNullException(nameof(writeAllText));
    }

    /// <summary>Gets the current local mihomo configuration state.</summary>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot for the managed configuration file.</returns>
    public CoreConfigurationState GetState()
    {
        lock (_syncLock)
        {
            return new CoreConfigurationState(
                _configurationDirectoryPath,
                _configurationFilePath,
                File.Exists(_configurationFilePath));
        }
    }

    /// <summary>Creates the built-in profile validation candidate without replacing the live runtime configuration.</summary>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot for the isolated validation candidate.</returns>
    public CoreConfigurationState EnsureDefaultConfiguration()
    {
        _runtimeConfigurationGate.Wait();
        try
        {
            lock (_syncLock)
            {
                string candidateDirectory = Path.Combine(
                    _configurationDirectoryPath,
                    "validation-candidates");
                string candidatePath = Path.Combine(candidateDirectory, "built-in-direct.yaml");
                Directory.CreateDirectory(candidateDirectory);
                string candidateText = BuildRuntimeConfiguration(
                    ProfileCatalogIds.BuiltInDirect,
                    _settings.MixedPort,
                    ClashSharpMode.Standby,
                    transparentProxyEnabled: false);
                _writeAllText(
                    candidatePath,
                    candidateText,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return new CoreConfigurationState(candidateDirectory, candidatePath, true);
            }
        }
        finally
        {
            _runtimeConfigurationGate.Release();
        }
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
        return EnsureConfiguration(mode, transparentProxyEnabled, _settings.MixedPort);
    }

    /// <summary>Ensures the managed configuration matches immutable planned mode, TUN, and port values.</summary>
    public CoreConfigurationState EnsureConfiguration(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort)
    {
        _runtimeConfigurationGate.Wait();
        try
        {
            lock (_syncLock)
            {
                Directory.CreateDirectory(_configurationDirectoryPath);

                string configText = BuildRuntimeConfiguration(mixedPort, mode, transparentProxyEnabled);
                _writeAllText(
                    _configurationFilePath,
                    configText,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                return GetState();
            }
        }
        finally
        {
            _runtimeConfigurationGate.Release();
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
        string transactionId = Guid.NewGuid().ToString("N");
        string stagingPath = Path.Combine(
            profileDirectory,
            $"config.yaml.staging.{transactionId}");
        string backupPath = Path.Combine(
            profileDirectory,
            $"config.yaml.backup.{transactionId}");
        int nodeCount = _profileMetrics.CountNodes(normalizedText);
        int ruleCount = _profileMetrics.CountRules(normalizedText);

        using IDisposable transactionLease = await _profileImportGate
            .EnterAsync(normalizedProfileId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool hadCommittedConfiguration = false;
        bool commitAttempted = false;
        try
        {
            lock (_syncLock)
            {
                Directory.CreateDirectory(profileDirectory);
                hadCommittedConfiguration = File.Exists(profileConfigPath);
                if (hadCommittedConfiguration)
                {
                    File.Copy(profileConfigPath, backupPath, overwrite: false);
                }

                _writeAllText(
                    stagingPath,
                    normalizedText,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            await _validator
                .ValidateAsync(profileDirectory, stagingPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_syncLock)
            {
                commitAttempted = true;
                File.Move(stagingPath, profileConfigPath, overwrite: true);
                DeleteFileIfPresent(backupPath);
            }
        }
        catch (Exception importFailure)
        {
            Exception? rollbackFailure = TryRollbackProfileImport(
                profileConfigPath,
                stagingPath,
                backupPath,
                hadCommittedConfiguration,
                commitAttempted);
            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Profile import failed and its private transaction could not be rolled back.",
                    importFailure,
                    rollbackFailure);
            }

            throw;
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

    /// <summary>Tries to read an imported profile configuration as one complete text snapshot.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null.</param>
    /// <param name="configurationText">Configuration text when the profile exists; otherwise null.</param>
    /// <returns>True when the profile configuration exists; otherwise false.</returns>
    public bool TryReadProfileConfigurationText(string profileId, out string? configurationText)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        lock (_syncLock)
        {
            string profileConfigPath = GetProfileConfigurationPath(profileId);
            if (!File.Exists(profileConfigPath))
            {
                configurationText = null;
                return false;
            }

            configurationText = _readAllText(profileConfigPath);
            return true;
        }
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

        using IDisposable transactionLease = await _profileImportGate
            .EnterAsync(normalizedProfileId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>Builds runtime configuration from the selected imported or built-in profile.</summary>
    /// <param name="mixedPort">Mixed HTTP and SOCKS proxy port in range [1, 65535].</param>
    /// <param name="mode">Master takeover mode whose equivalent mihomo mode should be emitted.</param>
    /// <param name="transparentProxyEnabled">Whether the generated runtime should include TUN ownership settings.</param>
    /// <returns>Runtime configuration text with deterministic line endings.</returns>
    private string BuildRuntimeConfiguration(int mixedPort, ClashSharpMode mode, bool transparentProxyEnabled)
    {
        return BuildRuntimeConfiguration(
            _settings.ActiveProfileId,
            mixedPort,
            mode,
            transparentProxyEnabled);
    }

    /// <summary>Builds runtime configuration for an explicit desired profile without mutating active-profile settings.</summary>
    private string BuildRuntimeConfiguration(
        string profileId,
        int mixedPort,
        ClashSharpMode mode,
        bool transparentProxyEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        if (StringComparer.Ordinal.Equals(profileId, ProfileCatalogIds.BuiltInDirect))
        {
            return MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
                mixedPort,
                mode,
                transparentProxyEnabled,
                _settings.MihomoControllerSecret);
        }

        string profileConfigPath = GetProfileConfigurationPath(profileId);
        if (!File.Exists(profileConfigPath))
        {
            throw new FileNotFoundException(
                "The selected imported profile configuration was not found.",
                profileConfigPath);
        }

        string profileText = _readAllText(profileConfigPath);
        return MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            profileText,
            mixedPort,
            mode,
            transparentProxyEnabled,
            _settings.MihomoControllerSecret);
    }

    /// <summary>Restores the previous committed configuration and removes only this transaction's sidecars.</summary>
    private Exception? TryRollbackProfileImport(
        string profileConfigPath,
        string stagingPath,
        string backupPath,
        bool hadCommittedConfiguration,
        bool commitAttempted)
    {
        ArgumentNullException.ThrowIfNull(profileConfigPath);
        ArgumentNullException.ThrowIfNull(stagingPath);
        ArgumentNullException.ThrowIfNull(backupPath);

        try
        {
            lock (_syncLock)
            {
                if (commitAttempted)
                {
                    if (hadCommittedConfiguration)
                    {
                        File.Copy(backupPath, profileConfigPath, overwrite: true);
                    }
                    else
                    {
                        DeleteFileIfPresent(profileConfigPath);
                    }
                }

                DeleteFileIfPresent(stagingPath);
                DeleteFileIfPresent(backupPath);
            }

            return null;
        }
        catch (Exception rollbackFailure)
        {
            return rollbackFailure;
        }
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
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
